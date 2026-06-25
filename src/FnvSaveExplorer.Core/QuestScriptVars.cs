namespace FnvSaveExplorer.Core;

/// <summary>One persisted quest script local variable: its <see cref="Index"/> (the SLSD slot in the quest's
/// <c>SCPT</c>) and current <see cref="Value"/>. FNV stores every script local (short/long/float) as an f64
/// (ROADMAP §6 #16 Stage B).</summary>
public sealed record QuestScriptVar(int Index, double Value);

/// <summary>
/// Decodes the <b>quest script local-variable block</b> a save stores under <c>CHANGE_QUEST_SCRIPT</c>
/// (changeFlags bit 30, <see cref="ReferenceChangeForm.ChangeQuestScript"/>) — ROADMAP §6 #16 Stage B. The
/// payload of such a quest change form begins with the var block: <c>[vsval count][7C]</c> then
/// <c>count × ([u32 varIndex][7C][f64 value][7C])</c>. Confirmed on the corpus (e.g. NVDLC03TeleportEffectTimer:
/// count 2 → var 2 = a timer, var 3 = 1.0).
/// <para><b>Honesty boundary.</b> This recovers vars only for quests whose change form carries bit 30. Many
/// quests don't persist their locals at all (their change form is the fixed ref-style template with bit 30
/// clear, or they have no change form), so their do-once / gate flags are simply not in the save — the engine
/// recomputes them at load. Mapping a var <i>index</i> to its source name needs the <c>SCPT</c> <c>SLSD</c>
/// table, which is not yet read (the masters reader only keeps local-var <i>names</i> from source text); until
/// then vars are reported by index. Read-only.</para>
/// </summary>
public static class QuestScriptVars
{
    /// <summary>Every quest change form that persists a local-variable block, mapped to its decoded vars
    /// (save-space quest FormID → vars). Quests with bit 30 clear or an undecodable block are omitted.</summary>
    public static IReadOnlyDictionary<uint, IReadOnlyList<QuestScriptVar>> Read(FalloutSave save)
    {
        var result = new Dictionary<uint, IReadOnlyList<QuestScriptVar>>();
        foreach (var cf in save.EnumerateChangeForms())
        {
            if ((cf.ChangeFlags & ReferenceChangeForm.ChangeQuestScript) == 0)
                continue;
            if (Decode(save, cf) is { Count: > 0 } vars)
                result[cf.FormId] = vars;
        }
        return result;
    }

    /// <summary>Decodes the leading var block of a <c>CHANGE_QUEST_SCRIPT</c> change form, or an empty list when
    /// the payload doesn't match the <c>[vsval count] count×(u32 idx, f64 val)</c> shape (self-validating, so a
    /// non-var-block layout is rejected rather than mis-parsed).</summary>
    public static IReadOnlyList<QuestScriptVar> Decode(FalloutSave save, FalloutSave.ChangeFormHeader cf)
    {
        var data = save.ReadAt(cf.DataOffset, cf.DataLength);
        return DecodeFields(ReferenceChangeForm.Tokenize(data, cf.DataOffset));
    }

    /// <summary>The pure decoder over a tokenized payload (split out so it is unit-testable without a save).</summary>
    public static IReadOnlyList<QuestScriptVar> DecodeFields(IReadOnlyList<RefField> fields)
    {
        // First field is a 1-byte vsval count (low 2 bits = 0 -> 1-byte width; value = b >> 2).
        if (fields.Count == 0 || fields[0].Length != 1 || (fields[0].Bytes[0] & 0x03) != 0)
            return [];
        var n = fields[0].Bytes[0] >> 2;
        if (n < 1 || 1 + 2 * n > fields.Count)
            return [];

        var vars = new List<QuestScriptVar>(n);
        for (var k = 0; k < n; k++)
        {
            // Each var is (u32 SLSD index, f64 value). If the value isn't exactly 8 bytes this isn't the var
            // block (e.g. a ref-var layout we don't model) — bail rather than guess.
            if (fields[1 + 2 * k].AsUInt32 is not { } idx || fields[2 + 2 * k].AsDouble is not { } val)
                return [];
            vars.Add(new QuestScriptVar((int)idx, val));
        }
        return vars;
    }
}
