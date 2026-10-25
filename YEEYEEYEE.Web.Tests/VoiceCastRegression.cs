using YEEYEEYEE.Desktop;

internal static class VoiceCastRegression
{
    public static void Run()
    {
        static void Assert(bool ok, string label) { if (!ok) throw new Exception("Voice cast: " + label); }
        var canvas = new WorkflowCanvasState();
        var cc = new WorkflowEntity { Kind = EntityKind.Character, Name = "CC", Aliases = "Claude" };
        var ccVariant = cc.CreateVariant("默认");
        ccVariant.Voice = new VoiceProfile { Provider = "anyaiapi", VoiceId = "cc-warm", Model = "tts-1", Speed = 1 };
        var dp = new WorkflowEntity { Kind = EntityKind.Character, Name = "DP", Aliases = "DeepSeek" };
        var dpVariant = dp.CreateVariant("默认");
        dpVariant.Voice = new VoiceProfile { Provider = "anyaiapi", VoiceId = "dp-bright", Model = "tts-1", Speed = 1 };
        var silent = new WorkflowEntity { Kind = EntityKind.Character, Name = "路人" };
        var silentVariant = silent.CreateVariant("默认");
        canvas.Entities.AddRange(new[] { cc, dp, silent });

        WorkflowNode Shot(string title, string dialogue, params (WorkflowEntity Entity, WorkflowEntityVariant Variant)[] cast)
        {
            var node = new WorkflowNode { Title = title, Category = NodeCategory.Storyboard };
            foreach (var (entity, variant) in cast)
                node.References.Add(new NodeReference { EntityId = entity.Id, VariantId = variant.Id });
            node.Parameters[VoiceCasting.DialogueParameter] = dialogue;
            canvas.Nodes.Add(node);
            return node;
        }

        var s01 = Shot("S01", "CC（内心）：先让这里像样一点。", (cc, ccVariant));
        var s08 = Shot("S08", "CC：你每天这样，不累吗？\nDP：累啊。", (cc, ccVariant), (dp, dpVariant));
        Assert(VoiceCasting.Check(canvas, s01).Count == 0, "complete profile with matching reference passes");
        Assert(VoiceCasting.Check(canvas, s08).Count == 0, "two speakers with complete profiles pass");

        var parsed = VoiceCasting.Parse("CC（内心）：……好像也没那么难。\n旁白：先爱自己。\n随便一句");
        Assert(parsed.Count == 3, "three lines parsed");
        Assert(parsed[0].Speaker == "CC" && parsed[0].Text.StartsWith("……"), "parenthetical speaker note is stripped");
        Assert(VoiceCasting.IsNarrator(parsed[1].Speaker), "narrator is recognised");
        Assert(parsed[2].Speaker.Length == 0, "line without colon is narration");

        var unbound = Shot("S09", "CC：我还在。", Array.Empty<(WorkflowEntity, WorkflowEntityVariant)>());
        Assert(VoiceCasting.Check(canvas, unbound).Any(error => error.Contains("没有挂对应的角色引用")), "speaker without reference is rejected");

        var mute = Shot("S10", "路人：借过一下。", (silent, silentVariant));
        Assert(VoiceCasting.Check(canvas, mute).Any(error => error.Contains("声音档案不完整")), "missing voice profile is rejected");

        var ccCold = cc.CreateVariant("冷声");
        ccCold.Voice = new VoiceProfile { Provider = "anyaiapi", VoiceId = "cc-cold", Model = "tts-1", Speed = 1 };

        var drifting = Shot("S11", "CC：嗯。", (cc, ccCold));
        Assert(VoiceCasting.Check(canvas, drifting).Any(error => error.Contains("不同的音色")), "cross-shot drift is rejected");
        drifting.References.Clear();
        drifting.References.Add(new NodeReference { EntityId = cc.Id, VariantId = ccVariant.Id });
        Assert(VoiceCasting.Check(canvas, drifting).Count == 0, "restored profile passes again");

        var silentShot = Shot("S12", string.Empty, (cc, ccVariant));
        Assert(VoiceCasting.Check(canvas, silentShot).Count == 0, "shot without dialogue is not blocked");

        var trusted = VoiceTakeEvidence.Trusted("CC", "anyaiapi|tts-1|cc-warm|1");
        Assert(VoiceConsistency.Check(new[] { ("S01", trusted), ("S08", trusted) }).Count == 0, "same voice key passes");
        Assert(VoiceConsistency.Check(new[] { ("S01", trusted with { TranscriptMatches = false }) })
            .Any(error => error.Contains("转写")), "transcript mismatch is rejected");
        Assert(VoiceConsistency.Check(new[] { ("S01", trusted with { SpeakerSimilarity = 0.5 }) })
            .Any(error => error.Contains("声纹")), "low speaker similarity is rejected");
        Assert(VoiceConsistency.Check(new[] { ("S01", trusted), ("S08", trusted with { VoiceKey = "anyaiapi|tts-1|cc-cold|1" }) })
            .Any(error => error.Contains("音色不一致")), "model voice drift is rejected");
        Assert(VoiceConsistency.Missing("S09", new[] { "CC" }).Count == 1, "missing evidence is reported");

        Console.WriteLine("Voice cast passed: parsing, missing reference, missing profile, cross-shot drift, silent shots and audio evidence rules.");
    }
}
