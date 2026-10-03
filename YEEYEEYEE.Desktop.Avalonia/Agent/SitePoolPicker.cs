using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YEEYEEYEE.Desktop;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 「调用前问一句用哪个」的那个窗口。
///
/// 为什么要在调用前问：一家接口站下面几十个池子（真实聚合站一家就有 60 多个生图池），
/// 价格与能力都不一样——同一句话用 1K 还是 4K、用哪个池，出的钱能差五六倍；
/// 一台 ComfyUI 上又有几百份工作流，用哪一份出的图完全不是一个路子。
/// 让人在花这笔钱之前自己挑，比替他挑一个「默认的」要正当得多。
///
/// **先选通道再选子项**（接口站的池子 / ComfyUI 的工作流）：
/// 这两种来源的「子项」不是同一种东西，混在一个下拉里会让人看不懂自己在选什么。
/// 选完通道之后，ComfyUI 那一路给出「家族 → 工作流」两级，并在下面标出该家族的推荐项。
/// </summary>
internal static class SitePoolPicker
{
    public static async Task<ImageSourceChoice?> ShowAsync(
        Window owner,
        IReadOnlyList<SiteProfile> sites,
        string nodeTitle,
        SitePoolChoice? preset = null,
        SiteWorkflowChoice? presetWorkflow = null,
        bool? video = null)
    {
        // 只要某一类时（一键出图 / 一键出视频那两个入口），候选里就不出现另一类——
        // 「出图选到视频池子、出视频选到图像池子」这种错，选完才发现已经晚了。
        IReadOnlyList<SitePool> PoolsOf(SiteProfile site) => video is { } want
            ? site.UsablePools.Where(pool => pool.IsVideo == want).ToList()
            : site.UsablePools;

        IReadOnlyList<SiteWorkflow> WorkflowsOf(SiteProfile site) => video is { } want
            ? site.UsableWorkflows.Where(workflow => workflow.IsVideo == want).ToList()
            : site.UsableWorkflows;

        var poolSites = sites.Where(site => PoolsOf(site).Count > 0).ToList();
        // 出视频这条路**暂时不列 ComfyUI 工作流**：那条链只认接口站的视频池子（提交 → 轮询 → 下载），
        // 还没接上 ComfyUI 的工作流提交。给一个选了必然失败的选项，比暂时不列更坏——
        // 用户会以为是自己选错了。所以这里列不出来，并且在下面对话说清。
        var comfySites = video == true
            ? new List<SiteProfile>()
            : sites.Where(site => site.IsComfyUi && WorkflowsOf(site).Count > 0).ToList();

        if (poolSites.Count == 0 && comfySites.Count == 0)
        {
            var wanted = video is { } kind ? (kind ? "出视频" : "出图") : string.Empty;
            await ConfirmAsync(owner, "还没有可用的接口",
                $"当前没有登记任何能用的{wanted}接口：既没有接口站的池子，也没有 ComfyUI 的工作流。\n"
                + "到「设置 → 生图与生视频」，用顶端的「智能导入」：给一个接口说明网页会登记成站点并带出池子；"
                + "给一个 ComfyUI 地址会把那台服务器的工作流整份拉下来。",
                "知道了");
            return null;
        }

        ImageSourceChoice? picked = null;

        // ---------- 通道 ----------
        var channelBox = Combo();
        var channels = new List<string>();
        if (poolSites.Count > 0)
            channels.Add(video is { } direction
                ? $"接口站的{(direction ? "出视频" : "出图")}池子（{poolSites.Sum(site => PoolsOf(site).Count)} 个）"
                : $"接口站的池子（{poolSites.Sum(site => PoolsOf(site).Count)} 个）");
        if (comfySites.Count > 0)
            channels.Add(video is { } direction2
                ? $"ComfyUI 的{(direction2 ? "出视频" : "出图")}工作流（{comfySites.Sum(site => WorkflowsOf(site).Count)} 份）"
                : $"ComfyUI 的工作流（{comfySites.Sum(site => WorkflowsOf(site).Count)} 份）");
        foreach (var channel in channels) channelBox.Items.Add(channel);

        const int ChannelPool = 0;
        var comfyChannelIndex = poolSites.Count > 0 ? 1 : 0;

        // ---------- 池子那一路 ----------
        var siteBox = Combo();
        var modelBox = Combo();
        var tierBox = Combo();
        var poolDetail = Note(string.Empty);
        poolDetail.TextWrapping = TextWrapping.Wrap;
        var pathHint = Note(string.Empty);
        pathHint.TextWrapping = TextWrapping.Wrap;

        // 两个下拉的候选按三层联动重建；重建期间置真，避免程序设值被当成用户选择。
        var loading = false;
        var models = new List<IGrouping<string, SitePool>>();

        SitePool? CurrentPool(int modelIndex, int tierIndex)
        {
            if (modelIndex < 0 || modelIndex >= models.Count) return null;
            var pools = models[modelIndex].ToList();
            return tierIndex >= 0 && tierIndex < pools.Count ? pools[tierIndex] : null;
        }

        void RefreshTiers()
        {
            loading = true;
            try
            {
                tierBox.Items.Clear();
                var index = modelBox.SelectedIndex;
                if (index < 0 || index >= models.Count)
                {
                    poolDetail.Text = "先选一个模型。";
                    return;
                }
                foreach (var pool in models[index]) tierBox.Items.Add(pool.Tier.Length == 0 ? "（该模型没声明档位）" : pool.Tier);
                tierBox.SelectedIndex = 0;
                DescribePool();
            }
            finally { loading = false; }
        }

        void DescribePool()
        {
            var pool = CurrentPool(modelBox.SelectedIndex, tierBox.SelectedIndex);
            if (pool is null) { poolDetail.Text = string.Empty; return; }
            poolDetail.Text = string.Join(" · ", pool.IsVideo ? "出视频" : "出图", pool.Describe())
                + (pool.IsVideo ? "\n注意：这是出视频池子，出图选它没用。" : string.Empty);
        }

        void RefreshModels()
        {
            loading = true;
            try
            {
                modelBox.Items.Clear();
                tierBox.Items.Clear();
                models = new List<IGrouping<string, SitePool>>();
                var index = siteBox.SelectedIndex;
                if (index < 0 || index >= poolSites.Count)
                {
                    poolDetail.Text = string.Empty;
                    return;
                }
                // 模型层按「种类 + 模型名」聚合：同一个模型的不同档位是同一个模型的三个选项，不该平铺成三行。
                models = PoolsOf(poolSites[index])
                    .GroupBy(pool => $"{(pool.IsVideo ? "视频" : "图像")} · {pool.Model}")
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToList();
                foreach (var group in models) modelBox.Items.Add($"{group.Key}（{group.Count()} 档）");
                modelBox.SelectedIndex = models.Count > 0 ? 0 : -1;
            }
            finally { loading = false; }
            RefreshTiers();
        }

        void ShowPaths()
        {
            var index = siteBox.SelectedIndex;
            if (index < 0 || index >= poolSites.Count) { pathHint.Text = string.Empty; return; }
            var site = poolSites[index];
            var paths = new List<string>();
            if (site.ImagePath.Length > 0) paths.Add($"文生图 {site.Method} {site.ImagePath}");
            if (site.ImageEditPath.Length > 0) paths.Add($"图生图 {site.Method} {site.ImageEditPath}");
            if (site.VideoPath.Length > 0) paths.Add($"出视频 {site.Method} {site.VideoPath}");
            pathHint.Text = $"接口：{site.BaseUrl}" + (paths.Count == 0 ? "（文档里没解析出接口路径）" : "｜" + string.Join("｜", paths));
        }

        siteBox.SelectionChanged += (_, _) => { if (loading) return; RefreshModels(); ShowPaths(); };
        modelBox.SelectionChanged += (_, _) => { if (!loading) RefreshTiers(); };
        tierBox.SelectionChanged += (_, _) => { if (!loading) DescribePool(); };

        foreach (var site in poolSites)
            siteBox.Items.Add(video is { } kind
                ? $"{site.Label}（{(kind ? "视频" : "生图")} {PoolsOf(site).Count}）"
                : $"{site.Label}（生图 {site.ImagePools.Count} / 视频 {site.VideoPools.Count}）");
        siteBox.SelectedIndex = 0;

        // ---------- ComfyUI 那一路 ----------
        var comfySiteBox = Combo();
        var folderBox = Combo();
        var workflowBox = Combo();
        var recommendNote = Note(string.Empty);
        recommendNote.TextWrapping = TextWrapping.Wrap;
        var workflowDetail = Note(string.Empty);
        workflowDetail.TextWrapping = TextWrapping.Wrap;

        var workflows = new List<SiteWorkflow>();
        var comfyLoading = false;

        SiteWorkflow? CurrentWorkflow() =>
            workflowBox.SelectedIndex >= 0 && workflowBox.SelectedIndex < workflows.Count
                ? workflows[workflowBox.SelectedIndex]
                : null;

        void DescribeWorkflow()
        {
            var workflow = CurrentWorkflow();
            if (workflow is null) { workflowDetail.Text = string.Empty; return; }

            var siteIndex = comfySiteBox.SelectedIndex;
            if (siteIndex < 0 || siteIndex >= comfySites.Count) { workflowDetail.Text = string.Empty; return; }
            var site = comfySites[siteIndex];

            // **在选之前就把这份工作流能收到什么说清**：认不出收提示词的位置时，选它出图会以失败告终
            // （那是正确的做法——静默把提示词丢掉、跑出导出时那张图才是最坏的）。
            // 与其等人跑一次才知道，不如在挑选这一刻就把话说出来。
            var (slots, error) = ComfyUiWorkflowInspector.Inspect(site, workflow);
            if (slots is null)
            {
                workflowDetail.Text = $"这份用不了：{error}";
                return;
            }

            var lines = new List<string>
            {
                $"这份工作流能收到：{slots.Describe()}",
                workflow.Note.Length > 0 ? "转换时的说明：" + workflow.Note : "转换时没有被跳过的节点。"
            };
            foreach (var note in slots.Notes.Take(3)) lines.Add("· " + note);
            if (slots.Notes.Count > 3) lines.Add($"· （另有 {slots.Notes.Count - 3} 条说明，出图时在状态区里能看到）");
            lines.Add("工作流的底模、步数、时长由它自己决定：我们只往里写提示词、负面词、画幅与种子。");
            workflowDetail.Text = string.Join("\n", lines);
        }

        void RefreshWorkflows()
        {
            comfyLoading = true;
            try
            {
                workflowBox.Items.Clear();
                workflows = new List<SiteWorkflow>();
                var index = comfySiteBox.SelectedIndex;
                if (index < 0 || index >= comfySites.Count)
                {
                    workflowDetail.Text = string.Empty;
                    recommendNote.Text = string.Empty;
                    return;
                }

                var site = comfySites[index];
                folderBox.Items.Clear();
                var folderIndex = folderBox.SelectedIndex;
                var folders = WorkflowsOf(site)
                    .GroupBy(workflow => workflow.Folder.Length == 0 ? "（根目录）" : workflow.Folder)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToList();
                foreach (var group in folders) folderBox.Items.Add($"{group.Key}（{group.Count()} 份）");
                folderBox.SelectedIndex = folderIndex >= 0 && folderIndex < folders.Count ? folderIndex : 0;

                if (folderBox.SelectedIndex < 0 || folderBox.SelectedIndex >= folders.Count)
                {
                    recommendNote.Text = string.Empty;
                    return;
                }

                // 推荐的那份排在第一位，并且标出来——它是这个家族里的默认选择。
                workflows = folders[folderBox.SelectedIndex]
                    .OrderByDescending(workflow => workflow.Recommended)
                    .ThenBy(workflow => workflow.NodeCount)
                    .ThenBy(workflow => workflow.Title, StringComparer.Ordinal)
                    .ToList();
                foreach (var workflow in workflows)
                    workflowBox.Items.Add(workflow.Title
                        + (workflow.Recommended ? "（推荐）" : string.Empty)
                        + $"｜节点 {workflow.NodeCount}");
                workflowBox.SelectedIndex = workflows.Count > 0 ? 0 : -1;

                var recommended = workflows.FirstOrDefault(workflow => workflow.Recommended);
                recommendNote.Text = recommended is null
                    ? "这个家族里没有推荐项（要么一份都没转成，要么都停用了）：从上到下按节点数排，越靠上越省事。"
                    : $"推荐用这一份：{recommended.Title}（节点 {recommended.NodeCount} 个，这个家族里最省事的一份）。"
                      + "理由：节点越少，能出错的地方越少，也更可能是这个家族的正路用法。";
            }
            finally { comfyLoading = false; }
            DescribeWorkflow();
        }

        comfySiteBox.SelectionChanged += (_, _) => { if (!comfyLoading) RefreshWorkflows(); };
        folderBox.SelectionChanged += (_, _) => { if (!comfyLoading) RefreshWorkflows(); };
        workflowBox.SelectionChanged += (_, _) => { if (!comfyLoading) DescribeWorkflow(); };

        foreach (var site in comfySites)
            comfySiteBox.Items.Add($"{site.Label}（图像 {site.ImageWorkflows.Count} / 视频 {site.VideoWorkflows.Count}）");
        comfySiteBox.SelectedIndex = 0;

        // ---------- 通道切换 ----------
        var poolSection = new StackPanel { Spacing = 6 };
        var comfySection = new StackPanel { Spacing = 6 };
        var note = Note("接口站下面每个池子的价格与能力都不一样；一台 ComfyUI 上不同工作流出图的路子也不同。"
            + "这一次挑哪个就只用哪个——接口站那边的池子会被记住，下次预选你上次用的那一个。");
        if (video == true)
            note.Text += "\n出视频这一路只列接口站的池子：ComfyUI 的出视频工作流还没接上提交链（那要另做一条链），"
                + "所以这里不列出来——不给你一个选了也跑不了的选项。";

        void ApplyChannel()
        {
            var isComfy = channelBox.SelectedIndex == comfyChannelIndex && comfySites.Count > 0;
            poolSection.IsVisible = !isComfy;
            comfySection.IsVisible = isComfy;
            if (isComfy) RefreshWorkflows();
            else { RefreshModels(); ShowPaths(); }
        }

        channelBox.SelectionChanged += (_, _) => { if (!loading) ApplyChannel(); };

        poolSection.Children.Add(Header("站点"));
        poolSection.Children.Add(siteBox);
        poolSection.Children.Add(Header("模型"));
        poolSection.Children.Add(modelBox);
        poolSection.Children.Add(Header("档位"));
        poolSection.Children.Add(tierBox);
        poolSection.Children.Add(poolDetail);
        poolSection.Children.Add(pathHint);

        comfySection.Children.Add(Header("哪一台 ComfyUI"));
        comfySection.Children.Add(comfySiteBox);
        comfySection.Children.Add(Header("家族（服务器上的文件夹）"));
        comfySection.Children.Add(folderBox);
        comfySection.Children.Add(Header("工作流"));
        comfySection.Children.Add(workflowBox);
        comfySection.Children.Add(recommendNote);
        comfySection.Children.Add(workflowDetail);

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(Header($"给「{nodeTitle}」选一个接口"));
        body.Children.Add(note);
        if (channels.Count > 1)
        {
            body.Children.Add(Header("从哪儿出"));
            body.Children.Add(channelBox);
        }
        body.Children.Add(poolSection);
        body.Children.Add(comfySection);

        var cancel = Secondary("取消");
        var confirm = Primary("用这个");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, confirm }
        };

        var dialog = DialogShell.Create("运行技能 · 选一个接口", Layout(body, Footer(buttons)), 660, 560);

        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) =>
        {
            var isComfy = channelBox.SelectedIndex == comfyChannelIndex && comfySites.Count > 0;
            if (isComfy)
            {
                var workflow = CurrentWorkflow();
                var siteIndex = comfySiteBox.SelectedIndex;
                if (workflow is null || siteIndex < 0 || siteIndex >= comfySites.Count)
                {
                    workflowDetail.Text = "还没选到具体的工作流：上面两级都要有选中项。";
                    return;
                }
                picked = ImageSourceChoice.OfWorkflow(new SiteWorkflowChoice(comfySites[siteIndex], workflow));
                dialog.Close();
                return;
            }

            var pool = CurrentPool(modelBox.SelectedIndex, tierBox.SelectedIndex);
            var index = siteBox.SelectedIndex;
            if (pool is null || index < 0 || index >= poolSites.Count)
            {
                poolDetail.Text = "还没选到具体的池子：上面三级都要有选中项。";
                return;
            }
            picked = ImageSourceChoice.OfPool(new SitePoolChoice(poolSites[index], pool));
            dialog.Close();
        };

        // 预选上一次用过的那个：对齐到同站点、同模型、同档位。
        // 找不到就停在默认（第一个）——预选是「省一次点击」，不是「保证一定选得上」。
        void ApplyPreset()
        {
            if (presetWorkflow is { } wanted && comfySites.Count > 0)
            {
                var siteIndex = comfySites.FindIndex(site =>
                    string.Equals(site.Id, wanted.Site.Id, StringComparison.OrdinalIgnoreCase));
                if (siteIndex >= 0)
                {
                    channelBox.SelectedIndex = comfyChannelIndex;
                    comfySiteBox.SelectedIndex = siteIndex;
                    var folder = wanted.Workflow.Folder.Length == 0 ? "（根目录）" : wanted.Workflow.Folder;
                    var match = folderBox.Items
                        .Select((item, index) => (Item: item as string ?? string.Empty, Index: index))
                        .FirstOrDefault(entry => entry.Item.StartsWith(folder + "（", StringComparison.Ordinal));
                    if (match.Item is not null) folderBox.SelectedIndex = match.Index;
                    var workflowIndex = workflows.FindIndex(workflow => workflow.Key == wanted.Workflow.Key);
                    if (workflowIndex >= 0) workflowBox.SelectedIndex = workflowIndex;
                    return;
                }
            }

            if (preset is null || poolSites.Count == 0) return;
            channelBox.SelectedIndex = ChannelPool;
            var poolSiteIndex = poolSites.FindIndex(site => string.Equals(site.Id, preset.Site.Id, StringComparison.OrdinalIgnoreCase));
            if (poolSiteIndex < 0) return;
            siteBox.SelectedIndex = poolSiteIndex;
            var groupIndex = models.FindIndex(group => group.Any(pool =>
                string.Equals(pool.Model, preset.Pool.Model, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pool.Tier, preset.Pool.Tier, StringComparison.OrdinalIgnoreCase)));
            if (groupIndex < 0) return;
            modelBox.SelectedIndex = groupIndex;
            var tierIndex = models[groupIndex].ToList().FindIndex(pool =>
                string.Equals(pool.Tier, preset.Pool.Tier, StringComparison.OrdinalIgnoreCase));
            if (tierIndex >= 0) tierBox.SelectedIndex = tierIndex;
        }

        channelBox.SelectedIndex = poolSites.Count > 0 ? ChannelPool : comfyChannelIndex;
        ApplyChannel();
        ApplyPreset();
        await dialog.ShowDialog(owner);
        return picked;
    }

    private static ComboBox Combo() => new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        FontSize = 12,
        Background = Brush("DfSurface2"),
        Foreground = Brush("DfInk")
    };
}
