namespace FnvSaveExplorer.Core;

/// <summary>Whether a quest objective's target reference is enabled in the save (ROADMAP §6 #10). An
/// objective is "active" when its target marker(s) are enabled. <see cref="Unknown"/> when the save records
/// no enable-state for the ref (no change form, or one that didn't touch the form-flags) — the engine then
/// uses the base record's default, which this tool doesn't read from the masters, so we don't guess.</summary>
public enum EnableState { Unknown, Enabled, Disabled }

/// <summary>The player's progress on a quest. <see cref="Active"/> = tracked with no terminal stage reached;
/// <see cref="Completed"/> = a done stage flagged "complete quest"; <see cref="Unknown"/> when the save holds
/// no decodable stage/objective state. (Quest <i>failure</i> isn't a distinct decoded signal yet, so it is
/// not reported as a separate state — ROADMAP §6 #10.)</summary>
public enum QuestState { Unknown, Active, Completed }

/// <summary>One target reference of an objective and its decoded enable-state in the save.</summary>
public sealed record QuestTarget(uint FormId, EnableState State);

/// <summary>One objective of a quest: its index and display text (from the masters), its target refs with
/// their save-side enable-state, and whether it is currently active (any target enabled), inactive (targets
/// known but none enabled), or unknown (<c>null</c> — no enable-state recorded).</summary>
public sealed record QuestObjective(int Index, string? Text, bool? Active, IReadOnlyList<QuestTarget> Targets);

/// <summary>One stage of a quest as recorded in the save: its index, whether it is done, the completion
/// game-time (when done), and — joined from the masters definition — the stage's raw <c>QSDT</c> flag byte
/// and Pip-Boy log text.</summary>
public sealed record QuestStageEntry(int Index, bool Done, uint? CompletionTime, byte DefinitionFlags, string? LogText);

/// <summary>A quest the save is tracking, with its decoded stages and objectives.</summary>
public sealed record Quest(
    uint FormId,
    string? Name,
    QuestState State,
    uint ChangeFlags,
    IReadOnlyList<QuestStageEntry> Stages,
    IReadOnlyList<QuestObjective> Objectives);

/// <summary>
/// Reads the player's quest log from a save (ROADMAP §6 #10) by combining two decoded storage mechanisms:
/// <list type="bullet">
/// <item><b>Stage lists</b> — a quest change form (classified by resolving its refID → FormID → masters
/// record type <c>QUST</c>) carrying the <c>CHANGE_QUEST_STAGES</c> flag stores its progress as a
/// <c>0x7C</c>-delimited stage list (ROADMAP §6 #10). Each completed entry is
/// <c>[stageNum][done][04][stageIdx][done2][u32 game-time]</c>; an incomplete entry drops the trailing
/// time and has <c>done=0</c>.</item>
/// <item><b>Objective enable-state</b> — for freeform quests, objective progress lives in the enable-state of
/// the objective's target placed references (the <c>QSTA</c> targets in the masters QUST record). A target
/// whose change form clears the <i>Initially Disabled</i> base-record flag (<c>0x800</c>) is enabled, i.e.
/// its objective is active (ROADMAP §6 #10).</item>
/// </list>
/// This is a read-only reconstruction; it never mutates the save. It requires a <see cref="PluginDatabase"/>
/// (built from the game masters) to classify quest change forms and to supply objective/stage definitions.
/// </summary>
public sealed class QuestLog
{
    /// <summary>The base-record flag marking a placed reference as <i>Initially Disabled</i>. A target ref
    /// whose change form clears this bit has been enabled — its objective is active (ROADMAP §6 #10).</summary>
    private const uint InitiallyDisabledFormFlag = 0x00000800;

    /// <summary>The <c>QSDT</c> stage-flag bit that marks a stage as completing the quest (FO3/FNV GECK
    /// "Complete Quest"). Used only to classify a quest as <see cref="QuestState.Completed"/>; left as a named
    /// constant so the semantics can be re-pinned if a controlled diff disagrees (ROADMAP §6 #10).</summary>
    private const byte CompleteQuestStageFlag = 0x02;

    /// <summary>The quests the save is tracking, in change-form order.</summary>
    public IReadOnlyList<Quest> Quests { get; }

    private QuestLog(IReadOnlyList<Quest> quests) => Quests = quests;

    /// <summary>Reads the quest log. Returns an empty log when <paramref name="db"/> has no masters (quest
    /// change forms can't be classified without them).</summary>
    public static QuestLog Read(FalloutSave save, PluginDatabase db)
    {
        // Index change forms by FormID once, for objective target-ref lookups.
        var byFormId = new Dictionary<uint, FalloutSave.ChangeFormHeader>();
        foreach (var cf in save.EnumerateChangeForms())
            byFormId[cf.FormId] = cf;

        var quests = new List<Quest>();
        foreach (var cf in save.EnumerateChangeForms())
        {
            if (db.RecordType(cf.FormId) != "QUST")
                continue;

            var def = db.Quest(cf.FormId);
            var stages = ReadStageList(save, cf, def);
            var objectives = ReadObjectives(save, byFormId, def);

            // A quest log entry needs something to show. Many QUST change forms are pure dialogue/timer/script
            // quests with neither decoded stages nor any player-facing objective — skip those as noise.
            if (stages.Count == 0 && objectives.Count == 0)
                continue;

            var state = DeriveState(stages);
            quests.Add(new Quest(cf.FormId, db.Resolve(cf.FormId), state, cf.ChangeFlags, stages, objectives));
        }

        return new QuestLog(quests);
    }

    /// <summary>Parses the <c>0x7C</c>-delimited stage list from a quest change form carrying the
    /// <c>CHANGE_QUEST_STAGES</c> flag, joining each entry to its masters stage definition (flags + log text).
    /// Returns an empty list when the flag is absent or no entry is recognised.</summary>
    private static IReadOnlyList<QuestStageEntry> ReadStageList(
        FalloutSave save, FalloutSave.ChangeFormHeader cf, QuestDefinition? def)
    {
        if ((cf.ChangeFlags & ReferenceChangeForm.ChangeQuestStages) == 0)
            return [];

        var data = save.ReadAt(cf.DataOffset, cf.DataLength);
        var fields = ReferenceChangeForm.Tokenize(data, cf.DataOffset);

        var stages = new List<QuestStageEntry>();
        var seen = new HashSet<int>();
        // An entry is the 5-field signature [stageNum][done 0/1][0x04][stageIdx][done2 0/1], optionally
        // followed by a 4-byte game-time when done. Scan for it; the leading script/header fields don't match.
        for (var i = 0; i + 4 < fields.Count; i++)
        {
            if (fields[i].Length != 1) continue;
            var done = fields[i + 1];
            var marker = fields[i + 2];
            var done2 = fields[i + 4];
            if (done.Length != 1 || done.Bytes[0] > 1) continue;
            if (marker.Length != 1 || marker.Bytes[0] != 0x04) continue;
            if (fields[i + 3].Length != 1) continue;
            if (done2.Length != 1 || done2.Bytes[0] > 1) continue;

            var stageNum = fields[i].Bytes[0];
            if (!seen.Add(stageNum)) continue; // a quest can re-log the same stage; keep the first

            var isDone = done.Bytes[0] == 1;
            uint? time = null;
            if (isDone && i + 5 < fields.Count && fields[i + 5].AsUInt32 is { } t)
                time = t;

            var stageDef = def?.Stages.FirstOrDefault(s => s.Index == stageNum);
            stages.Add(new QuestStageEntry(stageNum, isDone, time, stageDef?.Flags ?? 0, stageDef?.LogText));
        }
        return stages;
    }

    /// <summary>Resolves each objective's target refs to their save-side enable-state (ROADMAP §6 #10).
    /// Returns an empty list when the quest has no masters definition.</summary>
    private static IReadOnlyList<QuestObjective> ReadObjectives(
        FalloutSave save, Dictionary<uint, FalloutSave.ChangeFormHeader> byFormId, QuestDefinition? def)
    {
        if (def is null || def.Objectives.Count == 0)
            return [];

        var objectives = new List<QuestObjective>(def.Objectives.Count);
        foreach (var o in def.Objectives)
        {
            var targets = new List<QuestTarget>(o.TargetFormIds.Count);
            foreach (var formId in o.TargetFormIds)
                targets.Add(new QuestTarget(formId, TargetState(save, byFormId, formId)));

            bool? active = targets.Any(t => t.State == EnableState.Enabled) ? true
                : targets.Any(t => t.State == EnableState.Disabled) ? false
                : null; // all unknown
            objectives.Add(new QuestObjective(o.Index, o.Text, active, targets));
        }
        return objectives;
    }

    /// <summary>The enable-state of a placed reference: <see cref="EnableState.Enabled"/> /
    /// <see cref="EnableState.Disabled"/> read from its change form's recorded base-record flags (the
    /// <c>0x800</c> Initially-Disabled bit), or <see cref="EnableState.Unknown"/> when no such change form
    /// recorded the flags.</summary>
    private static EnableState TargetState(
        FalloutSave save, Dictionary<uint, FalloutSave.ChangeFormHeader> byFormId, uint formId)
    {
        if (!byFormId.TryGetValue(formId, out var cf))
            return EnableState.Unknown;
        if ((cf.ChangeFlags & 1u) == 0) // CHANGE_FORM_FLAGS not set -> base flags not recorded here
            return EnableState.Unknown;

        var data = save.ReadAt(cf.DataOffset, Math.Min(cf.DataLength, 8));
        var fields = ReferenceChangeForm.Tokenize(data, cf.DataOffset);
        if (fields.Count == 0 || fields[0].AsUInt32 is not { } flags)
            return EnableState.Unknown;
        return (flags & InitiallyDisabledFormFlag) != 0 ? EnableState.Disabled : EnableState.Enabled;
    }

    /// <summary>Classifies the quest from its decoded stage entries: <see cref="QuestState.Completed"/> when a
    /// done stage is flagged "complete quest", <see cref="QuestState.Active"/> when any stage is recorded, else
    /// <see cref="QuestState.Unknown"/>.</summary>
    private static QuestState DeriveState(IReadOnlyList<QuestStageEntry> stages)
    {
        if (stages.Count == 0)
            return QuestState.Unknown;
        if (stages.Any(s => s.Done && (s.DefinitionFlags & CompleteQuestStageFlag) != 0))
            return QuestState.Completed;
        return QuestState.Active;
    }
}
