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

        // ---- Seed: Start-Game-Enabled quests are running from game load at their lowest defined stage. ----
        var work = new Queue<(QState Quest, QuestStageDef Stage)>();
        foreach (var st in states.Values)
        {
            if (!st.Def.StartGameEnabled || st.Def.Stages.Count == 0)
                continue;
            st.Running = true;
            var lowest = st.Def.Stages.MinBy(s => s.Index)!;
            if (st.Reached.Add(lowest.Index))
                work.Enqueue((st, lowest));
        }

        // ---- Fixpoint: run reached-stage scripts; non-conditional SetStage/StartQuest expand the running set. ----
        QState? Resolve(string edid) => byEdid.GetValueOrDefault(edid);
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
                        target.Running = true;
                        var sd = target.Def.Stages.FirstOrDefault(s => s.Index == e.Arg1);
                        if (sd is not null && target.Reached.Add(e.Arg1))
                            work.Enqueue((target, sd));
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
            var displayed = st.ObjDisplayed.Where(o => o.Value).Select(o => o.Key).ToHashSet();
            if (displayed.Count == 0 || !(st.Running || st.Completed || st.Failed))
                continue;

            var objectives = st.Def.Objectives
                .Where(o => displayed.Contains(o.Index))
                .OrderBy(o => o.Index)
                .Select(o => new PipboyObjective(o.Index, o.Text, st.ObjCompleted.GetValueOrDefault(o.Index)))
                .ToList();

            var allDone = objectives.Count > 0 && objectives.All(o => o.Completed);
            var state = st.Failed ? PipboyQuestState.Failed
                : st.Completed || allDone ? PipboyQuestState.Completed
                : PipboyQuestState.Active;

            quests.Add(new PipboyQuest(st.Def.FormId, st.Def.Name, state, objectives, st.Def.StartGameEnabled));
        }

        quests = quests
            .OrderBy(q => q.State == PipboyQuestState.Completed) // active first, then completed/failed
            .ThenBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new QuestPipboy(quests);
    }
}
