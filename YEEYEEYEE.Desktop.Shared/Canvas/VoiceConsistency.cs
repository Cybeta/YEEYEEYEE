namespace YEEYEEYEE.Desktop;

/// <summary>这一镜的声音是谁产的：我们自己的配音技能，还是视频模型自带的音轨。</summary>
public enum VoiceAudioSource { None = 0, Tts = 1, Model = 2 }

/// <summary>
/// 一条声音的**可核查证据**。模型自带的音频没有 seed/音色参数可锁，
/// 所以必须把「转写对不对」「声纹像不像」变成可存档的数字，成片前拿它做闸门。
/// </summary>
public sealed record VoiceTakeEvidence(
    string Speaker,
    VoiceAudioSource Source,
    string VoiceKey,
    bool TranscriptMatches,
    double SpeakerSimilarity,
    string Note = "")
{
    public static VoiceTakeEvidence Trusted(string speaker, string voiceKey) =>
        new(speaker, VoiceAudioSource.Tts, voiceKey, true, 1, "本项目配音技能生成");
}

/// <summary>
/// 跨镜声音一致性的判定规则。**只有这一份**：配音技能、成片闸门、界面提示都调它，
/// 否则「哪里算一致」会随入口漂移，而声音漂移恰恰是最难被肉眼发现的问题。
/// </summary>
public static class VoiceConsistency
{
    /// <summary>同一角色跨镜的声纹相似度下限。低于它就按「换了个人」处理。</summary>
    public const double MinimumSimilarity = 0.75;

    /// <summary>
    /// 一组镜头的证据汇总。返回的是**阻断原因**，空列表表示可以进成片。
    /// 规则：
    /// 1. 有台词却没证据 → 阻断（模型音频必须检测过）。
    /// 2. 转写对不上 → 阻断（台词都不是剧本里那句）。
    /// 3. 相似度低于下限 → 阻断。
    /// 4. 同一角色跨镜的 VoiceKey 不一致 → 阻断（换了音色或换了模型）。
    /// </summary>
    public static IReadOnlyList<string> Check(IEnumerable<(string Shot, VoiceTakeEvidence Evidence)> takes)
    {
        var errors = new List<string>();
        var bySpeaker = new Dictionary<string, (string Shot, VoiceTakeEvidence Evidence)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (shot, evidence) in takes)
        {
            if (!evidence.TranscriptMatches)
                errors.Add($"「{shot}」的「{evidence.Speaker}」台词转写与剧本不一致，音频不能进成片。");
            if (evidence.SpeakerSimilarity + 0.0001 < MinimumSimilarity)
                errors.Add($"「{shot}」的「{evidence.Speaker}」声纹相似度 {evidence.SpeakerSimilarity:0.00} 低于下限 {MinimumSimilarity:0.00}，判定不是同一个声音。");
            if (string.IsNullOrWhiteSpace(evidence.VoiceKey))
                errors.Add($"「{shot}」的「{evidence.Speaker}」没有记录音色标识，无法证明跨镜一致。");
            if (bySpeaker.TryGetValue(evidence.Speaker, out var previous) &&
                !string.Equals(previous.Evidence.VoiceKey, evidence.VoiceKey, StringComparison.Ordinal))
                errors.Add($"「{evidence.Speaker}」在「{previous.Shot}」与「{shot}」音色不一致"
                    + $"（{previous.Evidence.VoiceKey} / {evidence.VoiceKey}），不能直接拼接。");
            else if (!bySpeaker.ContainsKey(evidence.Speaker)) bySpeaker[evidence.Speaker] = (shot, evidence);
        }
        return errors;
    }

    /// <summary>某镜有台词、却没有任何说话人的证据：直接列出缺谁。</summary>
    public static IReadOnlyList<string> Missing(string shot, IEnumerable<string> speakers) =>
        speakers.Select(speaker => $"「{shot}」的「{speaker}」还没有配音或音频检测记录，不能进成片。").ToList();
}
