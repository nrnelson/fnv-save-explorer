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

/// <summary>One objective of a quest: its index and display text (from the masters), the <b>save-side
/// display/complete status</b> read from the quest change form's <c>CHANGE_QUEST_OBJECTIVES</c> block
/// (ROADMAP §6 #10 — status byte: bit0 = displayed, bit1 = completed, confirmed by a controlled diff), and
/// its target refs with their enable-state. <see cref="Displayed"/>/<see cref="Completed"/> are <c>null</c>
/// when the change form records no objective status (e.g. the packed formType-7 quests, or a quest the
/// player hasn't touched). <see cref="Active"/> = displayed and not yet completed (the current objective).</summary>
public sealed record QuestObjective(
    int Index, string? Text, bool? Displayed, bool? Completed, IReadOnlyList<QuestTarget> Targets)
{
    /// <summary>The objective is the current one shown in the Pip-Boy: displayed and not yet completed.
    /// <c>null</c> when no save-side status was recorded.</summary>
    public bool? Active => Displayed is null ? null : Displayed == true && Completed != true;
}

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
/// <item><b>Objective display/complete state</b> — the quest change form's <c>CHANGE_QUEST_OBJECTIVES</c>
/// block (bit29) stores, after the stage list, <c>[vsval count][7C]</c> then <c>count ×
/// ([u32 objIndex][7C][u32 status][7C])</c>. The status is a bitfield — <b>bit0 = displayed, bit1 =
/// completed</b> (confirmed by a controlled diff: Saves 56→57, a quest completion flipped objective 70's
/// status <c>1 → 3</c>). This is the same signal the Pip-Boy renders, so it's the authoritative objective
/// state. The objective's <c>QSTA</c> target refs and their enable-state are also surfaced (secondary).</item>
/// </list>
/// <para><b>Scope (honesty boundary).</b> This reads the quest progress the save actually records: stage
/// lists and objective display/complete status for quests the player has advanced. It is <b>not</b> a
/// complete Pip-Boy mirror — Start-Game-Enabled quests sitting at their masters default (e.g. the DLC intro
/// quests, or "They Went That-a-Way" which has no change form at all) are displayed by the engine from the
/// masters with no save delta, and the packed formType-7 stage encoding (e.g. "Ain't That a Kick") is not
/// decoded. Those won't appear here. See ROADMAP §6 #10.</para>
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
    /// change forms can't be classified without them).
    /// <para>By default only quests that match the player's Pip-Boy are returned (see <see cref="InPlayerLog"/>):
    /// a displayed objective, or a completed log-entry stage. Pass <paramref name="includeAll"/> to instead
    /// return every quest with <i>any</i> decodable progress — including internal/tracker quests with stage
    /// state but no player-facing log line (e.g. <c>NVDLC04Ending</c>).</para></summary>
    public static QuestLog Read(FalloutSave save, PluginDatabase db, bool includeAll = false)
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
            var objectives = ReadObjectives(save, cf, byFormId, def);

            // Need *some* decodable progress: a stage list, or an objective the change form marks
            // displayed/completed. Quests with neither (pure dialogue/timer forms, or masters-default quests
            // with no save delta) carry nothing to show.
            var hasObjectiveState = objectives.Any(o => o.Displayed == true || o.Completed == true);
            if (stages.Count == 0 && !hasObjectiveState)
                continue;

            // Default view: only quests the Pip-Boy would render. includeAll keeps the rest (R&D).
            if (!includeAll && !InPlayerLog(stages, objectives))
                continue;

            var state = DeriveState(stages, objectives);
            quests.Add(new Quest(cf.FormId, db.Resolve(cf.FormId), state, cf.ChangeFlags, stages, objectives));
        }

        return new QuestLog(quests);
    }

    /// <summary>The player-facing gate (ROADMAP §6 #10): a quest matches the in-game Pip-Boy when the save
    /// records a <b>displayed objective</b> (status bit0 — the live signal the Pip-Boy renders) or a
    /// <b>completed log-entry stage</b> (a done stage with Pip-Boy log text). This drops internal/tracker quests
    /// that carry stage state but never wrote a player-facing line (their stages have no <c>CNAM</c> log text and
    /// their objectives aren't masters-defined). It does <i>not</i> recover Start-Game-Enabled quests sitting at
    /// their masters default (no save delta) — those are unreachable statically (ROADMAP §6 #10).</summary>
    private static bool InPlayerLog(
        IReadOnlyList<QuestStageEntry> stages, IReadOnlyList<QuestObjective> objectives)
        => objectives.Any(o => o.Displayed == true)
        || stages.Any(s => s.Done && s.LogText is { Length: > 0 });

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

    /// <summary>Joins each masters objective to its save-side display/complete status (from the change form's
    /// <c>CHANGE_QUEST_OBJECTIVES</c> block) and its target refs' enable-state (ROADMAP §6 #10). Returns an
    /// empty list when the quest has no masters definition.</summary>
    private static IReadOnlyList<QuestObjective> ReadObjectives(
        FalloutSave save, FalloutSave.ChangeFormHeader cf,
        Dictionary<uint, FalloutSave.ChangeFormHeader> byFormId, QuestDefinition? def)
    {
        if (def is null || def.Objectives.Count == 0)
            return [];

        var statuses = ReadObjectiveStatuses(save, cf, def); // objIndex -> status byte, or null if none recorded

        var objectives = new List<QuestObjective>(def.Objectives.Count);
        foreach (var o in def.Objectives)
        {
            var targets = new List<QuestTarget>(o.TargetFormIds.Count);
            foreach (var formId in o.TargetFormIds)
                targets.Add(new QuestTarget(formId, TargetState(save, byFormId, formId)));

            // When the change form records objective status, an objective not listed is implicitly status 0
            // (not displayed, not completed); when it records none at all, status is unknown (null).
            bool? displayed = null, completed = null;
            if (statuses is not null)
            {
                var st = statuses.GetValueOrDefault(o.Index);
                displayed = (st & ObjectiveDisplayedBit) != 0;
                completed = (st & ObjectiveCompletedBit) != 0;
            }
            objectives.Add(new QuestObjective(o.Index, o.Text, displayed, completed, targets));
        }
        return objectives;
    }

    /// <summary>The objective-status bits in the <c>CHANGE_QUEST_OBJECTIVES</c> block, confirmed by a
    /// controlled diff (Saves 56→57: completing a quest flipped objective 70's status <c>1 → 3</c>).</summary>
    private const int ObjectiveDisplayedBit = 0x01;
    private const int ObjectiveCompletedBit = 0x02;

    /// <summary>Decodes the quest change form's <c>CHANGE_QUEST_OBJECTIVES</c> block (ROADMAP §6 #10): after the
    /// stage list it holds <c>[vsval count][7C]</c> then <c>count × ([u32 objIndex][7C][u32 status][7C])</c>.
    /// Rather than parse every preceding section, this scans for the self-validating block — a small count
    /// immediately followed by that many (index, status) pairs whose <i>index</i> is one of the quest's masters
    /// objective indices and whose <i>status</i> is a small bitfield. Returns objIndex→status, or <c>null</c>
    /// when the flag is absent or no block validates (e.g. the packed formType-7 encoding).</summary>
    private static Dictionary<int, int>? ReadObjectiveStatuses(
        FalloutSave save, FalloutSave.ChangeFormHeader cf, QuestDefinition def)
    {
        if ((cf.ChangeFlags & ReferenceChangeForm.ChangeQuestObjectives) == 0)
            return null;

        var indices = def.Objectives.Select(o => o.Index).ToHashSet();
        if (indices.Count == 0)
            return null;

        var data = save.ReadAt(cf.DataOffset, cf.DataLength);
        var fields = ReferenceChangeForm.Tokenize(data, cf.DataOffset);

        for (var i = 0; i < fields.Count; i++)
        {
            // A single-byte vsval count (low 2 bits = 0 -> 1-byte width; value = b >> 2).
            if (fields[i].Length != 1 || (fields[i].Bytes[0] & 0x03) != 0)
                continue;
            var n = fields[i].Bytes[0] >> 2;
            if (n < 1 || n > indices.Count || i + 2 * n >= fields.Count)
                continue;

            var block = new Dictionary<int, int>(n);
            var ok = true;
            for (var k = 0; k < n; k++)
            {
                if (fields[i + 1 + 2 * k].AsUInt32 is not { } idx ||
                    fields[i + 2 + 2 * k].AsUInt32 is not { } status ||
                    !indices.Contains((int)idx) || status > 0x0F) // status is a small bitfield (displayed/completed)
                {
                    ok = false;
                    break;
                }
                block[(int)idx] = (int)status;
            }
            if (ok && block.Count == n)
                return block; // first self-validating block
        }
        return null;
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

    /// <summary>Classifies the quest from its decoded stages and objective status: <see
    /// cref="QuestState.Completed"/> when a done stage is flagged "complete quest" or every displayed objective
    /// is completed; <see cref="QuestState.Active"/> when an objective is displayed-and-incomplete or any stage
    /// is recorded; else <see cref="QuestState.Unknown"/>.</summary>
    private static QuestState DeriveState(
        IReadOnlyList<QuestStageEntry> stages, IReadOnlyList<QuestObjective> objectives)
    {
        if (stages.Any(s => s.Done && (s.DefinitionFlags & CompleteQuestStageFlag) != 0))
            return QuestState.Completed;

        var displayed = objectives.Where(o => o.Displayed == true).ToList();
        if (displayed.Count > 0)
            return displayed.All(o => o.Completed == true) ? QuestState.Completed : QuestState.Active;

        return stages.Count > 0 ? QuestState.Active : QuestState.Unknown;
    }
}
