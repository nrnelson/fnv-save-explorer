namespace FnvSaveExplorer.Core;

/// <summary>A quest's Pip-Boy display state. <see cref="Active"/> = running with at least one displayed,
/// not-all-completed objective; <see cref="Completed"/> = greyed (the quest's done); <see cref="Failed"/> =
/// marked failed.</summary>
public enum PipboyQuestState { Active, Completed, Failed }

/// <summary>One objective shown under a quest in the Pip-Boy: its index, display text, whether it's ticked
/// (completed), and whether it's flagged optional (FNV bakes "(Optional)" into the objective text).</summary>
public sealed record PipboyObjective(int Index, string? Text, bool Completed)
{
    /// <summary>FNV marks optional objectives by prefixing the display text with "(Optional)" / "Optional:".</summary>
    public bool Optional => Text is { } t &&
        (t.TrimStart().StartsWith("(Optional)", StringComparison.OrdinalIgnoreCase) ||
         t.TrimStart().StartsWith("Optional:", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One quest as it would appear in the Pip-Boy Quests list: its FormID, name, state, and the
/// objectives currently displayed under it. <see cref="StartGameEnabled"/> records whether the quest's
/// running state was seeded from the masters Start-Game-Enabled flag vs reached only by script propagation.</summary>
public sealed record PipboyQuest(
    uint FormId, string? Name, PipboyQuestState State,
    IReadOnlyList<PipboyObjective> Objectives, bool StartGameEnabled);

/// <summary>
/// Computes the in-game Pip-Boy "Quests" list by modelling what the engine does at load: starting
/// Start-Game-Enabled quests and running each reached stage's result script (ROADMAP §6 #16). This is a
/// <b>Phase-A static interpreter</b> — it reads the masters' quest-script <i>source text</i> (<c>SCTX</c>,
/// parsed by <see cref="QuestScript"/>) rather than executing bytecode, and it is deliberately
/// <b>precision-first</b>: a quest is treated as running only when it is Start-Game-Enabled or is started by a
/// <b>non-conditional</b> <c>SetStage</c>/<c>StartQuest</c> from another running quest's reached stage. That
/// correctly <i>excludes</i> the background-initialized quests that pollute a raw save read (e.g. "Welcome to
/// the Big Empty" carries a displayed objective in the save yet isn't shown because nothing has started it).
/// <para><b>Honesty boundary / known gaps.</b> The accuracy ceiling is the <b>seed</b>: quests whose reached
/// stage lives only in an undecoded store (the packed formType-7 change form — e.g. "Ain't That a Kick") or in
/// a script chain with no save anchor (the Goodsprings main-quest hand-off VCG01→VCG02→VMQ01) are not advanced,
/// so they may be missed. Conditional (<c>if</c>-guarded) propagation is intentionally NOT followed (it would
/// over-fire). This computes a high-coverage approximation, validated against ground-truth saves, not a
/// guaranteed-perfect mirror.</para>
/// Read-only: it never mutates the save.
/// </summary>
public sealed class QuestPipboy
{
    /// <summary>The QSDT stage-flag bit that marks a stage as completing its quest (FO3/FNV "Complete Quest").
    /// Confirmed on the corpus: VCG01 stage 200 / VMQ01 stage 100 carry <c>0x01</c> alongside their
    /// <c>CompleteQuest</c>/<c>StopQuest</c> script calls (ROADMAP §6 #16).</summary>
    private const byte CompleteQuestStageFlag = 0x01;

    /// <summary>The computed Pip-Boy quests, ordered active-first then by name.</summary>
    public IReadOnlyList<PipboyQuest> Quests { get; }

    private QuestPipboy(IReadOnlyList<PipboyQuest> quests) => Quests = quests;

    /// <summary>Per-quest runtime state accumulated while interpreting reached-stage scripts.</summary>
    private sealed class QState
    {
        public required QuestDefinition Def;
        public bool Running;
        public bool Completed;
        public bool Failed;
        public readonly HashSet<int> Reached = [];
        public readonly Dictionary<int, bool> ObjDisplayed = [];
        public readonly Dictionary<int, bool> ObjCompleted = [];
    }

    /// <summary>Computes the Pip-Boy quest list from the save's masters-derived quest scripts. Returns an empty
    /// list when <paramref name="db"/> has no masters (quests can't be read without them).</summary>
    public static QuestPipboy Compute(FalloutSave save, PluginDatabase db)
    {
        // Index all quests by FormID and by editor id (script calls target quests by EDID).
        var states = new Dictionary<uint, QState>();
        var byEdid = new Dictionary<string, QState>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in db.Quests.Values)
        {
            var st = new QState { Def = def };
            states[def.FormId] = st;
            if (!string.IsNullOrEmpty(def.Edid))
                byEdid[def.Edid] = st;
        }

        var work = new Queue<(QState Quest, QuestStageDef Stage)>();
        QState? Resolve(string edid) => byEdid.GetValueOrDefault(edid);
        void Reach(QState target, int stageIndex)
        {
            target.Running = true;
            var sd = target.Def.Stages.FirstOrDefault(s => s.Index == stageIndex);
            if (sd is not null && target.Reached.Add(stageIndex))
                work.Enqueue((target, sd));
        }

        // ---- Seed: Start-Game-Enabled quests run their GameMode startup script at load. A quest advances itself
        // to its startup stage via a GameMode SetStage; that SetStage is typically if-guarded, so we fire it only
        // when its guards hold AT GAME-START DEFAULTS (ScriptCondition.TrueAtDefaults — every var/world-query = 0).
        // That distinguishes a genuine startup call (guard like `DoOnceMessage == 0` → true) from a world-gated
        // "catcher" (`GetReputationThreshold … >= 2` / `GetStage … >= 80` → false), which is what an in-game
        // trigger sets later and must not fire at load. ----
        foreach (var st in states.Values)
        {
            if (!st.Def.StartGameEnabled)
                continue;
            st.Running = true;
            var startup = ScriptStartup.Analyze(st.Def.GameModeScript, st.Def.LocalVars);
            foreach (var e in QuestScript.Parse(st.Def.GameModeScript))
            {
                if (!e.Guards.All(startup.GuardHolds) || Resolve(e.TargetQuestEdid) is not { } target)
                    continue;
                switch (e.Verb)
                {
                    case QuestScriptVerb.SetStage:
                        Reach(target, e.Arg1);
                        break;
                    case QuestScriptVerb.StartQuest:
                        target.Running = true;
                        break;
                }
            }
        }

        // ---- Save-anchored seed (ROADMAP §6 #16): a player-facing formType-7 quest whose change form matches the
        // VCG01 "completed" change-flag pattern has run its result scripts through its completing stage. This is
        // controlled-diff validated on VCG01 "Ain't That a Kick in the Head" (0x00104C1C): its record is 0x80000000
        // while the player is still in Doc Mitchell's house (stage < 200) and flips to 0xC0000000 — bit30 added on top
        // of bit31 — the moment the completing stage 200 fires on leaving the house, and it stays 0xC0000000 in the
        // ground-truth oracle (Save 57). We therefore require the EXACT validated pattern (bits 30+31 set, bit29
        // clear) rather than bit30 alone: the len-9/0x60000000 and len-42/0xE0000000 records are a DIFFERENT,
        // unvalidated objective-state family that also carries bit30 (e.g. "Ant Misbehavin'" VNellisInfestation
        // 0x001083DE is 0x60000000 throughout its life across the whole corpus), and firing "completed" on those would
        // be an unproven guess. We reach the quest's stages up to and including its completing (QSDT-complete) stage,
        // so its final objective state AND its cross-quest propagation are reconstructed — for VCG01 that is stage 200
        // -> StartQuest VMQ01 + SetStage VMQ01 10 (recovering "They Went That-a-Way") + StartQuest VCG04. ----
        const uint CompletedFlagMask = 0xE0000000, CompletedFlagPattern = 0xC0000000;
        foreach (var cf in save.EnumerateChangeForms())
        {
            if (cf.FormType != 0x07 || (cf.ChangeFlags & CompletedFlagMask) != CompletedFlagPattern)
                continue;
            if (!states.TryGetValue(cf.FormId, out var st) || !st.Def.IsPlayerFacing)
                continue;
            var completeStage = st.Def.Stages
                .Where(s => (s.Flags & CompleteQuestStageFlag) != 0)
                .Select(s => (int?)s.Index)
                .Max();
            if (completeStage is not { } cap)
                continue; // no completing stage to anchor against
            st.Running = true;
            foreach (var sd in st.Def.Stages.Where(s => s.Index <= cap))
                Reach(st, sd.Index);
        }

        // ---- Save-gated dialogue seed (ROADMAP §6 #16 Phase B): a dialogue INFO the player has SAID gets a change
        // form written for it in the save, so an INFO that (a) carries a quest-affecting result script and (b) is
        // present in the save is a trigger that ACTUALLY FIRED. Apply those effects to start/advance the freeform &
        // dialogue-started quests that no quest script ever references (e.g. "Back in the Saddle"). Gating on the
        // save's said-INFOs is what keeps this precise: a background-initialized quest whose starting dialogue was
        // never said (e.g. "Welcome to the Big Empty" before Old World Blues is entered) has no such change form, so
        // it is NOT added. Like the fixpoint, only non-conditional SetStage is followed (StartQuest always). ----
        if (db.DialogueInfoEffects.Count > 0)
        {
            var saidInfoPresent = new HashSet<uint>();
            foreach (var cf in save.EnumerateChangeForms())
                saidInfoPresent.Add(cf.FormId);
            foreach (var (infoFormId, effects) in db.DialogueInfoEffects)
            {
                if (!saidInfoPresent.Contains(infoFormId))
                    continue;
                foreach (var e in effects)
                {
                    if (Resolve(e.TargetQuestEdid) is not { } target)
                        continue;
                    if (e.Verb == QuestScriptVerb.StartQuest)
                        target.Running = true;
                    else if (e.Verb == QuestScriptVerb.SetStage && !e.Conditional)
                        Reach(target, e.Arg1);
                }
            }
        }

        // ---- Fixpoint: run reached-stage scripts; non-conditional SetStage/StartQuest expand the running set. ----
        while (work.Count > 0)
        {
            var (_, stage) = work.Dequeue();
            foreach (var e in QuestScript.Parse(stage.ScriptText))
            {
                if (Resolve(e.TargetQuestEdid) is not { } target)
                    continue;
                switch (e.Verb)
                {
                    case QuestScriptVerb.StartQuest:
                        target.Running = true;
                        break;
                    case QuestScriptVerb.SetStage when !e.Conditional:
                        Reach(target, e.Arg1);
                        break;
                    case QuestScriptVerb.StopQuest:
                        target.Running = false;
                        break;
                    case QuestScriptVerb.CompleteQuest:
                        target.Completed = true;
                        break;
                    case QuestScriptVerb.FailQuest:
                        target.Failed = true;
                        break;
                }
            }
        }

        // ---- Apply each quest's reached-stage objective effects in stage order to settle objective state. ----
        foreach (var st in states.Values)
        {
            if (st.Reached.Count == 0)
                continue;
            foreach (var stage in st.Def.Stages.Where(s => st.Reached.Contains(s.Index)).OrderBy(s => s.Index))
            {
                if ((stage.Flags & CompleteQuestStageFlag) != 0)
                    st.Completed = true;
                foreach (var e in QuestScript.Parse(stage.ScriptText))
                {
                    // Objective effects target this quest in practice; resolve by EDID and apply to whichever it names.
                    var target = Resolve(e.TargetQuestEdid) ?? st;
                    switch (e.Verb)
                    {
                        case QuestScriptVerb.SetObjectiveDisplayed:
                            target.ObjDisplayed[e.Arg1] = e.Arg2 != 0;
                            break;
                        case QuestScriptVerb.SetObjectiveCompleted:
                            target.ObjCompleted[e.Arg1] = e.Arg2 != 0;
                            break;
                        case QuestScriptVerb.CompleteAllObjectives:
                            foreach (var oi in target.ObjDisplayed.Where(o => o.Value).Select(o => o.Key).ToList())
                                target.ObjCompleted[oi] = true;
                            break;
                    }
                }
            }
        }

        // ---- Assemble: a player-facing quest shows when it's running/completed with a displayed objective. ----
        var quests = new List<PipboyQuest>();
        foreach (var st in states.Values)
        {
            if (!st.Def.IsPlayerFacing)
                continue;
            // A completed objective shows in the Pip-Boy even if the stage script only SetObjectiveCompleted it
            // without an explicit SetObjectiveDisplayed (e.g. VCG01 obj 30 "Use the Vit-o-matic Vigor Tester") — so
            // the displayed set is the union of displayed-and-completed objectives.
            var displayed = st.ObjDisplayed.Where(o => o.Value).Select(o => o.Key)
                .Concat(st.ObjCompleted.Where(o => o.Value).Select(o => o.Key))
                .ToHashSet();
            if (displayed.Count == 0 || !(st.Running || st.Completed || st.Failed))
                continue;

            var allShownCompleted = displayed.All(i => st.ObjCompleted.GetValueOrDefault(i));
            var state = st.Failed ? PipboyQuestState.Failed
                : st.Completed || allShownCompleted ? PipboyQuestState.Completed
                : PipboyQuestState.Active;

            // The Pip-Boy lists objectives most-recent first (descending index), and a quest greyed as Completed shows
            // ALL its objectives ticked — the engine completes them on quest completion even when the stage script
            // only displayed them (e.g. VCG01 obj 40/50 are SetObjectiveDisplayed but never SetObjectiveCompleted).
            var objectives = st.Def.Objectives
                .Where(o => displayed.Contains(o.Index))
                .OrderByDescending(o => o.Index)
                .Select(o => new PipboyObjective(
                    o.Index, o.Text, state == PipboyQuestState.Completed || st.ObjCompleted.GetValueOrDefault(o.Index)))
                .ToList();

            quests.Add(new PipboyQuest(st.Def.FormId, st.Def.Name, state, objectives, st.Def.StartGameEnabled));
        }

        quests = quests
            .OrderBy(q => q.State == PipboyQuestState.Completed) // active first, then completed/failed
            .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new QuestPipboy(quests);
    }
}
