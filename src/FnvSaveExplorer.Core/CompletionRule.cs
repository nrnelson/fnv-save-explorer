namespace FnvSaveExplorer.Core;

/// <summary>The kind of runtime trigger whose firing completes a quest (ROADMAP §6 #16 SYNTHESIS). Each kind
/// leaves a distinct, persisted save trace that <see cref="SaveSignalEvaluator"/> reads — so a quest's completion
/// is recovered by checking the trigger's trace, with no script execution.</summary>
public enum CompletionTrigger
{
    /// <summary>An actor <c>OnDeath</c>/<c>OnHit</c> script completes the quest with a single kill (e.g. "I Fought
    /// the Law"). Trace: the actor is in the save's death registry / a created-reference corpse.</summary>
    Kill,
    /// <summary>The quest's own GameMode/stage script completes it inside a <c>counter &gt;= N</c> guard, and the
    /// counter is bumped by external (kill/event) scripts (e.g. Ghost Town Gunfight, <c>nGangerDeathCount &gt;= 6</c>).
    /// Trace: count how many counter-incrementing actors are dead.</summary>
    Counter,
    /// <summary>An <c>OnActivate</c> script completes the quest and <c>Enable</c>s specific references (e.g. That
    /// Lucky Old Sun powers on the HELIOS One FX). Trace: one of those refs is enabled in the save.</summary>
    Activate,
    /// <summary>A dialogue <c>INFO</c>'s <b>conditional</b> <c>SetStage</c> targets a completing (QSDT-0x01) stage
    /// (e.g. Ring-a-Ding-Ding! — "tell Mr. House the outcome"). Trace: the INFO is present as a change form (it was
    /// said), so the line — conditional only on the player's path — fired.</summary>
    DialogueSetStage,
    /// <summary>A dialogue <c>INFO</c> carries a quest-state <c>CTDA</c> condition that proves the quest reached a
    /// completing stage (the said-INFO's precondition held). Trace: the INFO is present as a change form.</summary>
    CtdaProof,
}

/// <summary>One harvested rule that completes a quest when its <see cref="Trigger"/> has fired in the save
/// (ROADMAP §6 #16, Stage A). This is the unified, data-driven replacement for the family of bespoke completion
/// passes that <see cref="QuestPipboy"/> grew during the phase: each rule names a <see cref="TargetQuestFormId"/>
/// (save-space) and the binding its <see cref="Trigger"/> needs, and <see cref="SaveSignalEvaluator"/> decides
/// whether the trace is present. Only the binding field(s) relevant to <see cref="Trigger"/> are populated; the
/// rest are empty. <para>Guards are <b>not</b> a field yet: today every guard is baked into the trigger semantics
/// (the counter threshold, single-kill = not-counter-gated, "the line was said ⇒ the player is on its path"). The
/// general guard evaluator is Stage B.</para></summary>
public sealed record CompletionRule(
    uint TargetQuestFormId,
    CompletionTrigger Trigger,
    IReadOnlyList<uint> Scripts,            // Kill: actor OnDeath/completion scripts
    IReadOnlyList<uint> IncrementScripts,   // Counter: scripts that bump the counter
    int Threshold,                          // Counter: kills needed
    IReadOnlySet<uint> EnableRefs,          // Activate: refs the completion enables
    uint InfoFormId)                        // DialogueSetStage / CtdaProof: the said-INFO
{
    private static readonly IReadOnlyList<uint> NoScripts = [];
    private static readonly IReadOnlySet<uint> NoRefs = new HashSet<uint>();

    /// <summary>The QSDT stage-flag bit marking a stage as completing its quest (FO3/FNV "Complete Quest"); matches
    /// <c>QuestPipboy</c>/<c>CounterGatedQuests</c>.</summary>
    private const byte CompleteQuestStageFlag = 0x01;

    private static CompletionRule Kill(uint quest, IReadOnlyList<uint> scripts) =>
        new(quest, CompletionTrigger.Kill, scripts, NoScripts, 0, NoRefs, 0);
    private static CompletionRule Counter(uint quest, IReadOnlyList<uint> incrementScripts, int threshold) =>
        new(quest, CompletionTrigger.Counter, NoScripts, incrementScripts, threshold, NoRefs, 0);
    private static CompletionRule Activate(uint quest, IReadOnlySet<uint> refs) =>
        new(quest, CompletionTrigger.Activate, NoScripts, NoScripts, 0, refs, 0);
    private static CompletionRule Dialogue(uint quest, uint infoFormId) =>
        new(quest, CompletionTrigger.DialogueSetStage, NoScripts, NoScripts, 0, NoRefs, infoFormId);
    private static CompletionRule Ctda(uint quest, uint infoFormId) =>
        new(quest, CompletionTrigger.CtdaProof, NoScripts, NoScripts, 0, NoRefs, infoFormId);

    /// <summary>Harvests one flat completion-rule catalog from the maps the <paramref name="db"/> already exposes —
    /// the same sources the bespoke passes scraped independently (<see cref="PluginDatabase.ExternalCompletions"/>,
    /// <see cref="PluginDatabase.CounterGates"/>, <see cref="PluginDatabase.CompletionEnableRefs"/>,
    /// <see cref="PluginDatabase.DialogueInfoEffects"/>, <see cref="PluginDatabase.DialogueInfoConditions"/>). No
    /// new analysis: this only reorganises what's already derived. Read-only.</summary>
    public static IReadOnlyList<CompletionRule> Harvest(PluginDatabase db)
    {
        var rules = new List<CompletionRule>();

        // Dialogue effects target their quest by editor id, so index the quests by EDID for those.
        var byEdid = new Dictionary<string, QuestDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var q in db.Quests.Values)
            if (!string.IsNullOrEmpty(q.Edid))
                byEdid[q.Edid!] = q;

        // Kill: single-kill external completions (one OnDeath completes the quest), excluding counter-gated ones.
        foreach (var c in db.ExternalCompletions)
            if (c is { ViaKill: true, ViaCounter: false })
                rules.Add(Kill(c.QuestFormId, c.Scripts));

        // Counter: bound, externally-incremented counter gates (count-N-kills, the Ghost Town Gunfight shape).
        foreach (var g in db.CounterGates)
            if (g is { Bound: true, ExternallyIncremented: true })
                rules.Add(Counter(g.QuestFormId, g.IncrementScripts, g.Threshold));

        // Activate: a quest whose completion enables specific world refs.
        foreach (var (questFormId, refs) in db.CompletionEnableRefs)
            rules.Add(Activate(questFormId, refs));

        // DialogueSetStage: a said-INFO whose CONDITIONAL SetStage targets a completing (QSDT-0x01) stage.
        foreach (var (infoFormId, effects) in db.DialogueInfoEffects)
            foreach (var e in effects)
                if (e is { Verb: QuestScriptVerb.SetStage, Conditional: true }
                    && byEdid.TryGetValue(e.TargetQuestEdid, out var def)
                    && def.Stages.Any(s => s.Index == e.Arg1 && (s.Flags & CompleteQuestStageFlag) != 0))
                    rules.Add(Dialogue(def.FormId, infoFormId));

        // CtdaProof: a said-INFO carrying a quest-state condition that proves its quest reached a completing stage.
        foreach (var (infoFormId, conditions) in db.DialogueInfoConditions)
            foreach (var c in conditions)
                if (db.Quests.TryGetValue(c.QuestFormId, out var def) && ImpliesCompleted(c, def))
                    rules.Add(Ctda(c.QuestFormId, infoFormId));

        return rules;
    }

    /// <summary>True when a said-INFO's quest-state <see cref="InfoCondition"/> proves its target quest reached a
    /// completing (QSDT-0x01) stage — so the quest is completed. Uses the quest's <i>minimum</i> completing stage,
    /// since reaching any complete-flagged stage finishes the quest. (Moved verbatim from <c>QuestPipboy</c>.)</summary>
    private static bool ImpliesCompleted(InfoCondition c, QuestDefinition def)
    {
        var minComplete = def.Stages.Where(s => (s.Flags & CompleteQuestStageFlag) != 0).Select(s => (int?)s.Index).Min();
        return c.Function switch
        {
            QuestFunction.GetQuestCompleted => c.Op is 0 or 3 && c.CompareValue >= 1,                          // == / >= 1
            QuestFunction.GetStage when minComplete is { } cs => c.Op is 0 or 2 or 3 && c.CompareValue >= cs,  // == / > / >= a completing stage
            QuestFunction.GetStageDone when minComplete is { } cs => c.Param2 >= cs && c.Op is 0 or 3 && c.CompareValue >= 1,
            _ => false,
        };
    }
}
