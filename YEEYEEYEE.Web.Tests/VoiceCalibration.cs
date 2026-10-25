using NAudio.Wave;

/// <summary>
/// 纯代码的“声学相似度”校准：不解码任何模型，只用信号处理。
///
/// 为什么先做自比对：真正比两条不同音频之前，得先证明这条链路本身可信——
/// 同一段音频应接近 1.0，轻微重采样（模拟编码/采样漂移）仍应很高，
/// 而变速（音高与共振峰一起变）应明显掉下去。三个锚点都符合预期，
/// 这个分数才有资格拿去比“同一个人”还是“换了人”。
///
/// **它不是声纹**：没有说话人嵌入模型，分数不能直接套产品里的 0.75；
/// 校准通过后只能在项目内定阈值，并在证据里注明是启发式。
/// </summary>
internal static class VoiceCalibration
{
    private const int Rate = 16000;
    private const int FrameLength = 400;   // 25ms
    private const int FrameShift = 160;    // 10ms
    private const int FftSize = 512;
    private const int MelBands = 26;
    private const int MfccCount = 12;      // c1..c12；更高阶在短音频上基本是噪声，比均值时不用

    public static void Run()
    {
        var first = Environment.GetEnvironmentVariable("VOICE_INPUT")
            ?? Path.Combine(Environment.CurrentDirectory, "demo-projects", "claude-covers-deepseek", "assets", "s07-dialogue-4s.mp4");
        var second = Environment.GetEnvironmentVariable("VOICE_INPUT2") ?? string.Empty;

        var original = LoadMono(first);
        Console.WriteLine($"AUDIO {Path.GetFileName(first)} {Describe(original)}");
        if (second.Length > 0)
        {
            var other = LoadMono(second);
            Console.WriteLine($"AUDIO {Path.GetFileName(second)} {Describe(other)}");
        }

        var features = Extract(original);
        Console.WriteLine($"FEATURES frames={features.Frames.Count} active={features.ActiveRatio:0.00} " +
            $"f0={features.MedianF0:0}Hz rms={features.Rms:0.0000}");
        Console.WriteLine($"VOICE 音高粗判={Gender(features.MedianF0)}（女声常见 165–255Hz，男声常见 85–180Hz）");
        Console.WriteLine($"VOICE 共振峰 F1={features.MedianF1:0}Hz F2={features.MedianF2:0}Hz "
            + $"交叉结论={GenderVerdict(features.MedianF0, features.MedianF1, features.MedianF2)}");

        Compare("同一段（自比对基准）", original, original);
        Compare("+0.5% 采样漂移", original, Resample(original, Rate, (int)(Rate * 1.005)));
        Compare("+15% 变速（另一把声音的代理）", original, Resample(original, Rate, (int)(Rate * 1.15)));
        Compare("-15% 变速（另一把声音的代理）", original, Resample(original, Rate, (int)(Rate * 0.85)));
        if (second.Length > 0) Compare(Path.GetFileName(second), original, LoadMono(second));
    }

    private static void Compare(string label, float[] left, float[] right)
    {
        var a = Extract(left);
        var b = Extract(right);
        if (a.Frames.Count == 0 || b.Frames.Count == 0)
        {
            Console.WriteLine($"COMPARE {label} -> 没有可用语音帧（整段近乎静音）");
            return;
        }
        var spectrumCos = Cosine(a.Spectrum, b.Spectrum);
        // MFCC 均值只作参考：短音频上高阶系数噪声大，不参与判定。
        var mfccCos = Correlation(a.MeanMfcc, b.MeanMfcc);
        var semitones = a.MedianF0 > 0 && b.MedianF0 > 0 ? 12 * Math.Log2(a.MedianF0 / b.MedianF0) : double.NaN;
        var dtw = DtwSimilarity(a.Frames, b.Frames);
        // 判定只用两条稳定的：长时谱形没变 + 音高没跨半音。质心这类特征在带音乐的短音轨上会跳变，已剔除。
        var same = spectrumCos >= 0.995 && Math.Abs(semitones) <= 1.5;
        Console.WriteLine($"COMPARE {label} -> spectrum={spectrumCos:0.000} 参考mfcc={mfccCos:0.000} "
            + $"f0差={semitones:+0.0;-0.0}半音 判定={(same ? "同一个人" : "不同人/不同音高")} (dtw={dtw:0.000})");
    }

    private sealed record Features(List<double[]> Frames, double[] Spectrum, double[] MeanMfcc, double ActiveRatio, double MedianF0, double MedianF1, double MedianF2, double Rms);

    private static Features Extract(float[] samples)
    {
        var frames = new List<float[]>();
        double maxRms = 0;
        for (var start = 0; start + FrameLength <= samples.Length; start += FrameShift)
        {
            var rms = 0d;
            for (var i = 0; i < FrameLength; i++) rms += samples[start + i] * samples[start + i];
            rms = Math.Sqrt(rms / FrameLength);
            maxRms = Math.Max(maxRms, rms);
            frames.Add(samples[start..(start + FrameLength)]);
        }
        var threshold = Math.Max(1e-4, maxRms * 0.1);

        var vectors = new List<double[]>();
        var spectrum = new double[MelBands];
        var mfccSum = new double[MfccCount];
        var f1s = new List<double>();
        var f2s = new List<double>();
        var kept = 0;
        foreach (var frame in frames)
        {
            var energy = 0d;
            foreach (var value in frame) energy += value * value;
            var rms = Math.Sqrt(energy / frame.Length);
            if (rms < threshold) continue;
            kept++;
            // 共振峰只在能量明显高于门限的帧上算：低能量帧的 LPC 会被噪声带偏。
            if (rms >= threshold * 1.5 && Formants(frame, out var f1, out var f2))
            {
                f1s.Add(f1);
                f2s.Add(f2);
            }
            var vector = Mfcc(frame, out var mel);
            vectors.Add(vector);
            for (var i = 0; i < MfccCount; i++) mfccSum[i] += vector[i];
            for (var i = 0; i < MelBands; i++) spectrum[i] += mel[i] / frames.Count;
        }
        if (vectors.Count == 0)
            return new Features(vectors, new double[MelBands], new double[MfccCount], 0, 0, 0, 0, 0);

        var mean = new double[MfccCount];
        for (var i = 0; i < MfccCount; i++) mean[i] = mfccSum[i] / vectors.Count;
        // 长时平均谱按“每帧占活跃帧的比例”归一，消掉段落长短的影响。
        for (var i = 0; i < MelBands; i++) spectrum[i] *= frames.Count / Math.Max(kept, 1);
        // CMVN：抵消通道与录音音量差异，再喂 DTW。
        foreach (var vector in vectors)
            for (var i = 0; i < MfccCount; i++) vector[i] -= mean[i];

        var pitch = Pitch(samples);
        return new Features(vectors, spectrum, mean, (double)kept / frames.Count, pitch,
            Median(f1s), Median(f2s), maxRms);
    }

    private static double[] Mfcc(float[] frame, out double[] mel)
    {
        var pre = new double[FftSize];
        for (var i = 0; i < FrameLength; i++)
        {
            var value = frame[i] - 0.97 * (i == 0 ? frame[0] : frame[i - 1]);
            pre[i] = value * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (FrameLength - 1)));
        }
        var re = (double[])pre.Clone();
        var im = new double[FftSize];
        Fft(re, im);
        var power = new double[FftSize / 2 + 1];
        for (var i = 0; i < power.Length; i++) power[i] = (re[i] * re[i] + im[i] * im[i]) / FftSize;

        mel = MelEnergies(power);
        var result = new double[MfccCount];
        for (var k = 0; k < MfccCount; k++)
        {
            var sum = 0d;
            for (var n = 0; n < MelBands; n++)
                sum += mel[n] * Math.Cos(Math.PI * (k + 1) * (n + 0.5) / MelBands);
            result[k] = sum;
        }
        return result;
    }

    private static double[] MelEnergies(double[] power)
    {
        var mel = new double[MelBands];
        var lowMel = HzToMel(60);
        var highMel = HzToMel(7600);
        for (var m = 0; m < MelBands; m++)
        {
            var left = MelToHz(lowMel + (highMel - lowMel) * m / (MelBands + 1));
            var center = MelToHz(lowMel + (highMel - lowMel) * (m + 1) / (MelBands + 1));
            var right = MelToHz(lowMel + (highMel - lowMel) * (m + 2) / (MelBands + 1));
            var sum = 0d;
            for (var i = 1; i < power.Length; i++)
            {
                var hz = i * Rate / (double)FftSize;
                if (hz <= left || hz >= right) continue;
                var weight = hz <= center
                    ? (hz - left) / (center - left)
                    : (right - hz) / (right - center);
                sum += power[i] * weight;
            }
            mel[m] = Math.Log(Math.Max(sum, 1e-10));
        }
        return mel;
    }

    private static double HzToMel(double hz) => 2595 * Math.Log10(1 + hz / 700);
    private static double MelToHz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);

    private static double DtwSimilarity(List<double[]> left, List<double[]> right)
    {
        var n = left.Count;
        var m = right.Count;
        var previous = new double[m + 1];
        var current = new double[m + 1];
        for (var j = 0; j <= m; j++) previous[j] = j == 0 ? 0 : double.MaxValue / 4;
        for (var i = 1; i <= n; i++)
        {
            current[0] = double.MaxValue / 4;
            for (var j = 1; j <= m; j++)
            {
                var cost = Distance(left[i - 1], right[j - 1]);
                current[j] = cost + Math.Min(previous[j], Math.Min(current[j - 1], previous[j - 1]));
            }
            (previous, current) = (current, previous);
        }
        var normalised = previous[m] / (n + m);
        return 1 / (1 + normalised);
    }

    private static double Distance(double[] a, double[] b)
    {
        var sum = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            var delta = a[i] - b[i];
            sum += delta * delta;
        }
        return Math.Sqrt(sum / a.Length);
    }

    private static double Cosine(double[] a, double[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na <= 1e-12 || nb <= 1e-12 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>皮尔逊相关：先各自去均值再算余弦，只比“形状”，不受整体偏移影响。</summary>
    private static double Correlation(double[] a, double[] b)
    {
        var ma = a.Average();
        var mb = b.Average();
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i] - ma;
            var y = b[i] - mb;
            dot += x * y;
            na += x * x;
            nb += y * y;
        }
        return na <= 1e-12 || nb <= 1e-12 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>
    /// 按基频中位数粗判性别倾向。**只当提示，不当结论**：
    /// 低音女声、儿童音色、气声、以及混了音乐的短音轨都会落到交界或判错；
    /// 真正判断是谁得靠声纹，不是靠音高。
    /// </summary>
    private static string Gender(double f0) => f0 <= 0 ? "未知（没有可用的浊音帧）"
        : f0 < 150 ? "男声倾向"
        : f0 > 185 ? "女声倾向"
        : "不确定（150–185Hz 交界，可能偏低的女声或偏高的男声）";

    /// <summary>
    /// 音高 + F1/F2 三项投票的交叉结论。**两项以上同向才算数**，否则给“不确定”——
    /// 单看任何一项都会误判（低音女声、卡通音、混音乐的短音轨）。
    /// </summary>
    private static string GenderVerdict(double f0, double f1, double f2)
    {
        var score = 0;
        var cues = new List<string>();
        if (f0 > 0)
        {
            if (f0 < 150) { score--; cues.Add($"音高 {f0:0}Hz 偏男"); }
            else if (f0 > 185) { score++; cues.Add($"音高 {f0:0}Hz 偏女"); }
            else cues.Add($"音高 {f0:0}Hz 交界");
        }
        if (f2 > 0)
        {
            if (f2 < 1300) { score--; cues.Add($"F2 {f2:0}Hz 偏男"); }
            else if (f2 > 1550) { score++; cues.Add($"F2 {f2:0}Hz 偏女"); }
            else cues.Add($"F2 {f2:0}Hz 交界");
        }
        if (f1 > 0) cues.Add($"F1 {f1:0}Hz（参考，元音影响太大，不投票）");
        var verdict = score >= 2 ? "女声" : score <= -2 ? "男声" : "不确定";
        return $"{verdict}（{(cues.Count == 0 ? "没有可用线索" : string.Join("，", cues))}）";
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>
    /// LPC 求 F1/F2：Levinson-Durbin 解预测系数，再在 60–4000Hz 上扫对数包络取峰。
    /// 纯信号处理，没有任何模型；对噪声和气声敏感，所以只在能量明显高于门限的帧上调用。
    /// </summary>
    private static bool Formants(float[] frame, out double f1, out double f2)
    {
        f1 = 0;
        f2 = 0;
        const int order = 14;
        var window = new double[frame.Length];
        for (var i = 0; i < frame.Length; i++)
        {
            var previous = i == 0 ? frame[0] : frame[i - 1];
            window[i] = (frame[i] - 0.97 * previous) * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (frame.Length - 1)));
        }
        var a = Lpc(window, order);
        if (a.Length == 0) return false;

        const int step = 10;
        const int maxHz = 4000;
        var count = maxHz / step;
        var envelope = new double[count + 1];
        for (var bin = 0; bin <= count; bin++)
        {
            var w = 2 * Math.PI * bin * step / (double)Rate;
            double re = 1, im = 0;
            for (var k = 1; k <= order; k++)
            {
                re -= a[k] * Math.Cos(w * k);
                im += a[k] * Math.Sin(w * k);
            }
            var magnitude = Math.Sqrt(re * re + im * im);
            envelope[bin] = magnitude <= 1e-9 ? 0 : -Math.Log(magnitude);
        }
        var peaks = new List<double>();
        for (var bin = 1; bin < count; bin++)
            if (envelope[bin] > envelope[bin - 1] && envelope[bin] >= envelope[bin + 1] && envelope[bin] > 0)
                peaks.Add(bin * step);
        f1 = peaks.FirstOrDefault(hz => hz is >= 200 and <= 1100);
        f2 = peaks.FirstOrDefault(hz => hz is >= 900 and <= 2600);
        return f1 > 0 && f2 > 0;
    }

    /// <summary>Levinson-Durbin：自相关法求 LPC 系数（a[1..order]，a[0]=1）。</summary>
    private static double[] Lpc(double[] frame, int order)
    {
        var r = new double[order + 1];
        for (var lag = 0; lag <= order; lag++)
        {
            var sum = 0d;
            for (var i = lag; i < frame.Length; i++) sum += frame[i] * frame[i - lag];
            r[lag] = sum;
        }
        if (r[0] <= 1e-9) return Array.Empty<double>();
        var a = new double[order + 1];
        var temp = new double[order + 1];
        var error = r[0];
        for (var i = 1; i <= order; i++)
        {
            var acc = r[i];
            for (var j = 1; j < i; j++) acc -= a[j] * r[i - j];
            var k = acc / error;
            a[i] = k;
            for (var j = 1; j < i; j++) temp[j] = a[j] - k * a[i - j];
            for (var j = 1; j < i; j++) a[j] = temp[j];
            error *= 1 - k * k;
            if (error <= 0) break;
        }
        return a;
    }

    /// <summary>
    /// 自相关法取基频中位数。**取“第一个足够强的峰”而不是全局最大峰**：
    /// 全局最大容易被低八度抢走（周期 2T 的相关往往也高），那正是上一轮把 -15% 判成同人的原因。
    /// </summary>
    private static double Pitch(float[] samples)
    {
        var values = new List<double>();
        const int size = 640;   // 40ms
        const int hop = 160;
        var minLag = Rate / 400;
        var maxLag = Rate / 60;
        var corr = new double[maxLag + 1];
        for (var start = 0; start + size + maxLag <= samples.Length; start += hop)
        {
            var window = samples[start..(start + size)];
            var energy = 0d;
            foreach (var value in window) energy += value * value;
            if (energy < 1e-5) continue;

            double peak = 0;
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                double sum = 0, tail = 0;
                for (var i = 0; i + lag < size; i++)
                {
                    sum += window[i] * window[i + lag];
                    tail += window[i + lag] * window[i + lag];
                }
                corr[lag] = tail <= 1e-9 ? 0 : sum / Math.Sqrt(energy * tail);
                peak = Math.Max(peak, corr[lag]);
            }
            if (peak < 0.3) continue;
            var gate = 0.85 * peak;
            for (var lag = minLag + 1; lag < maxLag; lag++)
            {
                if (corr[lag] < gate || corr[lag] < corr[lag - 1] || corr[lag] < corr[lag + 1]) continue;
                values.Add((double)Rate / lag);
                break;
            }
        }
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var start = 0; start < n; start += len)
            {
                double curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int u = start + k, v = start + k + len / 2;
                    var vr = re[v] * curRe - im[v] * curIm;
                    var vi = re[v] * curIm + im[v] * curRe;
                    re[v] = re[u] - vr;
                    im[v] = im[u] - vi;
                    re[u] += vr;
                    im[u] += vi;
                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }
    }

    /// <summary>线性插值重采样：只用于“变速/漂移”这类对照，不追求音质。</summary>
    private static float[] Resample(float[] samples, int from, int to)
    {
        if (from == to) return samples;
        var length = (int)(samples.Length * (double)to / from);
        var result = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * (double)from / to;
            var index = (int)position;
            var fraction = position - index;
            var next = Math.Min(index + 1, samples.Length - 1);
            result[i] = (float)(samples[index] * (1 - fraction) + samples[next] * fraction);
        }
        return result;
    }

    /// <summary>用系统的 Media Foundation 解 MP4 的 AAC，再降混成 16k 单声道。</summary>
    private static float[] LoadMono(string path)
    {
        using var reader = new MediaFoundationReader(path);
        var format = reader.WaveFormat;
        using var buffer = new MemoryStream();
        reader.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var channels = format.Channels;
        var mono = new float[bytes.Length / (format.BitsPerSample / 8) / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            var sum = 0d;
            for (var channel = 0; channel < channels; channel++)
            {
                var offset = (i * channels + channel) * (format.BitsPerSample / 8);
                sum += format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32
                    ? BitConverter.ToSingle(bytes, offset)
                    : BitConverter.ToInt16(bytes, offset) / 32768d;
            }
            mono[i] = (float)(sum / channels);
        }
        return format.SampleRate == Rate ? mono : Resample(mono, format.SampleRate, Rate);
    }

    private static string Describe(float[] samples) =>
        $"{samples.Length / (double)Rate:0.00}s · {Rate}Hz · peak={samples.Max(Math.Abs):0.00}";
}
