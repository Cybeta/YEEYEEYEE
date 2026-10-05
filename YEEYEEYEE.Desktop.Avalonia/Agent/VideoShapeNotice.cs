using YEEYEEYEE.Host;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 出视频那三样选项（比例 / 目标像素 / 时长）在**点下去之前**的说明。
///
/// 为什么单独一份：这三样改不改得动，答案只在**那份工作流自己的正文**里——有没有帧数入口、
/// 有没有比例选项、有没有可写的宽高。界面不该自己编一套判断，只该把
/// <see cref="ComfyUiWorkflowSlots"/> 已经认出来的结论，以及 <see cref="VideoShape"/> /
/// <see cref="VideoFrameMath"/> 的真实换算，拼成给人看的话。
///
/// 所以这里的方法**只读取槽位结论、只调用那两处纯算法**，不新增任何判断。
/// </summary>
internal static class VideoShapeNotice
{
    /// <summary>
    /// 走 ComfyUI 工作流时的说明：第一行是与选择器同源的总账（<see cref="ComfyUiWorkflowSlots.Describe"/>，
    /// 里面就有「时长✓（121 帧 × 24fps）/ 比例✓ / 画幅✗」），后面逐项说清**为什么**改得动或改不动，
    /// 以及这一次指定了值时会写成什么。
    /// </summary>
    /// <param name="aspectRatio">这一次想要的比例（空 = 跟随首帧）。</param>
    /// <param name="megapixels">这一次想要的目标像素（0 = 不指定，按 1.0 算）。</param>
    /// <param name="seconds">这一次想要的秒数（0 = 不指定）。</param>
    public static string DescribeWorkflow(
        ComfyUiWorkflowSlots slots, string aspectRatio, double megapixels, int seconds)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var lines = new List<string> { "这份工作流能收到：" + slots.Describe() };

        // 时长：改不动就当面说清是哪一种「改不动」；改得动就把这次要写的帧数真算出来。
        if (slots.CanSetLength)
        {
            if (seconds > 0)
                lines.Add(VideoFrameMath.TryFrames(seconds, slots.FrameRateValue ?? 0, slots.LengthCurrent,
                        out _, out var frameNote)
                    ? "· 时长：" + frameNote
                    : "· 时长✗：" + frameNote);
            else
                lines.Add(slots.LengthCurrent is { } current
                    ? $"· 时长：这次不指定秒数，沿用它的 {current} 帧。"
                    : "· 时长：这次不指定秒数，沿用它的帧数设置。");
        }
        else if (slots.CanSetSeconds)
        {
            // 帧数是算出来的那种：写的是**秒**，帧数与对齐由它自己的表达式折。
            lines.Add(seconds > 0
                ? $"· 时长：写 {seconds} 秒（这份工作流的帧数是**算出来**的，不是写死的）：" + slots.SecondsChain
                : "· 时长：这次不指定秒数，沿用它的默认值；要改的话这里写的是**秒数**（帧数由它自己的表达式折）。");
        }
        else
        {
            lines.Add(slots.LengthCurrent is { } fixedFrames
                ? $"· 时长✗：这份工作流没有帧数入口（生成侧的 length / num_frames 这类），这次改不了时长——"
                  + $"它出多少就是多少（当前设定 {fixedFrames} 帧）。"
                : "· 时长✗：这份工作流没有帧数入口，时长由它自己决定。");
        }

        // 比例：先看「这台服务器上同一个节点用过的值」里有没有我们要的那一档（那是最稳的），
        // 没有才退回「照它当前值的写法造一个」——造不出来就如实说改不了。
        if (!slots.CanSetAspect)
        {
            lines.Add("· 比例✗：这份工作流没有 aspect_ratio 这类比例选项，比例改不了。");
        }
        else if (aspectRatio.Length == 0)
        {
            lines.Add($"· 比例✓：它当前是「{slots.AspectCurrent}」，选一个比例就会写进去。");
        }
        else
        {
            var written = VideoShape.FormatAspect(slots.AspectCurrent, aspectRatio, slots.AspectOptions);
            if (written.Length == 0)
                lines.Add($"· 比例✗：它的比例值「{slots.AspectCurrent}」不是我们能改的写法，比例改不了（要改请在那份工作流里改）。");
            else if (string.Equals(written, slots.AspectCurrent, System.StringComparison.Ordinal))
                lines.Add($"· 比例✓：它当前写的就是「{written}」，这一项不用动。");
            else if (slots.AspectOptions.Contains(written))
                lines.Add($"· 比例✓：当前「{slots.AspectCurrent}」→ 写成「{written}」"
                    + $"（它在别的 {slots.AspectOptions.Count} 个值里，是这台服务器上真用过的写法）。");
            else
                lines.Add($"· 比例：当前「{slots.AspectCurrent}」→ 会写成「{written}」"
                    + "（照它自己的分隔符拼的：这个值不一定在它的选项清单里，提交可能被服务端拒）。");
        }

        // 画幅：要先有可写的 width / height，比例与目标像素才落得成具体像素。
        if (!slots.CanResize)
        {
            lines.Add("· 画幅✗：尺寸由它上游的节点或输入图决定，出多大就是多大；"
                + "要指定比例，最稳的办法是把首帧按那个比例出好再喂进来。");
        }
        else if (aspectRatio.Length == 0)
        {
            lines.Add("· 画幅✓：它认 width / height，但不选比例时就不写（按首帧的比例走）。");
        }
        else
        {
            lines.Add("· 画幅：" + VideoShape.Resolve(aspectRatio, megapixels).Note + "。");
        }

        if (slots.IsFrameDriven)
        {
            lines.Add("· 提示词—（只吃首帧）：SVD / 动作迁移 / 人物替换这一类是正当用法——"
                + "它靠首帧动起来、不收文字，你写的提示词不会进工作流。");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// 走接口站池子时的说明：这条路只发时长——比例与目标像素不适用，就明说不适用，
    /// 不假装它们会生效。时长本身有没有对上清单档位由调用方另行说明（<see cref="VideoDurationPolicy"/>）。
    /// </summary>
    public static string DescribePool(int seconds) =>
        (seconds > 0
            ? $"接口站这条路：时长会按 {seconds} 秒发出去。"
            : "接口站这条路：时长留空，由服务端按模型的默认值决定。")
        + "\n· 比例✗ / 目标像素✗：这条路只发时长，画幅由服务端按模型的默认档位决定——"
        + "比例与目标像素不会发出去，选了也不生效。";
}
