namespace FnvSaveExplorer.Core;

/// <summary>One decoded Global-Variable entry (GlobalData table-1 type 3): a <c>GLOB</c> reference and its
/// current float value, plus the <b>absolute file offset</b> of the value (so it can be edited with a
/// same-length float splice — see <see cref="FalloutSave.TrySetGlobalVariable(uint,float)"/>).</summary>
/// <param name="RefId">The raw 3-byte big-endian refID as stored in the record.</param>
/// <param name="FormId">The save-space FormID the refID resolves to (0 if unresolved) — name it via the masters.</param>
/// <param name="Value">The variable's current value (float32).</param>
/// <param name="ValueOffset">Absolute file offset of the 4-byte float value (the edit point).</param>
public readonly record struct GlobalVariable(int RefId, uint FormId, float Value, int ValueOffset);

/// <summary>
/// ROADMAP §4c / §2 — the field-by-field decode of the <b>GlobalData table-1</b> records. Mirrors
/// <see cref="ChangeFormPayload"/>: <see cref="Walk"/> renders a payload as a labeled field tree, emitting an
/// explicit <c>unknown[n]</c> gap for everything still undecoded (so coverage is always visible, never silently
/// skipped). Pure (bytes in, lines out / entries out) so it is unit-testable on synthetic payloads.
///
/// <para>Decoded so far (corpus-validated across all 607 real saves): <b>type 0</b> Misc Stats
/// (<c>[u32 count][7C]</c> + count×<c>[u32][7C]</c>, positional — see <see cref="MiscStats"/>); <b>type 2</b>
/// TES state-changed registry (see <see cref="FalloutSave.DecodeStateChangedRefs"/>); <b>type 3</b> Global
/// Variables (below — fully sized + editable). <b>Types 1/4/6</b> are rendered as their structural <c>0x7C</c>
/// token tree (boundaries + primitive kind + resolved refIDs) — the layout is visible but the fields are not yet
/// semantically named (needs §7 controlled diffs). <b>Types 5/7–11</b> are one honest <c>unknown[n]</c>.</para>
/// </summary>
public static class GlobalDataDecoder
{
    private const byte Delimiter = 0x7C;

    /// <summary>Decodes a GlobalData <b>type-3 (Global Variables)</b> payload into its entries:
    /// <c>[vsval count][7C]</c> then <c>count × ([refID:3 BE][7C][value:f32 LE][7C])</c> (9 bytes/entry).
    /// Verified self-consistent on all 607 real saves (<c>payload = vlen + 1 + 9·count</c>; e.g. vanilla 1803 =
    /// 2+1+9·200). <paramref name="dataOffset"/> is the payload's absolute file offset, so each entry's
    /// <see cref="GlobalVariable.ValueOffset"/> is absolute (the edit point). <paramref name="resolve"/> maps a
    /// raw 3-byte refID to a save-space FormID (<see cref="FalloutSave.ResolveRefId"/>). <paramref name="consumed"/>
    /// reports how many payload bytes were decoded — equal to the payload length on a clean save.</summary>
    public static IReadOnlyList<GlobalVariable> DecodeGlobalVariables(
        byte[] data, int dataOffset, Func<int, uint> resolve, out int consumed)
    {
        consumed = 0;
        if (data.Length == 0)
            return [];
        var count = ReferenceChangeForm.ReadVsval(data, 0, out var vlen);
        var p = vlen;
        if (count <= 0)
        {
            consumed = p;
            return [];
        }
        if (p < data.Length && data[p] == Delimiter) p++;   // delimiter after the count prefix
        var result = new List<GlobalVariable>((int)Math.Min(count, 65536));
        for (long i = 0; i < count; i++)
        {
            if (p + 9 > data.Length) break;                  // entry = [ref:3][7C][f32:4][7C]
            if (data[p + 3] != Delimiter || data[p + 8] != Delimiter) break;
            var refId = (data[p] << 16) | (data[p + 1] << 8) | data[p + 2];
            var value = BitConverter.ToSingle(data, p + 4);
            result.Add(new GlobalVariable(refId, resolve(refId), value, dataOffset + p + 4));
            p += 9;
        }
        consumed = p;
        return result;
    }

    /// <summary>Renders one GlobalData record payload as a labeled field tree (CLI <c>gdwalk</c> / the GUI Globals
    /// tab). <paramref name="resolve"/> maps a refID to a FormID; <paramref name="name"/> maps a FormID to a display
    /// name (both optional — pass null to show raw refIDs).</summary>
    public static IEnumerable<string> Walk(
        uint type, byte[] d, Func<int, uint>? resolve = null, Func<uint, string?>? name = null)
    {
        resolve ??= _ => 0;
        name ??= _ => null;
        if (d.Length == 0)
        {
            yield return "(empty payload)";
            yield break;
        }
        switch (type)
        {
            case 0:  foreach (var l in MiscStatsWalk(d)) yield return l; break;
            case 2:  foreach (var l in RegistryWalk(d, resolve, name)) yield return l; break;
            case 3:  foreach (var l in GlobalVariablesWalk(d, resolve, name)) yield return l; break;
            case 1 or 4 or 6:
                yield return $"(GlobalData type {type} — 0x7C token structure; fields not yet semantically named, §4c)";
                foreach (var l in TokenWalk(d, resolve, name)) yield return l;
                break;
            default:
                yield return Gap(0, d, d.Length) + $"  (GlobalData type {type} — not yet decoded, §4c)";
                break;
        }
    }

    // type 0 — Misc Stats: [u32 count][7C] then count × [u32 value][7C] (positional counters, §6.8 / MiscStats).
    private static IEnumerable<string> MiscStatsWalk(byte[] d)
    {
        if (d.Length < 5 || d[4] != Delimiter) { yield return Gap(0, d, d.Length); yield break; }
        var count = U32(d, 0);
        yield return Field(0, "count", count.ToString());
        var p = 5;
        for (var i = 0; i < count && p + 5 <= d.Length; i++, p += 5)
            yield return Field(p, $"stat[{i}]", U32(d, p).ToString());
        if (p < d.Length) yield return Gap(p, d, d.Length - p);
    }

    // type 2 — TES state-changed registry: [vsval count][7C] then count × [ref:3 BE][7C][u16 status][7C] (§4c).
    private static IEnumerable<string> RegistryWalk(byte[] d, Func<int, uint> resolve, Func<uint, string?> name)
    {
        var count = ReferenceChangeForm.ReadVsval(d, 0, out var vlen);
        yield return Field(0, "count", count.ToString());
        var p = vlen;
        if (p < d.Length && d[p] == Delimiter) p++;
        for (long i = 0; i < count; i++)
        {
            if (p + 3 > d.Length) break;
            var refId = (d[p] << 16) | (d[p + 1] << 8) | d[p + 2];
            p += 3;
            if (p < d.Length && d[p] == Delimiter) p++;
            if (p + 2 > d.Length) break;
            var status = d[p] | (d[p + 1] << 8);
            p += 2;
            if (p < d.Length && d[p] == Delimiter) p++;
            yield return Field(p - 6, $"ref[{i}]", $"{RefName(refId, resolve, name)}  status={status}");
        }
        if (p < d.Length) yield return Gap(p, d, d.Length - p);
    }

    // type 3 — Global Variables: [vsval count][7C] then count × [ref:3 BE][7C][f32 LE][7C] (§4c, editable).
    private static IEnumerable<string> GlobalVariablesWalk(byte[] d, Func<int, uint> resolve, Func<uint, string?> name)
    {
        var count = ReferenceChangeForm.ReadVsval(d, 0, out _);
        yield return Field(0, "count", count.ToString());
        var vars = DecodeGlobalVariables(d, 0, resolve, out var consumed);
        foreach (var v in vars)
            yield return Field(v.ValueOffset - 4, RefName(v.RefId, resolve, name), v.Value.ToString());
        if (consumed < d.Length) yield return Gap(consumed, d, d.Length - consumed);
    }

    // types 1/4/6 — split the 0x7C-delimited payload into tokens, rendering each as its most plausible reading:
    // a 3-byte token as a resolvable refID, a 4-byte run as u32/float, otherwise raw hex. Structure-only (does not
    // assert a semantic field layout — that needs §7 controlled diffs).
    private static IEnumerable<string> TokenWalk(byte[] d, Func<int, uint> resolve, Func<uint, string?> name)
    {
        var start = 0;
        var idx = 0;
        for (var i = 0; i <= d.Length; i++)
        {
            if (i != d.Length && d[i] != Delimiter) continue;
            var len = i - start;
            if (len > 0)
            {
                string render;
                if (len == 3)
                {
                    var refId = (d[start] << 16) | (d[start + 1] << 8) | d[start + 2];
                    render = $"ref {RefName(refId, resolve, name)}";
                }
                else if (len == 4)
                    render = $"u32={U32(d, start)} f32={BitConverter.ToSingle(d, start)}";
                else
                    render = Hex(d, start, len);
                yield return $"+0x{start:X3}  tok[{idx}] ({len}b) {render}";
            }
            idx++;
            start = i + 1;
        }
    }

    private static string RefName(int refId, Func<int, uint> resolve, Func<uint, string?> name)
    {
        var formId = resolve(refId);
        if (formId == 0)
            return $"refId 0x{refId:X6}";
        var n = name(formId);
        return n is null ? $"0x{formId:X8}" : $"0x{formId:X8} {n}";
    }

    private static string Hex(byte[] b, int off, int len) =>
        string.Join(' ', b.Skip(off).Take(len).Select(x => x.ToString("X2")));

    private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static string Field(int off, string label, string val) => $"+0x{off:X3}  {label} = {val}";

    private static string Gap(int off, byte[] b, int len)
    {
        const int preview = 48;
        var head = Hex(b, off, Math.Min(len, preview));
        return len <= preview ? $"+0x{off:X3}  unknown[{len}]  {head}"
                              : $"+0x{off:X3}  unknown[{len}]  {head} … (+{len - preview} more)";
    }
}
