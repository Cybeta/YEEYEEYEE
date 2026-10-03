using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YEEYEEYEE.Desktop;
using static YEEYEEYEE.Desktop.Avalonia.AgentDialogUi;

namespace YEEYEEYEE.Desktop.Avalonia;

/// <summary>
/// 「调用前问一句用哪个接口」的那个窗口。
///
/// 为什么要在调用前问：一个站点下面几十个池子（真实聚合站一家就有 60 多个生图池），
/// 它们**价格与能力都不一样**——同一句话用 1K 还是 4K、用哪个池，出的钱能差五六倍。
/// 让人在花这笔钱之前自己挑，比替他挑一个「默认的」要正当得多；挑完这一次也不会被记住，
/// 免得下次悄悄按上次那个贵的跑。
///
/// 三级选择（站点 → 模型 → 档位）而不是把 60 个池子平铺出来：平铺的列表里，
/// 同一个小区的池子除了「(池6)/(池1)」这一个后缀之外没有任何区别，翻起来只会让人放弃选择。
/// </summary>
internal static class SitePoolPicker
{
    public static async Task<SitePoolChoice?> ShowAsync(
        Window owner, IReadOnlyList<SiteProfile> sites, string nodeTitle, SitePoolChoice? preset = null)
    {
        var usable = sites.Where(site => site.UsablePools.Count > 0).ToList();
        if (usable.Count == 0)
        {
            await ConfirmAsync(owner, "还没有可用的池子",
                "当前没有登记任何站点，或者登记过的站点里一个可用池子都没有。\n"
                + "到「设置 → 生图与生视频」，用顶端的「智能导入」给一个接口说明网页，导入一次就有池子了。",
                "知道了");
            return null;
        }

        SitePoolChoice? picked = null;
        var siteBox = Combo();
        var modelBox = Combo();
        var tierBox = Combo();
        var detail = Note(string.Empty);
        detail.TextWrapping = TextWrapping.Wrap;

        // 两个下拉的候选按三层联动重建；重建期间置真，避免程序设值被当成用户选择。
        var loading = false;
        var models = new List<IGrouping<string, SitePool>>();

        void RefreshTiers()
        {
            loading = true;
            try
            {
                tierBox.Items.Clear();
                var index = modelBox.SelectedIndex;
                if (index < 0 || index >= models.Count)
                {
                    detail.Text = "先选一个模型。";
                    return;
                }
                foreach (var pool in models[index]) tierBox.Items.Add(pool.Tier.Length == 0 ? "（该模型没声明档位）" : pool.Tier);
                tierBox.SelectedIndex = 0;
                Describe();
            }
            finally { loading = false; }
        }

        void Describe()
        {
            var pool = CurrentPool(modelBox.SelectedIndex, tierBox.SelectedIndex);
            if (pool is null) { detail.Text = string.Empty; return; }
            var parts = new List<string> { pool.IsVideo ? "出视频" : "出图", pool.Describe() };
            detail.Text = string.Join(" · ", parts)
                + (pool.IsVideo ? "\n注意：这是出视频池子，出图选它没用；出视频请在分镜节点右键选「出这一镜的视频」。" : string.Empty);
        }

        SitePool? CurrentPool(int modelIndex, int tierIndex)
        {
            if (modelIndex < 0 || modelIndex >= models.Count) return null;
            var pools = models[modelIndex].ToList();
            return tierIndex >= 0 && tierIndex < pools.Count ? pools[tierIndex] : null;
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
                if (index < 0 || index >= usable.Count)
                {
                    detail.Text = string.Empty;
                    return;
                }
                // 模型层按「种类 + 模型名」聚合：同一个模型的不同档位是同一个模型的三个选项，不该平铺成三行。
                models = usable[index].UsablePools
                    .GroupBy(pool => $"{(pool.IsVideo ? "视频" : "图像")} · {pool.Model}")
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToList();
                foreach (var group in models) modelBox.Items.Add($"{group.Key}（{group.Count()} 档）");
                modelBox.SelectedIndex = models.Count > 0 ? 0 : -1;
            }
            finally { loading = false; }
            RefreshTiers();
        }

        siteBox.SelectionChanged += (_, _) => { if (!loading) RefreshModels(); };
        modelBox.SelectionChanged += (_, _) => { if (!loading) RefreshTiers(); };
        tierBox.SelectionChanged += (_, _) => { if (!loading) Describe(); };

        foreach (var site in usable)
            siteBox.Items.Add($"{site.Label}（生图 {site.ImagePools.Count} / 视频 {site.VideoPools.Count}）");
        siteBox.SelectedIndex = 0;

        var body = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        body.Children.Add(Header($"给「{nodeTitle}」选一个接口"));
        body.Children.Add(Note("站点下面每个池子的价格与能力都不一样：同一句话用 1K 还是 4K、用哪个池，出的钱能差好几倍。"
            + "这一次挑哪个就只用哪个，不会被记住成下次的默认。"));
        body.Children.Add(Header("站点"));
        body.Children.Add(siteBox);
        body.Children.Add(Header("模型"));
        body.Children.Add(modelBox);
        body.Children.Add(Header("档位"));
        body.Children.Add(tierBox);
        body.Children.Add(detail);
        var pathHint = Note(string.Empty);
        pathHint.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(pathHint);

        void ShowPaths()
        {
            var index = siteBox.SelectedIndex;
            if (index < 0 || index >= usable.Count) { pathHint.Text = string.Empty; return; }
            var site = usable[index];
            var paths = new List<string>();
            if (site.ImagePath.Length > 0) paths.Add($"文生图 {site.Method} {site.ImagePath}");
            if (site.ImageEditPath.Length > 0) paths.Add($"图生图 {site.Method} {site.ImageEditPath}");
            if (site.VideoPath.Length > 0) paths.Add($"出视频 {site.Method} {site.VideoPath}");
            pathHint.Text = $"接口：{site.BaseUrl}" + (paths.Count == 0 ? "（文档里没解析出接口路径）" : "｜" + string.Join("｜", paths));
        }
        siteBox.SelectionChanged += (_, _) => ShowPaths();

        var cancel = Secondary("取消");
        var confirm = Primary("用这个池子");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Children = { cancel, confirm }
        };

        var dialog = DialogShell.Create("运行技能 · 选一个接口", Layout(body, Footer(buttons)), 620, 470);

        cancel.Click += (_, _) => dialog.Close();
        confirm.Click += (_, _) =>
        {
            var pool = CurrentPool(modelBox.SelectedIndex, tierBox.SelectedIndex);
            var index = siteBox.SelectedIndex;
            if (pool is null || index < 0 || index >= usable.Count)
            {
                detail.Text = "还没选到具体的池子：上面三级都要有选中项。";
                return;
            }
            picked = new SitePoolChoice(usable[index], pool);
            dialog.Close();
        };

        // 预选上一次用过的那个：对齐到同站点、同模型、同档位。
        // 找不到就停在默认（第一个）——预选是「省一次点击」，不是「保证一定选得上」。
        void ApplyPreset()
        {
            if (preset is null) return;
            var siteIndex = usable.FindIndex(site => string.Equals(site.Id, preset.Site.Id, StringComparison.OrdinalIgnoreCase));
            if (siteIndex < 0) return;
            siteBox.SelectedIndex = siteIndex;
            var groupIndex = models.FindIndex(group => group.Any(pool =>
                string.Equals(pool.Model, preset.Pool.Model, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pool.Tier, preset.Pool.Tier, StringComparison.OrdinalIgnoreCase)));
            if (groupIndex < 0) return;
            modelBox.SelectedIndex = groupIndex;
            var tierIndex = models[groupIndex].ToList().FindIndex(pool =>
                string.Equals(pool.Tier, preset.Pool.Tier, StringComparison.OrdinalIgnoreCase));
            if (tierIndex >= 0) tierBox.SelectedIndex = tierIndex;
        }

        RefreshModels();
        ShowPaths();
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
