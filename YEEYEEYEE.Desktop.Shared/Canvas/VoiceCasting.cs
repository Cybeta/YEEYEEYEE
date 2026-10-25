namespace YEEYEEYEE.Desktop;

/// <summary>一句台词：谁说、说什么。</summary>
public sealed record VoiceLine(string Speaker, string Text);

/// <summary>一位说话人在这一镜里落到哪个实体与哪份声音档案。</summary>
public sealed record VoiceCastEntry(string Speaker, WorkflowEntity Entity, WorkflowEntityVariant Variant, VoiceProfile? Voice)
{
    public string VoiceKey => Voice?.Key ?? string.Empty;
}

/// <summary>
/// 台词与声音档案的对照表。
///
/// 为什么台词先存在节点的 <see cref="WorkflowNode.Parameters"/> 里而不是新加一个字段：
/// 参数本来就是「这一镜的可变输入」，先放这里可以不动画布格式、不动两端投影；
/// 等台词编辑界面定稿再提升为正式字段，届时只需迁移一次。
/// 约定格式：每行一句，<c>角色：台词</c>；没有冒号的行按旁白处理。
/// </summary>
public static class VoiceCasting
{
    public const string DialogueParameter = "dialogue";
    /// <summary>不需要绑定角色、也不需要声音档案的说话人。</summary>
    public static readonly string[] Narrators = { "旁白", "字幕", "narrator", "字幕/旁白" };

    public static string? DialogueOf(WorkflowNode node) =>
        node.Parameters.TryGetValue(DialogueParameter, out var value) ? value : null;

    /// <summary>
    /// 解析台词。行格式：<c>CC：你每天这样，不累吗？</c>；允许全角/半角冒号，
    /// 允许说话人后带括号备注（<c>CC（内心）：…</c>），备注不参与角色匹配。
    /// </summary>
    public static IReadOnlyList<VoiceLine> Parse(string? script)
    {
        var lines = new List<VoiceLine>();
        if (string.IsNullOrWhiteSpace(script)) return lines;
        foreach (var raw in script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = line.IndexOf('：');
            if (split < 0) split = line.IndexOf(':');
            if (split <= 0)
            {
                lines.Add(new VoiceLine(string.Empty, line));
                continue;
            }
            var speaker = line[..split].Trim();
            var note = speaker.IndexOfAny(new[] { '（', '(' });
            if (note > 0) speaker = speaker[..note].Trim();
            var text = line[(split + 1)..].Trim();
            if (text.Length == 0) continue;
            lines.Add(new VoiceLine(speaker, text));
        }
        return lines;
    }

    public static bool IsNarrator(string speaker) =>
        Narrators.Contains(speaker.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>这一镜的台词落到哪些实体；匹配不到的角色不会出现在结果里（由 <see cref="Check"/> 报错）。</summary>
    public static IReadOnlyList<VoiceCastEntry> Cast(WorkflowCanvasState canvas, WorkflowNode node)
    {
        var cast = new List<VoiceCastEntry>();
        foreach (var speaker in Parse(DialogueOf(node)).Select(line => line.Speaker).Where(s => s.Length > 0 && !IsNarrator(s)).Distinct())
        {
            var content = canvas.ResolveReferences(node).FirstOrDefault(reference =>
                reference.Entity.Kind == EntityKind.Character && Matches(reference.Entity, speaker));
            if (content is null) continue;
            cast.Add(new VoiceCastEntry(speaker, content.Entity, content.Variant, content.Variant.Voice));
        }
        return cast;
    }

    /// <summary>
    /// 配音前的一致性校验：缺档案、档案不完整、跨镜换了音色，都写清楚是哪一镜哪个人。
    /// 没有台词的分镜返回空列表——静音镜头不该被声音档案挡住。
    /// </summary>
    public static IReadOnlyList<string> Check(WorkflowCanvasState canvas, WorkflowNode node)
    {
        var errors = new List<string>();
        var lines = Parse(DialogueOf(node));
        if (lines.Count == 0) return errors;
        foreach (var speaker in lines.Select(line => line.Speaker).Where(s => s.Length > 0 && !IsNarrator(s)).Distinct())
        {
            var content = canvas.ResolveReferences(node).FirstOrDefault(reference =>
                reference.Entity.Kind == EntityKind.Character && Matches(reference.Entity, speaker));
            if (content is null)
            {
                errors.Add($"台词里的「{speaker}」没有挂对应的角色引用，无法确定用谁的声音。");
                continue;
            }
            if (content.Variant.Voice is not { IsComplete: true } voice)
            {
                errors.Add($"「{speaker}」的声音档案不完整（需要 voiceId 与 model），请先补齐再配音。");
                continue;
            }
            foreach (var other in canvas.Nodes.Where(item => item.Id != node.Id && item.Category == NodeCategory.Storyboard))
            {
                var peer = Cast(canvas, other).FirstOrDefault(entry => Matches(entry.Entity, speaker));
                if (peer?.Voice is not { IsComplete: true } peerVoice) continue;
                if (string.Equals(peerVoice.Key, voice.Key, StringComparison.Ordinal)) continue;
                errors.Add($"「{speaker}」在「{other.Title}」与「{node.Title}」使用了不同的音色"
                    + $"（{peerVoice.Describe()} / {voice.Describe()}），跨镜一致性不成立；请统一声音档案或显式升级版本。");
            }
        }
        return errors;
    }

    /// <summary>角色名或别名命中即可，忽略大小写与空格。</summary>
    public static bool Matches(WorkflowEntity entity, string speaker)
    {
        var key = speaker.Trim();
        if (key.Length == 0) return false;
        if (string.Equals(entity.Name.Trim(), key, StringComparison.OrdinalIgnoreCase)) return true;
        return entity.Aliases.Split(new[] { ',', '，', '/', '、', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(alias => string.Equals(alias.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }
}
