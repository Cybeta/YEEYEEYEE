using System.Globalization;

namespace YEEYEEYEE.Host;

/// <summary>
/// 「画幅」这一件事的算法：比例怎么认、比例怎么写成工作流认的写法、按比例与目标像素算成具体的宽高。
///
/// 为什么单独一个文件：这三件事都是纯算术，谁都要用（绑定器要写、界面要显示、出视频那条链要换算），
/// 散在三处的话「9:16 到底算成多少像素」会有三个答案，而用户只会看到一个不对劲的结果。
/// </summary>
public static class VideoShape
{
    /// <summary>视频模型对宽高几乎都有对齐要求（16 最常见）。我们按 16 取整并**说出来**，不悄悄替它取整。</summary>
    public const int SizeQuantum = 16;

    /// <summary>
    /// 认比例：从一串文字里读出「几比几」。认得 <c>9:16</c> / <c>9x16</c> / <c>1080x1920</c> / <c>9/16</c>，
    /// 也认带后缀的（<c>16:9 (横屏)</c>）。读不出就返回 false——**读不出时不猜**，
    /// 猜错的代价是把一个工作流不认识的字符串写进去，换来一次白跑的报错。
    /// </summary>
    public static bool TryParseRatio(string? text, out int first, out int second)
    {
        first = 0;
        second = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var digits = new List<int>();
        var index = 0;
        while (index < text.Length && digits.Count < 2)
        {
            if (!char.IsDigit(text[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsDigit(text[index])) index++;
            if (!int.TryParse(text[start..index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) break;
            digits.Add(value);
        }

        if (digits.Count < 2) return false;
        if (digits[0] <= 0 || digits[1] <= 0) return false;
        // 比例的两个数不该大得离谱（超过 100 多半是把 "1080x1920" 之外的东西读进来了，例如 "step 20"）。
        if (digits[0] > 10000 || digits[1] > 10000) return false;

        first = digits[0];
        second = digits[1];
        return true;
    }

    /// <summary>
    /// 把一个想要的比例写成**那份工作流认的写法**。做法不是自己造格式，而是：
    /// ① 想要的和它当前那个值同比例 → 原样不动；
    /// ② <paramref name="known"/> 里有同比例的值 → 直接用那个（见下）；
    /// ③ 是它当前值的**倒数**（16:9 对 9:16）→ 照它自己的数字量级与分隔符交换两个数（`1920x1080` → `1080x1920`）；
    /// ④ 都不是（例如它写 16:9 而你要 1:1）→ 用它当前值的分隔符拼一个。
    ///
    /// <paramref name="known"/> 是**这台服务器上同一个节点类型 + 同一个输入名真实用过的值**
    /// （导入时全库扫出来，存在站点文件的 <c>OptionValues</c> 里）。
    /// 为什么要它：合法选项清单只在服务端的 <c>object_info</c> 里，工作流文件里只有当前选中的那一个，
    /// 而「把当前值里的数字换掉」会造出服务端不认的字符串——实测 `ResolutionSelector.aspect_ratio`
    /// 当前是 `16:9 (Widescreen)`，改成 9:16 时按数字替换得到 `9:16 (Widescreen)`，
    /// 合法值却是 `9:16 (Portrait Widescreen)`（括号里的朝向词并不跟着数字走），提交被 400 拒。
    /// 库里见过的值就绕开了这件事：写回去的一定是它认识的。
    ///
    /// 拼不出来（当前值里没有两个数，例如 `adaptive`，而 known 里也没有同比例的）就返回空字符串，
    /// 由调用方如实说改不了。
    /// </summary>
    public static string FormatAspect(
        string current, string wanted, IReadOnlyCollection<string>? known = null)
    {
        if (wanted.Length == 0) return string.Empty;
        if (!TryParseRatio(wanted, out var wantFirst, out var wantSecond)) return string.Empty;

        // 同比例：一个字都不用改（先判它，免得把一个已经正确的值换成清单里另一个同比例的写法）。
        if (TryParseRatio(current, out var haveFirst, out var haveSecond)
            && SameRatio(wantFirst, wantSecond, haveFirst, haveSecond))
            return current;

        if (known is not null)
        {
            foreach (var option in known)
                if (TryParseRatio(option, out var optionFirst, out var optionSecond)
                    && SameRatio(wantFirst, wantSecond, optionFirst, optionSecond))
                    return option;
        }

        if (!TryParseRatio(current, out haveFirst, out haveSecond)) return string.Empty;

        // 倒数：交换它自己的两个数，连分隔符一起保留。
        if (SameRatio(wantFirst, wantSecond, haveSecond, haveFirst))
            return ReplaceNumbers(current, haveSecond, haveFirst);

        var separator = SeparatorOf(current);
        return $"{wantFirst}{separator}{wantSecond}";
    }

    /// <summary>
    /// 按「比例 + 目标像素」算出具体的宽高，都对齐到 <see cref="SizeQuantum"/> 的整数倍。
    /// <paramref name="megapixels"/> 给 0 时按 1.0MP 算。
    ///
    /// 返回的说明是**要显示给用户的**：它得能解释「你要的 9:16 / 1MP 为什么落成了 720×1280」。
    /// </summary>
    public static (int Width, int Height, string Note) Resolve(string aspectRatio, double megapixels)
    {
        if (!TryParseRatio(aspectRatio, out var first, out var second))
            return (0, 0, $"比例「{aspectRatio}」认不出来（要 9:16 这种写法）：尺寸交给那份工作流自己定。");

        var target = megapixels > 0 ? megapixels : 1.0;
        var area = target * 1_000_000.0;
        // 按等比缩放保持比例：以高为基准解出宽，再各自取整。
        var height = Math.Sqrt(area * second / first);
        var width = height * first / second;

        var roundedWidth = Quantize(width);
        var roundedHeight = Quantize(height);
        return (roundedWidth, roundedHeight,
            $"{first}:{second} · {target:0.##}MP 算成 {roundedWidth}×{roundedHeight}"
            + $"（宽高都取 {SizeQuantum} 的整数倍：视频模型大多有这个要求）");
    }

    /// <summary>取整到 <see cref="SizeQuantum"/> 的整数倍，且不小于一个量子。</summary>
    public static int Quantize(double value)
    {
        var steps = (int)Math.Round(value / SizeQuantum, MidpointRounding.AwayFromZero);
        return Math.Max(SizeQuantum, steps * SizeQuantum);
    }

    /// <summary>把「1080x1920」里的两个数按顺序换成新的两个数，分隔符原样保留。</summary>
    private static string ReplaceNumbers(string text, int first, int second)
    {
        var buffer = new System.Text.StringBuilder();
        var written = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (!char.IsDigit(text[index]))
            {
                buffer.Append(text[index]);
                index++;
                continue;
            }

            var start = index;
            while (index < text.Length && char.IsDigit(text[index])) index++;
            if (written == 0) buffer.Append(first.ToString(CultureInfo.InvariantCulture));
            else if (written == 1) buffer.Append(second.ToString(CultureInfo.InvariantCulture));
            else buffer.Append(text[start..index]);
            written++;
        }

        return buffer.ToString();
    }

    private static string SeparatorOf(string text)
    {
        var seenDigit = false;
        var buffer = new System.Text.StringBuilder();
        foreach (var character in text)
        {
            if (char.IsDigit(character))
            {
                if (seenDigit && buffer.Length > 0) return buffer.ToString().Trim();
                seenDigit = true;
                buffer.Clear();
                continue;
            }

            if (seenDigit && !char.IsWhiteSpace(character)) buffer.Append(character);
        }

        return ":";
    }

    /// <summary>两个比例是不是同一个比例（交叉相乘比较，不看数字大小）。</summary>
    private static bool SameRatio(int leftFirst, int leftSecond, int rightFirst, int rightSecond) =>
        (long)leftFirst * rightSecond == (long)rightFirst * leftSecond;
}

/// <summary>
/// 把「我要几秒」换算成工作流要的**帧数**。
///
/// 为什么需要一个单独的算法：帧数不是随便一个数就行的——大多数视频模型要求帧数落在一个「家族」里
/// （实测那份 Wan 工作流的 121 帧就是 <c>4n+1</c> 也是 <c>8n+1</c> 的形状）。直接写 15 秒 × 24fps = 360，
/// 服务器可能直接拒绝。所以这里做一件很省事但很关键的事：**从那份工作流自己的帧数反推出它属于哪个家族**，
/// 再把我们要的帧数贴到同一个家族上。
///
/// 这不是「猜」：模数是从它自己的值里**推**出来的（121-1=120，能被 8 整除），
/// 而且如果推不出来（原值减去 1 之后既不被 4 整除也没有别的线索），就按四舍五入写，并如实说明。
/// </summary>
public static class VideoFrameMath
{
    /// <summary>帧数家族的候选模数，从严格到宽松。挑**最大的那个**能整除原值减一的。</summary>
    private static readonly int[] ModulusCandidates = { 32, 16, 8, 4 };

    /// <summary>
    /// 换算。<paramref name="currentFrames"/> 是那份工作流现在的帧数（拿来推家族，没有就传 null）。
    /// 返回 false 表示「换算不出来」（秒数或帧率不可用），调用方应如实说「时长由它自己决定」。
    /// </summary>
    public static bool TryFrames(int seconds, double framesPerSecond, int? currentFrames, out int frames, out string note)
    {
        frames = 0;
        note = string.Empty;
        if (seconds <= 0) return false;
        if (framesPerSecond <= 0)
        {
            note = "这份工作流里找不到帧率，没法把秒数换算成帧数：时长由它自己决定。";
            return false;
        }

        var wanted = (int)Math.Round(seconds * framesPerSecond, MidpointRounding.AwayFromZero);
        if (wanted < 1) wanted = 1;

        if (currentFrames is not { } current || current <= 1)
        {
            frames = wanted;
            note = $"{seconds} 秒 × {framesPerSecond:0.##}fps = {frames} 帧"
                + "（这份工作流没有原帧数可参照，就按四舍五入写）。";
            return true;
        }

        var modulus = 0;
        foreach (var candidate in ModulusCandidates)
        {
            if ((current - 1) % candidate != 0) continue;
            modulus = candidate;
            break;
        }

        if (modulus == 0)
        {
            frames = wanted;
            note = $"{seconds} 秒 × {framesPerSecond:0.##}fps = {frames} 帧"
                + $"（它原本是 {current} 帧，看不出要对齐到哪个家族，就按四舍五入写）。";
            return true;
        }

        var steps = (int)Math.Round((wanted - 1) / (double)modulus, MidpointRounding.AwayFromZero);
        frames = Math.Max(1, steps * modulus + 1);
        note = $"{seconds} 秒 × {framesPerSecond:0.##}fps = {wanted} 帧，"
            + $"按它原本的 {current} 帧推出来这个模型要 {modulus}n+1，所以写成 {frames} 帧"
            + $"（= {modulus}×{steps}+1）。帧率本身没动：改了它动作的快慢也跟着变。";
        return true;
    }
}
