using System.Buffers.Binary;
using System.Text;

namespace YEEYEEYEE.Desktop;

/// <summary>拼接结果。<c>Note</c> 是**如实说明**：做了哪些取舍、丢了什么，成功也要说。</summary>
public sealed record Mp4ConcatResult(bool Ok, string FilePath, string Error, string Note);

/// <summary>
/// 把若干个 mp4 **无损**接成一个（不重编码：样本字节原样搬过去，只重排容器里的表）。
///
/// 为什么要有它：分镜是逐段出的，成片要把它们接起来；而引一堆 ffmpeg 进来是另一笔账
/// （体积、授权、随包带运行时）。这一段只做容器层面的拼接，没有外部依赖。
///
/// **支持哪种形状就写清，其余一律如实拒绝**——接错的容器比接不了更坏：能播，但音画慢慢跑偏。
/// 现在只认实测到的那一种（我们这条流水线出的段都是它）：
///   · 顶层 ftyp + moov + mdat，单个 mdat；没有 moof（分片 mp4 不支持）；
///   · 每条轨道都用 32 位 stco（co64 不支持）、编辑列表最多一条。
///
/// 写出来的结构是「照抄源、只换样本表」：mdia 里除 stbl 之外的东西（视频的 vmhd、音频的 smhd、
/// 共有的 dinf）一律原样搬过去。第一版想手工重建 minf，结果**把音频的 smhd 漏掉了**——
/// 那种文件播放器直接不认。抄结构比重建结构安全。
/// </summary>
public static class Mp4Concatenator
{
    /// <summary>
    /// 每段开头要丢几个样本，才算「无损对齐」。
    ///
    /// 实测（我们那条流水线出的每一段都长这样）：两条轨道都带 <c>elst.media_time=1024</c>，但含义不同——
    ///   · **视频轨有 <c>ctts</c>，首条正好是 <c>1x1024</c>**，与 media_time 相等：那是解码/合成偏移，
    ///     不是剪头，**一个样本都不能丢**（丢了等于把开头两帧真画面扔掉）；
    ///   · **音频轨没有 ctts**，而 1024@32k = 32ms 正好是一帧 AAC：那是编码器的预听样本，**要丢一帧**。
    ///
    /// 按这条算，视频每段 124×512/12288 = 5.16667s，音频剪掉一帧后 (166358−1024)/32000 = 5.16669s，
    /// 两者相等。**不剪的话音轨每段多 32ms，六段下来音频迟到 160ms**——这正是「拼接后音画慢慢不同步」
    /// 的常见成因，而没人会想到是容器表的问题。
    /// </summary>
    private static int PrimingSamplesToDrop(Track track)
    {
        if (track.HasCompositionOffsets) return 0;      // 有 ctts：media_time 是合成偏移，不剪
        if (track.EditMediaTime <= 0) return 0;

        var remaining = track.EditMediaTime;
        var dropped = 0;
        foreach (var sample in track.Samples)
        {
            if (remaining <= 0) break;
            remaining -= sample.Duration;
            dropped++;
        }
        // 编辑列表说的起点超过了整轨长度：这份文件本身就不正常，别剪，交给播放器。
        return remaining <= 0 ? dropped : 0;
    }

    public static Mp4ConcatResult Concat(IReadOnlyList<string> inputs, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return Fail("没有可拼的段。");
        if (inputs.Count == 1) return Fail("只有一段，不需要拼。");

        var files = new List<Mp4File>();
        for (var index = 0; index < inputs.Count; index++)
        {
            var path = inputs[index];
            if (!File.Exists(path)) return Fail($"第 {index + 1} 段找不到文件：{Path.GetFileName(path)}");
            try { files.Add(Mp4File.Parse(path)); }
            catch (Exception error)
            {
                return Fail($"第 {index + 1} 段读不动（{Path.GetFileName(path)}）：{error.Message}");
            }
        }

        var first = files[0];
        for (var index = 1; index < files.Count; index++)
        {
            var mismatch = Compare(first, files[index], index);
            if (mismatch.Length > 0) return Fail(mismatch);
        }

        try
        {
            return Write(files, outputPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail("写成品失败：" + error.Message);
        }
    }

    /// <summary>
    /// 采样条目里**参与解码**的那部分：把「随内容变、但不影响解码」的码率提示抹平后再比。
    ///
    /// 为什么必须这么做：实测同一轮出的两段，采样条目 182 字节里有 3 个字节不同，
    /// 位置在尾部那个 <c>btrt</c>（码率提示盒）里——<c>00 11 01 04</c>(1114372) vs <c>00 13 C4 2E</c>(1295406)，
    /// 就是**各自的实际码率**；音频的 <c>esds</c> 里同样有 11 个字节
    /// （<c>bufferSizeDB / maxBitrate / avgBitrate</c>）逐段不同，连 <c>btrt</c> 里也重复了一份。
    ///
    /// 这些字段一个都不影响能不能解码（解码看的是 avcC 里的 SPS/PPS、esds 里的 AudioSpecificConfig）。
    /// 拿整段字节比，会把「同一份工作流、同一档设置、内容不同」的正常段误判成不一致——
    /// 那就等于这个功能永远用不了。抹平的是**提示值**，编码配置一个字节都没放过。
    /// </summary>
    private static byte[] DecoderRelevant(byte[] entry)
    {
        var copy = (byte[])entry.Clone();

        // 1) 条目里的 btrt 盒（视频与音频都有）整段清零：长度留着，省得改结构。
        for (var i = 8; i + 4 <= copy.Length; i++)
        {
            if (copy[i] != (byte)'b' || copy[i + 1] != (byte)'t' || copy[i + 2] != (byte)'r' || copy[i + 3] != (byte)'t') continue;
            var size = BinaryPrimitives.ReadInt32BigEndian(copy.AsSpan(i - 4, 4));
            if (size >= 8 && i - 4 + size <= copy.Length) Array.Clear(copy, i - 4, size);
        }

        // 2) esds 的 DecoderConfigDescriptor：objectTypeIndication / streamType 之后 11 个字节
        //    就是 bufferSizeDB(3) + maxBitrate(4) + avgBitrate(4)，同样清零。
        var esds = IndexOf(copy, "esds");
        if (esds >= 4) ClearEsdsBitrates(copy, esds);
        return copy;
    }

    private static void ClearEsdsBitrates(byte[] copy, int esds)
    {
        // esds 的类型字段在 esds 处，盒头前 4 字节是长度；payload 先有 version/flags(4)，之后是描述符链。
        var cursor = esds + 4 + 4;
        var es = FindDescriptor(copy, cursor, 0x03);
        if (es < 0) return;
        // ES_Descriptor 的内容是 ES_ID(2) + flags(1)，之后才是嵌套描述符。
        // flags 非 0 时后面还挂着 dependsOn_ES_ID / URL 之类的字段，那种形状认不准就不动它——
        // 宁可退回「按原样比」这种更严的判断，也不要瞎抹。
        if (es + 3 > copy.Length || copy[es + 2] != 0) return;
        var config = FindDescriptor(copy, es + 3, 0x04);
        if (config < 0) return;
        // DecoderConfigDescriptor 的内容：objectTypeIndication(1) + streamType(1) + 要抹的那 11 个字节。
        if (config + 13 <= copy.Length) Array.Clear(copy, config + 2, 11);
    }

    /// <summary>
    /// 在描述符链里找某个 tag，返回它**内容**的起点。
    ///
    /// 长度字段的写法有两种（都遇到过）：单字节 <c>0x17</c>，以及展开式 <c>80 80 80 17</c>——
    /// 后者是「凡是高位为 1 的都是长度的一部分，直到某个高位为 0 的字节收尾」。
    /// 第一版按 ISO 那句「低位 7 位是后续字节数」去读 <c>80</c>，读出「后面还有 0 个字节」，
    /// 直接把整段抹平逻辑跳过了——所以音频那条一直判成「不一致」。
    /// </summary>
    private static int FindDescriptor(byte[] data, int start, byte tag)
    {
        var cursor = start;
        while (cursor < data.Length)
        {
            if (data[cursor] == tag)
            {
                var probe = cursor + 1;
                while (probe < data.Length && (data[probe] & 0x80) != 0) probe++;
                probe++;                       // 越过最后一个（高位为 0 的）长度字节
                return probe <= data.Length ? probe : -1;
            }
            cursor++;
        }
        return -1;
    }

    private static int IndexOf(byte[] data, string ascii)
    {
        for (var i = 0; i + ascii.Length <= data.Length; i++)
        {
            var found = true;
            for (var j = 0; j < ascii.Length; j++)
                if (data[i + j] != (byte)ascii[j]) { found = false; break; }
            if (found) return i;
        }
        return -1;
    }

    private static string Compare(Mp4File first, Mp4File other, int index)
    {
        var label = $"第 {index + 1} 段";
        if (other.Tracks.Count != first.Tracks.Count)
            return $"{label}的轨道数和第 1 段不一样（{other.Tracks.Count} vs {first.Tracks.Count}），拼不了。";
        if (other.Timescale != first.Timescale)
            return $"{label}的时基和第 1 段不一样（{other.Timescale} vs {first.Timescale}），拼不了。";
        for (var t = 0; t < first.Tracks.Count; t++)
        {
            var a = first.Tracks[t];
            var b = other.Tracks[t];
            if (a.Handler != b.Handler)
                return $"{label}第 {t + 1} 条轨道是 {b.Handler}，第 1 段那条是 {a.Handler}，拼不了。";
            if (a.Timescale != b.Timescale)
                return $"{label}的 {b.Handler} 轨时间刻度和第 1 段不一样（{b.Timescale} vs {a.Timescale}），"
                    + "接起来时长会算错，拼不了。";
            if (!DecoderRelevant(a.SampleEntry).SequenceEqual(DecoderRelevant(b.SampleEntry)))
                return $"{label}的 {b.Handler} 轨编码参数和第 1 段不一致（分辨率、编码配置、音频采样率这类）。"
                    + "无损拼接要求各段完全一致——这一段得重新出一次（用同一份工作流 / 同一个池子）再拼。";
        }
        return string.Empty;
    }

    private static Mp4ConcatResult Write(List<Mp4File> files, string outputPath)
    {
        var movieTimescale = files[0].Timescale;
        var head = files[0];

        // ── 1. 按轨道把各段的样本并起来，并算出每段占多长 ──────────────────────
        var merged = head.Tracks.Select(_ => new List<Sample>()).ToList();
        var segmentDurations = new List<ulong>();
        var primingDropped = 0;

        foreach (var file in files)
        {
            ulong longest = 0;
            for (var t = 0; t < file.Tracks.Count; t++)
            {
                var track = file.Tracks[t];
                var skip = PrimingSamplesToDrop(track);
                primingDropped += skip;
                var kept = track.Samples.Skip(skip).ToList();
                var duration = (ulong)kept.Sum(sample => (long)sample.Duration);
                var inMovieTime = (ulong)Math.Round(duration * (double)movieTimescale / track.Timescale);
                if (inMovieTime > longest) longest = inMovieTime;

                var isFirstOfSegment = true;
                foreach (var sample in kept)
                {
                    // 每段第一帧原来是关键帧的话，接缝处必须仍然算关键帧——
                    // 否则从接缝往前跳会解不出来（播放器跳过去只看到花屏）。
                    merged[t].Add(sample with { IsSync = sample.IsSync || isFirstOfSegment });
                    isFirstOfSegment = false;
                }
            }
            segmentDurations.Add(longest);
        }

        var totalDuration = segmentDurations.Aggregate(0UL, (a, b) => a + b);

        // ── 2. 布局：ftyp + moov + mdat；样本按「段 → 轨道」顺序连续摆 ──────────
        // 保住「段」这个粒度（每段都自带关键帧），比按轨道把所有音频堆到文件末尾好：
        // 那样播放器在整段里要不停来回跳。
        var mdatPayload = new MemoryStream();
        var chunks = new List<(int Track, long Offset, int Count)>();
        var running = 0L;
        foreach (var file in files)
        {
            for (var t = 0; t < file.Tracks.Count; t++)
            {
                var track = file.Tracks[t];
                var kept = track.Samples.Skip(PrimingSamplesToDrop(track)).ToList();
                if (kept.Count == 0) continue;
                chunks.Add((t, running, kept.Count));
                foreach (var sample in kept)
                {
                    mdatPayload.Write(track.Data, (int)sample.Offset, sample.Size);
                    running += sample.Size;
                }
            }
        }
        var mdatBytes = mdatPayload.ToArray();

        // moov 的长度取决于样本表，而样本表里的 chunk 偏移取决于 moov 有多长 —— 先写一遍量长度，
        // 再带着正确偏移写第二遍。两遍的 stco 条数一样，所以长度相等。
        var measuring = BuildMoov(head, merged, chunks, totalDuration, movieTimescale);
        var headerLength = head.FtypBytes.Length + measuring.Length + 8;
        var fixedChunks = chunks.Select(chunk => (chunk.Track, chunk.Offset + headerLength, chunk.Count)).ToList();
        var moov = BuildMoov(head, merged, fixedChunks, totalDuration, movieTimescale);

        using (var output = File.Create(outputPath))
        {
            output.Write(head.FtypBytes);
            output.Write(moov);
            output.Write(BoxHeader("mdat", mdatBytes.Length + 8));
            output.Write(mdatBytes);
        }

        var note = $"把 {files.Count} 段接成一段：{totalDuration / (double)movieTimescale:0.##}s、"
            + $"{mdatBytes.Length / 1024 / 1024.0:0.#} MB；画面与音频的样本字节原样搬运，没有重编码。"
            + (primingDropped > 0
                ? $"接缝对齐：视频不剪（它的 elst.media_time 是合成偏移），音频丢掉了 {primingDropped} 个预听样本——"
                  + "不丢的话音轨会逐段迟到，越拼越不同步。"
                : "接缝对齐：各段都没有需要丢的预听样本。")
            + "每条轨道的「样本组」（sgpd/sbgp）没有保留：那是可选的附加分组，合并会打乱样本下标，"
            + "丢掉不影响播放。";
        return new Mp4ConcatResult(true, outputPath, string.Empty, note);
    }

    private static byte[] BuildMoov(
        Mp4File head, List<List<Sample>> merged, List<(int Track, long Offset, int Count)> chunks,
        ulong totalDuration, uint movieTimescale)
    {
        var payload = new MemoryStream();
        payload.Write(BuildMvhd(movieTimescale, totalDuration, head.NextTrackId));
        for (var t = 0; t < head.Tracks.Count; t++)
        {
            if (merged[t].Count == 0) continue;
            payload.Write(BuildTrak(
                head.Tracks[t], merged[t],
                chunks.Where(chunk => chunk.Track == t).ToList(),
                totalDuration, movieTimescale));
        }
        return Box("moov", payload.ToArray());
    }

    /// <summary>mvhd（version 0）：字段位置见 ISO/IEC 14496-12；写错任何一个都会让播放器认不出时长。</summary>
    private static byte[] BuildMvhd(uint timescale, ulong duration, uint nextTrackId)
    {
        var payload = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(12, 4), timescale);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(16, 4), (uint)Math.Min(duration, uint.MaxValue));
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(20, 4), 0x00010000);       // rate 1.0
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(24, 2), 0x0100);           // volume 1.0
        // matrix：单位矩阵（36 字节，从 36 起）
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(36, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(52, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(68, 4), 0x40000000);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(96, 4), nextTrackId);
        return Box("mvhd", payload);
    }

    private static byte[] BuildTrak(Track track, List<Sample> samples, List<(int Track, long Offset, int Count)> chunks,
        ulong totalDuration, uint movieTimescale)
    {
        var payload = new MemoryStream();
        foreach (var child in track.BodyChildren)
        {
            switch (child.Type)
            {
                case "tkhd":
                    payload.Write(Box("tkhd", PatchDuration(track.TkhdPayload, track, totalDuration, movieTimescale, isTkhd: true)));
                    break;
                case "edts":
                    // 统一改成「从头播满」：每段的预听样本已经在并表时丢掉了，
                    // 再保留原来的 media_time 会多剪一次。
                    payload.Write(BuildEdts(totalDuration));
                    break;
                case "mdia":
                    payload.Write(Box("mdia", BuildMdia(track, samples, chunks)));
                    break;
                default:
                    payload.Write(child.Bytes);
                    break;
            }
        }
        return Box("trak", payload.ToArray());
    }

    private static byte[] BuildMdia(Track track, List<Sample> samples, List<(int Track, long Offset, int Count)> chunks)
    {
        var mediaDuration = (ulong)samples.Sum(sample => (long)sample.Duration);
        var payload = new MemoryStream();
        foreach (var child in track.MdiaChildren)
        {
            if (child.Type == "mdhd")
                payload.Write(Box("mdhd", PatchDuration(track.MdhdPayload, track, mediaDuration, track.Timescale, isTkhd: false)));
            else if (child.Type == "minf")
                payload.Write(Box("minf", BuildMinf(track, samples, chunks)));
            else
                payload.Write(child.Bytes);
        }
        return payload.ToArray();
    }

    /// <summary>minf：除 stbl 之外全部照抄——**视频的 vmhd、音频的 smhd、共有的 dinf 都在这里**，
    /// 漏掉 smhd 的音轨播放器直接不认。</summary>
    private static byte[] BuildMinf(Track track, List<Sample> samples, List<(int Track, long Offset, int Count)> chunks)
    {
        var payload = new MemoryStream();
        foreach (var child in track.MinfChildren)
        {
            if (child.Type == "stbl") payload.Write(Box("stbl", BuildStbl(track, samples, chunks)));
            else payload.Write(child.Bytes);
        }
        return payload.ToArray();
    }

    private static byte[] BuildStbl(Track track, List<Sample> samples, List<(int Track, long Offset, int Count)> chunks)
    {
        var payload = new MemoryStream();
        payload.Write(Box("stsd", track.StsdPayload));
        payload.Write(Box("stts", BuildStts(samples)));
        if (track.HasCompositionOffsets) payload.Write(Box("ctts", BuildCtts(samples)));
        if (track.HasSyncTable) payload.Write(Box("stss", BuildStss(samples)));
        payload.Write(Box("stsc", BuildStsc(chunks)));
        payload.Write(Box("stsz", BuildStsz(samples)));
        payload.Write(Box("stco", BuildStco(chunks)));
        return payload.ToArray();
    }

    private static byte[] BuildEdts(ulong movieDuration)
    {
        var elst = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(4, 4), 1);                       // entry_count
        BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(8, 4), (uint)Math.Min(movieDuration, uint.MaxValue));
        BinaryPrimitives.WriteInt32BigEndian(elst.AsSpan(12, 4), 0);                       // media_time = 0
        BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(16, 4), 0x00010000);             // rate 1.0
        var payload = new MemoryStream();
        payload.Write(Box("elst", elst));
        return Box("edts", payload.ToArray());
    }

    /// <summary>改时长。tkhd 的时长以**影片时基**计，mdhd 的以**自身时基**计——这两处混了就会时长错。</summary>
    private static byte[] PatchDuration(byte[] original, Track track, ulong duration, uint timescale, bool isTkhd)
    {
        var payload = (byte[])original.Clone();
        var version = payload[0];
        var value = isTkhd && track.Timescale != timescale
            ? (ulong)Math.Round(duration * (double)timescale / track.Timescale)
            : duration;
        // tkhd v0 duration 在 +20、v1 在 +28；mdhd v0 在 +16、v1 在 +24。
        var offset = isTkhd ? (version == 1 ? 28 : 20) : (version == 1 ? 24 : 16);
        if (offset + (version == 1 ? 8 : 4) > payload.Length)
            throw new InvalidDataException("盒子的长度和它的版本对不上，这一版拼接还不认这种形状");
        if (version == 1) BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(offset, 8), value);
        else BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, 4), (uint)Math.Min(value, uint.MaxValue));
        return payload;
    }

    private static byte[] BuildStts(IReadOnlyList<Sample> samples)
    {
        var runs = new List<(uint Count, uint Delta)>();
        foreach (var sample in samples)
        {
            if (runs.Count > 0 && runs[^1].Delta == sample.Duration) runs[^1] = (runs[^1].Count + 1, runs[^1].Delta);
            else runs.Add((1, sample.Duration));
        }
        var payload = new MemoryStream();
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4, 4), (uint)runs.Count);
        payload.Write(head);
        foreach (var run in runs)
        {
            var entry = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(0, 4), run.Count);
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4, 4), run.Delta);
            payload.Write(entry);
        }
        return payload.ToArray();
    }

    private static byte[] BuildCtts(IReadOnlyList<Sample> samples)
    {
        var runs = new List<(uint Count, int Offset)>();
        foreach (var sample in samples)
        {
            if (runs.Count > 0 && runs[^1].Offset == sample.CompositionOffset) runs[^1] = (runs[^1].Count + 1, runs[^1].Offset);
            else runs.Add((1, sample.CompositionOffset));
        }
        var payload = new MemoryStream();
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4, 4), (uint)runs.Count);
        payload.Write(head);
        foreach (var run in runs)
        {
            var entry = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(0, 4), run.Count);
            BinaryPrimitives.WriteInt32BigEndian(entry.AsSpan(4, 4), run.Offset);
            payload.Write(entry);
        }
        return payload.ToArray();
    }

    private static byte[] BuildStss(IReadOnlyList<Sample> samples)
    {
        var sync = new List<uint>();
        for (var index = 0; index < samples.Count; index++)
            if (samples[index].IsSync) sync.Add((uint)(index + 1));
        var payload = new MemoryStream();
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4, 4), (uint)sync.Count);
        payload.Write(head);
        foreach (var index in sync)
        {
            var entry = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(entry, index);
            payload.Write(entry);
        }
        return payload.ToArray();
    }

    /// <summary>每个 chunk 一条（简单、合法）：first_chunk 递增，各自带自己的 samples_per_chunk。</summary>
    private static byte[] BuildStsc(IReadOnlyList<(int Track, long Offset, int Count)> chunks)
    {
        var payload = new MemoryStream();
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4, 4), (uint)chunks.Count);
        payload.Write(head);
        for (var index = 0; index < chunks.Count; index++)
        {
            var entry = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(0, 4), (uint)(index + 1));
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4, 4), (uint)chunks[index].Count);
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(8, 4), 1);
            payload.Write(entry);
        }
        return payload.ToArray();
    }

    private static byte[] BuildStsz(IReadOnlyList<Sample> samples)
    {
        var payload = new MemoryStream();
        var head = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4, 4), 0);                       // sample_size = 0：逐样本给
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8, 4), (uint)samples.Count);
        payload.Write(head);
        foreach (var sample in samples)
        {
            var entry = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)sample.Size);
            payload.Write(entry);
        }
        return payload.ToArray();
    }

    private static byte[] BuildStco(IReadOnlyList<(int Track, long Offset, int Count)> chunks)
    {
        var payload = new MemoryStream();
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4, 4), (uint)chunks.Count);
        payload.Write(head);
        foreach (var chunk in chunks)
        {
            var entry = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)chunk.Offset);
            payload.Write(entry);
        }
        return payload.ToArray();
    }

    private static byte[] BoxHeader(string type, int size)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), size);
        Encoding.ASCII.GetBytes(type, header.AsSpan(4, 4));
        return header;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        var bytes = new byte[payload.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0, 4), bytes.Length);
        Encoding.ASCII.GetBytes(type, bytes.AsSpan(4, 4));
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static Mp4ConcatResult Fail(string error) => new(false, string.Empty, error, string.Empty);
}

/// <summary>一个样本：在源文件里的位置与大小，以及时间轴上的三个值。</summary>
internal sealed record Sample(long Offset, int Size, uint Duration, int CompositionOffset, bool IsSync);

/// <summary>一个盒子原样（含盒头）：重建时会整段抄过去。</summary>
internal sealed record RawChild(string Type, byte[] Bytes);

/// <summary>一条轨道：结构部分的原始字节（照抄用）+ 拆开的样本表。</summary>
internal sealed class Track
{
    public string Handler { get; init; } = string.Empty;
    public uint Timescale { get; init; }
    public long EditMediaTime { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public List<Sample> Samples { get; init; } = new();
    public bool HasCompositionOffsets { get; init; }

    public byte[] TkhdPayload { get; init; } = Array.Empty<byte>();
    public byte[] MdhdPayload { get; init; } = Array.Empty<byte>();
    public byte[] StsdPayload { get; init; } = Array.Empty<byte>();
    public byte[] SampleEntry { get; init; } = Array.Empty<byte>();
    public bool HasSyncTable { get; init; }

    public List<RawChild> BodyChildren { get; init; } = new();
    public List<RawChild> MdiaChildren { get; init; } = new();
    public List<RawChild> MinfChildren { get; init; } = new();
}

/// <summary>一个 mp4 解析出来的、拼接需要的那部分。</summary>
internal sealed class Mp4File
{
    public uint Timescale { get; init; }
    public uint NextTrackId { get; init; }
    public byte[] FtypBytes { get; init; } = Array.Empty<byte>();
    public List<Track> Tracks { get; init; } = new();

    public static Mp4File Parse(string path)
    {
        var data = File.ReadAllBytes(path);
        var top = Boxes(data, 0, data.Length);
        var ftyp = top.FirstOrDefault(box => box.Type == "ftyp") ?? throw new InvalidDataException("没有 ftyp");
        var moov = top.FirstOrDefault(box => box.Type == "moov") ?? throw new InvalidDataException("没有 moov");
        if (top.Any(box => box.Type == "moof")) throw new InvalidDataException("是分片 mp4（有 moof），这一版拼接不认");
        if (top.Count(box => box.Type == "mdat") > 1) throw new InvalidDataException("有多个 mdat，这一版拼接不认");

        var children = Boxes(data, moov.PayloadStart, moov.PayloadEnd);
        var mvhd = children.FirstOrDefault(box => box.Type == "mvhd") ?? throw new InvalidDataException("没有 mvhd");
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(mvhd.PayloadStart + 12, 4));
        var nextTrackId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(mvhd.PayloadStart + 96, 4));

        return new Mp4File
        {
            Timescale = timescale,
            NextTrackId = nextTrackId,
            FtypBytes = data[ftyp.Offset..ftyp.End],
            Tracks = children.Where(box => box.Type == "trak").Select(trak => ParseTrack(data, trak)).ToList()
        };
    }

    private static Track ParseTrack(byte[] data, Box trak)
    {
        var bodyChildren = Boxes(data, trak.PayloadStart, trak.PayloadEnd)
            .Select(box => new RawChild(box.Type, data[box.Offset..box.End])).ToList();

        var tkhd = Boxes(data, trak.PayloadStart, trak.PayloadEnd).FirstOrDefault(box => box.Type == "tkhd")
            ?? throw new InvalidDataException("没有 tkhd");
        var mdia = Boxes(data, trak.PayloadStart, trak.PayloadEnd).FirstOrDefault(box => box.Type == "mdia")
            ?? throw new InvalidDataException("没有 mdia");
        var mdiaBoxes = Boxes(data, mdia.PayloadStart, mdia.PayloadEnd);
        var mdiaChildren = mdiaBoxes.Select(box => new RawChild(box.Type, data[box.Offset..box.End])).ToList();
        var mdhd = mdiaBoxes.FirstOrDefault(box => box.Type == "mdhd") ?? throw new InvalidDataException("没有 mdhd");
        var hdlr = mdiaBoxes.FirstOrDefault(box => box.Type == "hdlr") ?? throw new InvalidDataException("没有 hdlr");
        var minf = mdiaBoxes.FirstOrDefault(box => box.Type == "minf") ?? throw new InvalidDataException("没有 minf");

        var minfBoxes = Boxes(data, minf.PayloadStart, minf.PayloadEnd);
        var minfChildren = minfBoxes.Select(box => new RawChild(box.Type, data[box.Offset..box.End])).ToList();
        var stbl = minfBoxes.FirstOrDefault(box => box.Type == "stbl") ?? throw new InvalidDataException("没有 stbl");
        var stblChildren = Boxes(data, stbl.PayloadStart, stbl.PayloadEnd);

        var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(mdhd.PayloadStart + 12, 4));
        var handler = Encoding.ASCII.GetString(data, hdlr.PayloadStart + 8, 4);

        var stsd = stblChildren.FirstOrDefault(box => box.Type == "stsd") ?? throw new InvalidDataException("没有 stsd");
        var entries = Boxes(data, stsd.PayloadStart + 8, stsd.PayloadEnd);
        if (entries.Count == 0) throw new InvalidDataException("stsd 里没有采样条目");
        var entry = entries[0];

        var stco = stblChildren.FirstOrDefault(box => box.Type == "stco");
        if (stco is null)
        {
            throw new InvalidDataException(stblChildren.Any(box => box.Type == "co64")
                ? "样本偏移用的是 co64（64 位），这一版拼接不认"
                : "样本表里没有 stco");
        }

        var durations = ExpandStts(data, stblChildren.First(box => box.Type == "stts"));
        var sizes = ExpandStsz(data, stblChildren.First(box => box.Type == "stsz"));
        if (durations.Count != sizes.Count)
            throw new InvalidDataException($"stts 与 stsz 的样本数不一致（{durations.Count} vs {sizes.Count}）");
        var ctts = stblChildren.FirstOrDefault(box => box.Type == "ctts");
        var composition = ctts is null ? null : ExpandCtts(data, ctts);
        if (composition is not null && composition.Count != sizes.Count)
            throw new InvalidDataException("ctts 与 stsz 的样本数不一致");
        var sync = stblChildren.FirstOrDefault(box => box.Type == "stss") is { } stss ? ExpandStss(data, stss) : null;

        var offsets = ExpandOffsets(data, stblChildren.First(box => box.Type == "stsc"), stco, sizes);
        var samples = new List<Sample>(sizes.Count);
        for (var index = 0; index < sizes.Count; index++)
        {
            samples.Add(new Sample(
                offsets[index], sizes[index], durations[index],
                composition is null ? 0 : composition[index],
                sync is null || sync.Contains(index + 1)));
        }

        return new Track
        {
            Handler = handler,
            Timescale = timescale,
            EditMediaTime = ReadEditMediaTime(data, trak),
            Data = data,
            Samples = samples,
            HasCompositionOffsets = composition is not null,
            TkhdPayload = data[tkhd.PayloadStart..tkhd.PayloadEnd],
            MdhdPayload = data[mdhd.PayloadStart..mdhd.PayloadEnd],
            StsdPayload = data[stsd.PayloadStart..stsd.PayloadEnd],
            SampleEntry = data[entry.Offset..entry.End],
            HasSyncTable = sync is not null,
            BodyChildren = bodyChildren,
            MdiaChildren = mdiaChildren,
            MinfChildren = minfChildren
        };
    }

    private static long ReadEditMediaTime(byte[] data, Box trak)
    {
        var edts = Boxes(data, trak.PayloadStart, trak.PayloadEnd).FirstOrDefault(box => box.Type == "edts");
        if (edts is null) return 0;
        var elst = Boxes(data, edts.PayloadStart, edts.PayloadEnd).FirstOrDefault(box => box.Type == "elst");
        if (elst is null) return 0;
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(elst.PayloadStart + 4, 4));
        if (count == 0) return 0;
        // 多条就是「分段剪辑」，语义不只是时间原点，这一版不认。
        if (count > 1) throw new InvalidDataException("编辑列表有多条（分段剪辑），这一版拼接不认");
        var version = data[elst.PayloadStart];
        return version == 1
            ? BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(elst.PayloadStart + 16, 8))
            : BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(elst.PayloadStart + 12, 4));
    }

    private static List<uint> ExpandStts(byte[] data, Box box)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.PayloadStart + 4, 4));
        var durations = new List<uint>();
        var offset = box.PayloadStart + 8;
        for (var i = 0; i < count; i++)
        {
            var repeats = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            var delta = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            for (var r = 0u; r < repeats; r++) durations.Add(delta);
            offset += 8;
        }
        return durations;
    }

    private static List<int> ExpandStsz(byte[] data, Box box)
    {
        var uniform = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.PayloadStart + 4, 4));
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.PayloadStart + 8, 4));
        var sizes = new List<int>(count);
        if (uniform > 0)
        {
            for (var i = 0; i < count; i++) sizes.Add((int)uniform);
            return sizes;
        }
        var offset = box.PayloadStart + 12;
        for (var i = 0; i < count; i++)
        {
            sizes.Add((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
            offset += 4;
        }
        return sizes;
    }

    private static List<int> ExpandCtts(byte[] data, Box box)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.PayloadStart + 4, 4));
        var offsets = new List<int>();
        var offset = box.PayloadStart + 8;
        for (var i = 0; i < count; i++)
        {
            var repeats = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4, 4));
            for (var r = 0u; r < repeats; r++) offsets.Add(value);
            offset += 8;
        }
        return offsets;
    }

    private static HashSet<int> ExpandStss(byte[] data, Box box)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.PayloadStart + 4, 4));
        var sync = new HashSet<int>();
        var offset = box.PayloadStart + 8;
        for (var i = 0; i < count; i++)
        {
            sync.Add((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
            offset += 4;
        }
        return sync;
    }

    /// <summary>算每个样本在文件里的绝对偏移：stsc 说「第几个 chunk 起、每 chunk 几个样本」，stco 给 chunk 偏移。</summary>
    private static long[] ExpandOffsets(byte[] data, Box stsc, Box stco, List<int> sizes)
    {
        var scCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(stsc.PayloadStart + 4, 4));
        var runs = new List<(uint FirstChunk, uint PerChunk)>();
        var cursor = stsc.PayloadStart + 8;
        for (var i = 0; i < scCount; i++)
        {
            runs.Add((BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor, 4)),
                      BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cursor + 4, 4))));
            cursor += 12;
        }

        var chunkCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(stco.PayloadStart + 4, 4));
        var offsets = new long[sizes.Count];
        var sample = 0;
        for (var chunk = 1; chunk <= chunkCount && sample < sizes.Count; chunk++)
        {
            var perChunk = 0u;
            foreach (var run in runs)
                if (run.FirstChunk <= chunk) perChunk = run.PerChunk;
            if (perChunk == 0) continue;
            var position = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(stco.PayloadStart + 4 + (chunk - 1) * 4, 4));
            for (var i = 0u; i < perChunk && sample < sizes.Count; i++)
            {
                offsets[sample] = position;
                position += sizes[sample];
                sample++;
            }
        }
        if (sample < sizes.Count)
            throw new InvalidDataException($"样本表对不上：只定位到 {sample}/{sizes.Count} 个样本");
        return offsets;
    }

    private static List<Box> Boxes(byte[] data, int start, int end)
    {
        var boxes = new List<Box>();
        var offset = start;
        while (offset + 8 <= end)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(data, offset + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (offset + 16 > end) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                header = 16;
            }
            else if (size == 0) size = end - offset;
            if (size < header || offset + size > end) break;
            boxes.Add(new Box(type, (int)offset, (int)(offset + size), (int)(offset + header), (int)(offset + size)));
            offset += (int)size;
        }
        return boxes;
    }
}

/// <summary>一个盒子在数据里的位置。<c>Offset</c> 含盒头，<c>PayloadStart</c> 是内容起点。</summary>
internal sealed record Box(string Type, int Offset, int End, int PayloadStart, int PayloadEnd);
