namespace YEEYEEYEE.Desktop;

/// <summary>
/// 画布标签的规则（UI-free）：编号分配与空白画布的造法。
///
/// 为什么放在共享源码里而不是直接写在某个客户端的按钮回调里：
/// 1）它决定新画布叫什么名字，而**画布标题同时是画布库里的文件名**——编号撞了就会覆盖掉别人的画布，
///    这条规则必须能被测试盯住；
/// 2）旧端与新主端都可能有「新建画布」，两边各写一套的话，同名的判定迟早会分叉。
/// </summary>
public static class CanvasTabRules
{
    /// <summary>
    /// 「画布1 / 画布2 …」：取第一个没被占用的编号。
    /// 传入的已占用标题应包含：界面上的标签标题、画布库里的画布标题、画布库目录里读不出来的文件名
    /// （坏文件不会出现在画布库里，但同样不能被新画布覆盖）。
    /// </summary>
    public static string NextTitle(IEnumerable<string> takenTitles)
    {
        var used = new HashSet<int>();
        foreach (var title in takenTitles)
        {
            var text = title?.Trim() ?? string.Empty;
            if (text.Length < 3 || text[0] != '画' || text[1] != '布') continue;
            var digits = text[2..].Trim();
            if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) continue;
            if (int.TryParse(digits, out var number) && number > 0) used.Add(number);
        }

        for (var candidate = 1; ; candidate++)
            if (!used.Contains(candidate)) return $"画布{candidate}";
    }

    /// <summary>
    /// 空白画布的唯一造法：自动建的「画布1」与点 ＋ 建的画布必须是同一种，
    /// 否则用户会看到两种新画布（一个有「开始」节点、一个什么都没有）。
    /// </summary>
    public static RecentCanvasState CreateBlank(string title)
    {
        var canvas = new WorkflowCanvasState();
        canvas.Nodes.Add(new WorkflowNode { Title = "开始" });
        return new RecentCanvasState(title, 1, string.Empty, string.Empty, 1920, 1080, 20, 7, string.Empty, canvas);
    }

    /// <summary>
    /// 改名前的查重：不通过返回原因（可直接显示给用户），通过返回 null。
    ///
    /// 为什么必须查：画布标题就是画布库里的**文件名**，改成一个已经存在的标题就等于把那张画布覆盖掉。
    /// 旧端在重名时会问「是否覆盖」，这里一律拒绝——旧端的保存链写前有备份，覆盖还能捞回来，
    /// 新主端的画布库保存是原子替换，覆盖下去就没了，这种按钮不该摆在一次误点的距离内。
    /// </summary>
    public static string? DescribeNameConflict(string candidate, IEnumerable<string> takenTitles)
    {
        var name = (candidate ?? string.Empty).Trim();
        if (name.Length == 0) return "画布名称不能为空。";
        if (name.Length > 80) return "画布名称太长了（文件名只取前 80 个字符），请改短一些。";

        foreach (var taken in takenTitles)
        {
            if (string.Equals(taken?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                return $"已经有一个叫「{name}」的画布（标签或画布库里的文件）：换一个名字，免得把它覆盖掉。";
        }

        return null;
    }
}
