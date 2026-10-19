using System.Buffers.Binary;
using System.Text;

namespace YEEYEEYEE.Desktop;

/// <summary>拼接结果。<c>Note</c> 是**如实说明**：做了哪些取舍、丢了什么，成功也要说。</summary>
public sealed record Mp4ConcatResult(bool Ok, string FilePath, string Error, string Note);

/// <summary>
/// 一段片子的容器事实——不解码就读得出来的那几项。给版本对比用：要比两版成片，先得知道每一版究竟是什么。
///
/// <see cref="Readable"/> 为 false 时只有 <see cref="Problem"/> 与 <see cref="Bytes"/> 可信：
/// **读不出来就说读不出来**，不拿 0 秒 / 0 帧冒充一个「量过了」的结果。
/// </summary>
public sealed record Mp4FilmInfo(
    bool Readable, string Problem, double Seconds, int Tracks, int Width, int Height, int Frames, long Bytes)
{
    public static Mp4FilmInfo Unreadable(string problem, long bytes = 0) => new(false, problem, 0, 0, 0, 0, 0, bytes);

    /// <summary>人话一行。读不出来就直说，不编数字。</summary>
    public string Describe() => Readable
        ? $"{Seconds:0.00} 秒 · {Width}×{Height} · {Frames} 帧 · {Tracks} 条轨 · {Size(Bytes)}"
        : $"读不出来（{Problem}）";

    public static string Size(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
        : $"{Math.Max(bytes, 0) / 1024.0:0} KB";
}

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
    /// 实测源片视频为 124×512/12288 = 5.16667s，音频剪头后为 161×1024/32000 = 5.152s。
    /// 剪头并不保证尾部对齐；音轨用逐段媒体编辑和空编辑保留源尾隙，避免差值累积。
    /// </summary>
    private static int PrimingSamplesToDrop(Track track)
    {
        if (track.EditMediaTime < 0)
            throw new InvalidDataException("编辑起点为负数，这一版不支持空编辑");
        if (track.HasCompositionOffsets)
        {
            // 只支持实测的合成偏移原点；有 ctts 并不代表任意编辑起点都可保留。
            if (track.Samples.Count == 0 || track.EditMediaTime != track.Samples[0].CompositionOffset)
                throw new InvalidDataException("编辑起点与首样本合成偏移不一致，无法无损拼接");
            return 0;
        }
        if (track.EditMediaTime == 0) return 0;

        var remaining = track.EditMediaTime;
        var dropped = 0;
        foreach (var sample in track.Samples)
        {
            if (remaining <= 0) break;
            remaining -= sample.Duration;
            dropped++;
        }
        if (dropped >= track.Samples.Count)
            throw new InvalidDataException("编辑起点会剪掉整轨，无法无损拼接");
        if (remaining != 0)
            throw new InvalidDataException("编辑起点不在样本边界，无法无损剪头");
        return dropped;
    }

    /// <summary>
    /// 只读探针：不解码，只读容器头，报出这段片子「多久、几轨、画面多大、多少帧、多大」。
    /// 给版本对比用——要比两版成片，先得知道每一版究竟是什么，而这件事不能靠文件名或附件标题。
    ///
    /// 宽容度比拼接高：拼接认不出的形状（分片 mp4、co64）这里照样能报出时长，
    /// 因为「量一下」与「接起来」是两件事。真读不出来（没 ftyp、没 moov、文件不在）就如实说，不报 0。
    ///
    /// 读法是「先只读头部」：我们自己的成片 moov 都在前面，量一次只碰几百 KB，不必把整段片子搬进内存。
    /// moov 不在头部那一段里（别的工具出的、没做 faststart 的文件）才整份读——那样慢，但结果一样。
    /// </summary>
    public static Mp4FilmInfo Probe(string path)
    {
        long bytes = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Mp4FilmInfo.Unreadable("文件不在了");
            bytes = info.Length;

            var data = ReadHead(path, 1 << 20);
            var top = Mp4File.Boxes(data, 0, data.Length);
            if (top.All(box => box.Type != "ftyp")) return Mp4FilmInfo.Unreadable("不是 mp4（没有 ftyp）", bytes);
            var moov = top.FirstOrDefault(box => box.Type == "moov");
            if (moov is null || moov.End > data.Length)
            {
                data = File.ReadAllBytes(path);
                top = Mp4File.Boxes(data, 0, data.Length);
                moov = top.FirstOrDefault(box => box.Type == "moov");
            }
            if (moov is null)
                return Mp4FilmInfo.Unreadable(
                    top.Any(box => box.Type == "moof") ? "是分片 mp4，moov 不在文件里" : "没有 moov", bytes);

            var children = Mp4File.Boxes(data, moov.PayloadStart, moov.PayloadEnd);
            var movieSeconds = children.FirstOrDefault(box => box.Type == "mvhd") is { } mvhd
                ? ReadDurationSeconds(data, mvhd) : 0d;

            var tracks = 0;
            var width = 0;
            var height = 0;
            var frames = 0;
            var videoSeconds = 0d;
            foreach (var trak in children.Where(box => box.Type == "trak"))
            {
                tracks++;
                var mdia = Mp4File.Boxes(data, trak.PayloadStart, trak.PayloadEnd).FirstOrDefault(box => box.Type == "mdia");
                if (mdia is null) continue;
                var mdiaBoxes = Mp4File.Boxes(data, mdia.PayloadStart, mdia.PayloadEnd);
                if (mdiaBoxes.FirstOrDefault(box => box.Type == "hdlr") is not { } hdlr || hdlr.PayloadStart + 12 > hdlr.PayloadEnd) continue;
                if (Encoding.ASCII.GetString(data, hdlr.PayloadStart + 8, 4) != "vide") continue;

                if (mdiaBoxes.FirstOrDefault(box => box.Type == "mdhd") is { } mdhd)
                    videoSeconds = ReadDurationSeconds(data, mdhd);
                if (mdiaBoxes.FirstOrDefault(box => box.Type == "minf") is not { } minf) continue;
                if (Mp4File.Boxes(data, minf.PayloadStart, minf.PayloadEnd).FirstOrDefault(box => box.Type == "stbl") is not { } stbl) continue;
                var stblBoxes = Mp4File.Boxes(data, stbl.PayloadStart, stbl.PayloadEnd);

                // 帧数就是视频轨的样本数：我们这条流水线一帧一个样本。
                if (stblBoxes.FirstOrDefault(box => box.Type == "stsz") is { } stsz && stsz.PayloadStart + 12 <= stsz.PayloadEnd)
                    frames = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(stsz.PayloadStart + 8, 4));

                // 宽高在采样条目里：8 字节 SampleEntry 头 + 16 字节 VisualSampleEntry 头，之后是 width/height。
                if (stblBoxes.FirstOrDefault(box => box.Type == "stsd") is not { } stsd) continue;
                if (Mp4File.Boxes(data, stsd.PayloadStart + 8, stsd.PayloadEnd).FirstOrDefault() is not { } entry) continue;
                if (entry.PayloadStart + 28 > entry.PayloadEnd) continue;
                width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(entry.PayloadStart + 24, 2));
                height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(entry.PayloadStart + 26, 2));
            }

            // 未知时长的全 1 哨兵按字段宽度判断；无有效电影时长时回退到视频轨。
            var seconds = movieSeconds > 0 ? movieSeconds : videoSeconds;
            if (seconds <= 0)
                return Mp4FilmInfo.Unreadable("容器里的时长是空的（mvhd 与视频轨的 mdhd 都没给出有效时长）", bytes);

            return new Mp4FilmInfo(true, string.Empty, seconds, tracks, width, height, frames, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidDataException or IndexOutOfRangeException or OverflowException)
        {
            return Mp4FilmInfo.Unreadable(exception.Message, bytes);
        }
    }

    private static double ReadDurationSeconds(byte[] data, Box box)
    {
        if (box.PayloadEnd - box.PayloadStart < 4) return 0;
        var version = data[box.PayloadStart];
        if (version is not (0 or 1)) return 0;
        var timescaleOffset = version == 1 ? 20 : 12;
        var durationOffset = version == 1 ? 24 : 16;
        var durationBytes = version == 1 ? 8 : 4;
        if (box.PayloadEnd - box.PayloadStart < durationOffset + durationBytes) return 0;
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.PayloadStart + timescaleOffset, 4));
        var duration = version == 1
            ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(box.PayloadStart + durationOffset, 8))
            : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(box.PayloadStart + durationOffset, 4));
        var unknown = version == 1 ? ulong.MaxValue : uint.MaxValue;
        return timescale > 0 && duration > 0 && duration != unknown ? (double)duration / timescale : 0;
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
        for (var index = 0; index < files.Count; index++)
        {
            if (files[index].Tracks.Count == 0)
                return Fail($"第 {index + 1} 段（{Path.GetFileName(inputs[index])}）里一条轨道都没有，拼不了。");
        }
        for (var index = 1; index < files.Count; index++)
        {
            var mismatch = Compare(first, files[index], index);
            if (mismatch.Length > 0) return Fail(mismatch);
        }

        try
        {
            return Write(files, outputPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
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

    private static uint CommonMovieTimescale(List<Mp4File> files)
    {
        ulong common = 1;
        foreach (var scale in files.SelectMany(file => file.Tracks.Select(track => track.Timescale).Prepend(file.Timescale)))
        {
            var a = common;
            var b = (ulong)scale;
            while (b != 0) (a, b) = (b, a % b);
            common = checked(common / a * scale);
            if (common > uint.MaxValue)
                throw new InvalidDataException("精确公共影片时基超出 32 位范围，无法无损拼接");
        }
        return (uint)common;
    }

    private static bool CanPreserveAudioTail(Mp4File file, int videoIndex, int audioIndex,
        List<ulong> mediaDurations, List<ulong> trackDurations, uint movieTimescale)
    {
        var video = file.Tracks[videoIndex];
        var audio = file.Tracks[audioIndex];
        if (audio.HasCompositionOffsets || audio.Edit is not { MediaRate: 0x00010000 }
            || video.Edit is not { MediaRate: 0x00010000 }
            || audio.SampleEntry.Length < 8 || Encoding.ASCII.GetString(audio.SampleEntry, 4, 4) != "mp4a"
            || audio.Samples.Count == 0 || audio.Samples.Any(sample => sample.Duration != 1024)) return false;
        var esds = IndexOf(audio.SampleEntry, "esds");
        var es = esds < 4 ? -1 : FindDescriptor(audio.SampleEntry, esds + 8, 0x03);
        var config = es < 0 || es + 3 > audio.SampleEntry.Length || audio.SampleEntry[es + 2] != 0
            ? -1 : FindDescriptor(audio.SampleEntry, es + 3, 0x04);
        if (config < 0 || config + 13 > audio.SampleEntry.Length || audio.SampleEntry[config] != 0x40) return false;
        var specific = FindDescriptor(audio.SampleEntry, config + 13, 0x05);
        if (specific < 0 || specific >= audio.SampleEntry.Length
            || (audio.SampleEntry[specific] >> 3) is not (1 or 2 or 3 or 4)) return false;
        if (trackDurations[videoIndex] - trackDurations[audioIndex]
            >= checked(1024UL * (movieTimescale / audio.Timescale))) return false;
        foreach (var index in new[] { videoIndex, audioIndex })
        {
            var track = file.Tracks[index];
            var expected = decimal.Round((decimal)mediaDurations[index] * file.Timescale / track.Timescale,
                0, MidpointRounding.AwayFromZero);
            if (track.Edit!.Duration != expected) return false;
        }
        long decodeTime = 0;
        long minPts = long.MaxValue;
        long maxEnd = long.MinValue;
        foreach (var sample in video.Samples)
        {
            var pts = checked(decodeTime + sample.CompositionOffset);
            minPts = Math.Min(minPts, pts);
            maxEnd = Math.Max(maxEnd, checked(pts + sample.Duration));
            decodeTime = checked(decodeTime + sample.Duration);
        }
        return minPts == video.EditMediaTime && maxEnd - minPts == checked((long)mediaDurations[videoIndex]);
    }

    private static Mp4ConcatResult Write(List<Mp4File> files, string outputPath)
    {
        var movieTimescale = CommonMovieTimescale(files);
        var head = files[0];
        var audioEdits = head.Tracks.Select(_ => new List<EditEntry>()).ToList();
        var mediaCursors = new ulong[head.Tracks.Count];
        var gapMilliseconds = new List<double>();

        // ── 1. 按轨道把各段的样本并起来，并算出每段占多长 ──────────────────────
        var merged = head.Tracks.Select(_ => new List<Sample>()).ToList();
        var droppedPerTrack = new int[head.Tracks.Count];
        var segmentDurations = new List<ulong>();
        var primingDropped = 0;

        foreach (var file in files)
        {
            ulong longest = 0;
            var trackDurations = new List<ulong>();
            var mediaDurations = new List<ulong>();
            for (var t = 0; t < file.Tracks.Count; t++)
            {
                var track = file.Tracks[t];
                var skip = PrimingSamplesToDrop(track);
                primingDropped += skip;
                droppedPerTrack[t] += skip;
                var kept = track.Samples.Skip(skip).ToList();
                var duration = (ulong)kept.Sum(sample => (long)sample.Duration);
                mediaDurations.Add(duration);
                var inMovieTime = checked(duration * (movieTimescale / track.Timescale));
                trackDurations.Add(inMovieTime);
                if (track.Edit is { MediaRate: not 0x00010000 })
                    throw new InvalidDataException("源编辑列表速率不是 1，无法无损拼接");
                if (inMovieTime > longest) longest = inMovieTime;

                if (kept.Count > 0 && !kept[0].IsSync)
                    throw new InvalidDataException($"{track.Handler} 轨保留后的首样本不是同步样本，无法无损拼接");
                merged[t].AddRange(kept);

            }
            if (trackDurations.Max() != trackDurations.Min())
            {
                var video = file.Tracks.FindIndex(track => track.Handler == "vide");
                var audio = file.Tracks.FindIndex(track => track.Handler == "soun");
                if (file.Tracks.Count != 2 || video < 0 || audio < 0
                    || trackDurations[audio] >= trackDurations[video]
                    || !CanPreserveAudioTail(file, video, audio, mediaDurations, trackDurations, movieTimescale))
                    throw new InvalidDataException("剪头后各轨时长不一致，且不满足单视频/AAC 音频短于一个样本的源尾隙条件，无法无损拼接");
                gapMilliseconds.Add((trackDurations[video] - trackDurations[audio]) * 1000d / movieTimescale);
            }
            for (var t = 0; t < file.Tracks.Count; t++)
            {
                if (file.Tracks[t].Handler == "soun")
                {
                    audioEdits[t].Add(new EditEntry(trackDurations[t], checked((long)mediaCursors[t]), 0x00010000));
                    if (trackDurations[t] < longest)
                        audioEdits[t].Add(new EditEntry(longest - trackDurations[t], -1, 0x00010000));
                }
                mediaCursors[t] = checked(mediaCursors[t] + mediaDurations[t]);
            }
            segmentDurations.Add(longest);
        }

        var totalDuration = segmentDurations.Aggregate(0UL, (a, b) => checked(a + b));
        if (totalDuration >= uint.MaxValue || mediaCursors.Any(duration => duration >= uint.MaxValue))
            throw new InvalidDataException("精确时基下的时长超出当前 32 位容器字段范围，无法无损拼接");
        if (gapMilliseconds.Count == 0)
            foreach (var edits in audioEdits) edits.Clear();

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
        var measuring = BuildMoov(head, merged, chunks, totalDuration, movieTimescale, droppedPerTrack, audioEdits);
        var headerLength = head.FtypBytes.Length + measuring.Length + 8;
        var fixedChunks = chunks.Select(chunk => (chunk.Track, chunk.Offset + headerLength, chunk.Count)).ToList();
        var moov = BuildMoov(head, merged, fixedChunks, totalDuration, movieTimescale, droppedPerTrack, audioEdits);

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
            + (gapMilliseconds.Count > 0
                ? $"保留源片尾部无音频区间：各段尾隙 {string.Join("、", gapMilliseconds.Select(gap => $"{gap:0.000}ms"))}，"
                  + "使用音轨多条 elst 的空编辑补到画面边界，不拉长 AAC 样本；播放器须支持多条编辑及空编辑，尚未试听验证。"
                : string.Empty)
            + "每条轨道的「样本组」（sgpd/sbgp）没有保留：那是可选的附加分组，合并会打乱样本下标，"
            + "丢掉不影响播放。";
        return new Mp4ConcatResult(true, outputPath, string.Empty, note);
    }

    private static byte[] BuildMoov(
        Mp4File head, List<List<Sample>> merged, List<(int Track, long Offset, int Count)> chunks,
        ulong totalDuration, uint movieTimescale, int[] droppedPerTrack, List<List<EditEntry>> audioEdits)
    {
        var payload = new MemoryStream();
        payload.Write(BuildMvhd(movieTimescale, totalDuration, head.NextTrackId));
        for (var t = 0; t < head.Tracks.Count; t++)
        {
            if (merged[t].Count == 0) continue;
            payload.Write(BuildTrak(
                head.Tracks[t], merged[t],
                chunks.Where(chunk => chunk.Track == t).ToList(),
                totalDuration, movieTimescale, droppedPerTrack[t], audioEdits[t]));
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
        ulong totalDuration, uint movieTimescale, int droppedSamples, List<EditEntry> edits)
    {
        var payload = new MemoryStream();
        var outputEdits = edits.Count > 0 ? edits
            : new List<EditEntry> { new(totalDuration, EditMediaTimeFor(track, droppedSamples), 0x00010000) };
        if (track.BodyChildren.All(child => child.Type != "edts"))
            payload.Write(BuildEdts(outputEdits));
        foreach (var child in track.BodyChildren)
        {
            switch (child.Type)
            {
                case "tkhd":
                    payload.Write(Box("tkhd", PatchDuration(track.TkhdPayload, totalDuration, isTkhd: true)));
                    break;
                case "edts":
                    payload.Write(BuildEdts(outputEdits));
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

    /// <summary>
    /// 成品的编辑列表该写什么时间原点——**这一条也得分轨道，判据与丢预听样本同一个**：
    ///
    /// · **丢过样本的（音频）**：预听样本已经在并表时丢掉了，原点写 0——再保留原来的 media_time 等于多剪一次；
    /// · **没丢的（视频）**：原样的 media_time 必须留着。实测它正好等于首帧的 ctts 偏移（1024），
    ///   源文件正是靠它把首帧的 PTS 映射到 t=0。这里若一律写 0，整条时间轴会晚 83ms，
    ///   最后一帧就落到声明时长之外（实测解码帧数从 744 变 743——数据没丢，是时间轴错位）。
    /// </summary>
    private static long EditMediaTimeFor(Track track, int droppedSamples) =>
        droppedSamples > 0 ? 0 : track.EditMediaTime;

    private static byte[] BuildMdia(Track track, List<Sample> samples, List<(int Track, long Offset, int Count)> chunks)
    {
        var mediaDuration = (ulong)samples.Sum(sample => (long)sample.Duration);
        var payload = new MemoryStream();
        foreach (var child in track.MdiaChildren)
        {
            if (child.Type == "mdhd")
                payload.Write(Box("mdhd", PatchDuration(track.MdhdPayload, mediaDuration, isTkhd: false)));
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
        if (track.HasSyncTable || samples.Any(sample => !sample.IsSync))
            payload.Write(Box("stss", BuildStss(samples)));
        payload.Write(Box("stsc", BuildStsc(chunks)));
        payload.Write(Box("stsz", BuildStsz(samples)));
        payload.Write(Box("stco", BuildStco(chunks)));
        return payload.ToArray();
    }

    /// <summary>编辑列表。movieDuration 是这段在影片时间轴上占的长度，mediaTime 是它的时间原点——
    /// **两者都得分轨道，别一律写 0**（见 <see cref="EditMediaTimeFor"/>）。</summary>
    private static byte[] BuildEdts(IReadOnlyList<EditEntry> edits)
    {
        var version1 = edits.Any(edit => edit.MediaTime > int.MaxValue || edit.Duration > uint.MaxValue);
        var stride = version1 ? 20 : 12;
        var elst = new byte[checked(8 + edits.Count * stride)];
        elst[0] = version1 ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(4, 4), checked((uint)edits.Count));
        for (var index = 0; index < edits.Count; index++)
        {
            var edit = edits[index];
            var offset = 8 + index * stride;
            if (version1)
            {
                BinaryPrimitives.WriteUInt64BigEndian(elst.AsSpan(offset, 8), edit.Duration);
                BinaryPrimitives.WriteInt64BigEndian(elst.AsSpan(offset + 8, 8), edit.MediaTime);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(offset, 4), checked((uint)edit.Duration));
                BinaryPrimitives.WriteInt32BigEndian(elst.AsSpan(offset + 4, 4), checked((int)edit.MediaTime));
            }
            BinaryPrimitives.WriteUInt32BigEndian(elst.AsSpan(offset + stride - 4, 4), edit.MediaRate);
        }
        var payload = new MemoryStream();
        payload.Write(Box("elst", elst));
        return Box("edts", payload.ToArray());
    }

    /// <summary>改时长。tkhd 的时长以**影片时基**计，mdhd 的以**自身时基**计——这两处混了就会时长错。</summary>
    private static byte[] PatchDuration(byte[] original, ulong duration, bool isTkhd)
    {
        var payload = (byte[])original.Clone();
        var version = payload[0];
        var value = duration;
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

    /// <summary>只读文件开头这一段（最多 <paramref name="limit"/> 字节）。</summary>
    private static byte[] ReadHead(string path, int limit)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, limit);
        var buffer = new byte[length];
        stream.ReadExactly(buffer, 0, length);
        return buffer;
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

internal sealed record EditEntry(ulong Duration, long MediaTime, uint MediaRate);

/// <summary>一条轨道：结构部分的原始字节（照抄用）+ 拆开的样本表。</summary>
internal sealed class Track
{
    public string Handler { get; init; } = string.Empty;
    public uint Timescale { get; init; }
    public EditEntry? Edit { get; init; }
    public long EditMediaTime => Edit?.MediaTime ?? 0;
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
        if (mvhd.PayloadEnd - mvhd.PayloadStart < 100 || data[mvhd.PayloadStart] != 0)
            throw new InvalidDataException("mvhd 长度或版本不受当前拼接支持");
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(mvhd.PayloadStart + 12, 4));
        if (timescale == 0) throw new InvalidDataException("影片时间刻度为 0");
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

        if (mdhd.PayloadEnd - mdhd.PayloadStart < 24 || data[mdhd.PayloadStart] != 0)
            throw new InvalidDataException("mdhd 长度或版本不受当前拼接支持");
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(mdhd.PayloadStart + 12, 4));
        if (timescale == 0) throw new InvalidDataException("轨道时间刻度为 0");
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
            Edit = ReadEdit(data, trak),
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

    private static EditEntry? ReadEdit(byte[] data, Box trak)
    {
        var edts = Boxes(data, trak.PayloadStart, trak.PayloadEnd).FirstOrDefault(box => box.Type == "edts");
        if (edts is null) return null;
        var elst = Boxes(data, edts.PayloadStart, edts.PayloadEnd).FirstOrDefault(box => box.Type == "elst");
        if (elst is null) return null;
        if (elst.PayloadEnd - elst.PayloadStart < 8) throw new InvalidDataException("elst 头部不完整");
        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(elst.PayloadStart + 4, 4));
        if (count == 0) return null;
        if (count > 1) throw new InvalidDataException("编辑列表有多条（分段剪辑），这一版拼接不认");
        var version = data[elst.PayloadStart];
        if (version is not (0 or 1) || elst.PayloadEnd - elst.PayloadStart < (version == 1 ? 28 : 20))
            throw new InvalidDataException("elst 长度或版本不受当前拼接支持");
        var offset = elst.PayloadStart + 8;
        return version == 1
            ? new EditEntry(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8)),
                BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(offset + 8, 8)),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 16, 4)))
            : new EditEntry(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)),
                BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset + 4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8, 4)));
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
            // stco 也是 full box：内容先有 version/flags(4)，才是 entry_count(4)，条目从 +8 起。
            // **这里原先写成 +4**，第一个 chunk 的偏移就被读成了 entry_count（真文件里是 123 这种小数字），
            // 于是从第一帧起读到的都是垃圾字节——而解码器只会报「Invalid NAL unit size」，
            // 看不出是容器表的问题。更要命的是当时那个造样本的小工具也漏了 version/flags，
            // 两边错得一样，254 项测试全绿——**是独立解码判读把它揪出来的**（见 README）。
            var position = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(stco.PayloadStart + 8 + (chunk - 1) * 4, 4));
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

    internal static List<Box> Boxes(byte[] data, int start, int end)
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
