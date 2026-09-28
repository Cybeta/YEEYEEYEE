namespace DreamForge.Core;

public static class ReferenceGraph
{
    public const int MaxDepth = 32;

    public static SkillLocation Judge(Skill root, IReadOnlyDictionary<Guid, Channel> channels, IReadOnlyDictionary<Guid, Tool> tools, IReadOnlyDictionary<Guid, Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(root);
        return VisitSkill(root, channels, tools, skills, new HashSet<Guid>(), 0);
    }

    private static SkillLocation VisitSkill(Skill skill, IReadOnlyDictionary<Guid, Channel> channels, IReadOnlyDictionary<Guid, Tool> tools, IReadOnlyDictionary<Guid, Skill> skills, HashSet<Guid> path, int depth)
    {
        if (depth > MaxDepth) throw new SkillReferenceException(skill.Id, "REFERENCE_RECURSION_LIMIT", $"引用嵌套超过 {MaxDepth} 层");
        if (!path.Add(skill.Id)) throw new SkillReferenceException(skill.Id, "REFERENCE_CYCLE", "检测到 Skill 循环引用");
        var locations = skill.References.Select(reference => VisitReference(reference, channels, tools, skills, path, depth)).ToArray();
        path.Remove(skill.Id);
        return Combine(locations);
    }

    private static SkillLocation VisitReference(TypedReference reference, IReadOnlyDictionary<Guid, Channel> channels, IReadOnlyDictionary<Guid, Tool> tools, IReadOnlyDictionary<Guid, Skill> skills, HashSet<Guid> path, int depth)
    {
        if (reference.TargetId == Guid.Empty) throw new SkillReferenceException(reference.TargetId, "REFERENCE_UNRESOLVED", "引用目标为空");
        return reference.Kind switch
        {
            ReferenceKind.Channel when channels.TryGetValue(reference.TargetId, out var channel) => channel.Location,
            ReferenceKind.Tool when tools.TryGetValue(reference.TargetId, out var tool) => tool.LocalDependencies.Count > 0 ? SkillLocation.Local : SkillLocation.Cloud,
            ReferenceKind.Skill when skills.TryGetValue(reference.TargetId, out var skill) => VisitSkill(skill, channels, tools, skills, path, depth + 1),
            _ => throw new SkillReferenceException(reference.TargetId, "REFERENCE_UNRESOLVED", "未知、缺失或无权访问的引用")
        };
    }

    private static SkillLocation Combine(IEnumerable<SkillLocation> locations)
    {
        var values = locations.ToArray();
        var local = values.Any(x => x is SkillLocation.Local or SkillLocation.Hybrid);
        var cloud = values.Any(x => x is SkillLocation.Cloud or SkillLocation.Hybrid);
        return local && cloud ? SkillLocation.Hybrid : local ? SkillLocation.Local : SkillLocation.Cloud;
    }
}
