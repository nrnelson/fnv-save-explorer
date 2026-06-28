namespace FnvSaveExplorer.Core;

// ROADMAP §6 #1b — the labeled "FULL WALK" of a change-form payload. `Walk` renders a payload as a field
// tree, emitting labeled "+0xOFF  label = value" lines for the types whose structure SPEC §4l has pinned and
// an explicit "+0xOFF  unknown[n]  <hex>" gap for everything still undecoded — so coverage is always visible
// (never silently skipped). This is the consumer-facing surface of the decode (CLI `cfwalk`, and the planned
// GUI full-walk tab): as a type graduates from "located" to "field-decoded", its emitter here replaces the
// `unknown[n]`. Semantics stay unlabelled per "size, don't guess". Pure (bytes in, lines out) so it is
// unit-testable on synthetic payloads.
public static class ChangeFormPayload
{
    // FNV change-form type-byte hints. NOTE: the type byte is a change-CATEGORY, NOT the changed form's record
    // type (SPEC §4l — a `recid` census shows each type byte spans many record types and vice-versa). These are
    // only the COMMON association for the player's own records (the usual cfwalk target), as a reading aid — use
    // `recid` for a given record's actual masters record type. Most types stay "?" (decode by payload shape).
    public static string FormTypeName(int formType) => formType switch
    {
        0x01 => "REFR?",   // the player inventory ref is 0x01; but 0x01 ≠ "REFR" in general (§4l)
        0x02 => "ACHR?",   // the PlayerRef is 0x02; not a record-type tag
        0x1F => "NOTE-rd",  // read-note marker; its −1-hopped base is uniformly NOTE (§4k.1)
        _ => "?",
    };

    // Emits the field-tree lines for one change-form payload. The per-type emitters mirror the sized structures
    // in SPEC §4l; anything not yet pinned is one honest `unknown[n]` over the whole payload. When a
    // <paramref name="resolveRef"/> is supplied (a 3-byte BE refID -> display name), the decoded refID fields
    // (0x0D/0x22/0x25) show the resolved name alongside the raw bytes; without it they stay raw hex.
    public static IEnumerable<string> Walk(int formType, uint flags, byte[] d, Func<int, string?>? resolveRef = null)
    {
        if (d.Length == 0)
        {
            // 0x08 / 0x1F (and any other) zero-payload marker — the record's presence IS the state (§4l/§4k).
            yield return formType == 0x1F ? "(no payload — NOTE read marker, §4k)" : "(no payload — marker form, §4l)";
            yield break;
        }

        switch (formType)
        {
            // Fixed-width sized types (SPEC §4l). Each field labelled u32/i32; the 0x7C delimiters shown inline.
            case 0x2B when d.Length == 10 && d[4] == 0x7C && d[9] == 0x7C:
                // Reputation (§4o): [fame:f32][7C][infamy:f32][7C], keyed by the REPU faction (array[refID-1]).
                yield return Field(0, "fame",   BitConverter.ToSingle(d, 0).ToString());
                yield return Field(5, "infamy", BitConverter.ToSingle(d, 5).ToString());
                break;
            case 0x32 when d.Length == 10 && d[4] == 0x7C && d[9] == 0x7C:
                yield return Field(0, "u32[0]", U32(d, 0).ToString());   // per-form counter (§4l)
                yield return Field(5, "u32[1]", U32(d, 5).ToString());
                break;
            case 0x21 when d.Length == 20 && d[4] == 0x7C && d[9] == 0x7C && d[14] == 0x7C && d[19] == 0x7C:
                yield return Field(0, "u32[0]", U32(d, 0).ToString());
                yield return Field(5, "u32[1]", U32(d, 5).ToString());
                yield return Field(10, "u32[2]", U32(d, 10).ToString());
                yield return Field(15, "i32[3]", ((int)U32(d, 15)).ToString());   // observed always -1 (sentinel)
                break;
            case 0x20 when d.Length == 17 && d[16] == 0x7C:
                // Four packed u32 LE then a trailing delimiter (no internal 7C, §4l).
                yield return Field(0, "u32[0]", U32(d, 0).ToString());
                yield return Field(4, "u32[1]", U32(d, 4).ToString());
                yield return Field(8, "u32[2]", U32(d, 8).ToString());
                yield return Field(12, "u32[3]", U32(d, 12).ToString());
                break;
            case 0x28 when d.Length == 62 && d[0] == 0x10 && d[1] == 0x7C:
                // [10][7C] then 4× [u8 u8 u8][7C][u16][7C][u16][7C][f32][7C] (§4l).
                yield return Field(0, "tag", "0x10");
                for (var e = 0; e < 4; e++)
                {
                    var o = 2 + e * 15;
                    if (o + 15 > d.Length) { yield return Gap(o, d, d.Length - o); break; }
                    // entry = [3 B][7C][u16][7C][u16][7C][f32][7C] (§4l): f32 (always 1.0) at o+10.
                    var f = BitConverter.ToSingle(d, o + 10);
                    yield return Field(o, $"entry[{e}]",
                        $"b={Hex(d, o, 3)} u16={(ushort)(d[o + 4] | (d[o + 5] << 8))} " +
                        $"u16={(ushort)(d[o + 7] | (d[o + 8] << 8))} f32={f}");
                }
                break;
            case 0x0B when d.Length == 25 && d[24] == 0x7C:
                // Constant 24-byte config block on vanilla (byte-identical); other variants on modded (§4l).
                yield return Field(0, "config[24]", Hex(d, 0, 24));
                break;
            // 0x25 — count-prefixed refID list: [u32 count][7C] then count × [ref:3 BE][7C] (§4l, len = 5+4·count).
            case 0x25 when d.Length >= 5 && d[4] == 0x7C && U32(d, 0) is var c25 &&
                           c25 <= 0x100000 && d.Length == 5 + 4 * (int)c25:
                yield return Field(0, "count", c25.ToString());
                for (var e = 0; e < (int)c25; e++)
                {
                    var o = 5 + e * 4;
                    yield return Field(o, $"ref[{e}]", RefStr(d, o, resolveRef));   // 3-byte BE refID
                }
                break;
            // 0x22 — count-prefixed list: [n:u8][7C] then n/4 × [ref:3 BE][7C][u32][7C][u32][7C] (§4l, len = 2+14·n/4).
            case 0x22 when d.Length >= 2 && d[1] == 0x7C && (d[0] & 3) == 0 && d[0] / 4 is var c22 &&
                           d.Length == 2 + 14 * c22:
                yield return Field(0, "count*4", $"{d[0]} ({c22} entries)");
                for (var e = 0; e < c22; e++)
                {
                    var o = 2 + e * 14;
                    yield return Field(o, $"entry[{e}]",
                        $"ref={RefStr(d, o, resolveRef)} u32={U32(d, o + 4)} u32={U32(d, o + 9)}");
                }
                break;

            // 0x00 map-marker / per-form flags (§4m): the len-6 [04][7C][2C][7C][flags:u8][7C] shape. For a map
            // marker REFR the flags byte is the visibility/discovery state (GECK: bit0 0x01 Visible, bit1 0x02
            // Can-Travel-To); the same shape carries a generic Seen/Enabled flag on other forms (e.g. INFO).
            case 0x00 when d.Length == 6 && d[0] == 0x04 && d[1] == 0x7C && d[2] == 0x2C && d[3] == 0x7C && d[5] == 0x7C:
            {
                var mf = d[4];
                var bits = $"{((mf & 0x01) != 0 ? "Visible" : "-")}|{((mf & 0x02) != 0 ? "CanTravelTo" : "-")}";
                yield return Field(0, "tag", "0x04");
                yield return Field(2, "field", "0x2C");
                yield return Field(4, "flags", $"0x{mf:X2} ({bits})");   // §4m map-marker visibility / per-form flags
                break;
            }

            // 0x0A actor/havok state — the dominant fixed 58-byte variant (flags 0x020C, ≥99% of all 0x0A).
            // 0x7C-delimited into three spans (delimiters at +0x07/+0x1C/+0x39) with a constant 0x32 tag at
            // +0x0B and zero padding at +0x2B..+0x38. Variable bytes are float-shaped placement/havok data —
            // SIZED (fixed length + pinned constants + span boundaries), not yet field-named (needs §7 diffs).
            case 0x0A when d.Length == 58 && flags == 0x0000020C
                           && d[7] == 0x7C && d[28] == 0x7C && d[57] == 0x7C && d[11] == 0x32:
                yield return Field(0, "span0[7]", Hex(d, 0, 7));
                yield return Field(8, "span1[20]", Hex(d, 8, 20) + "  (const 0x32 tag @+0x0B)");
                yield return Field(29, "span2[28]", Hex(d, 29, 28) + "  (zero padding @+0x2B..+0x38)");
                break;

            // 0x07 — the two cross-corpus-stable fixed variants (§4l). Both open with an 8-byte body + 7C:
            // len 9 (flags 0x60000000 dominant) and len 42 (flags 0xE0000000) = that header + a 32-byte
            // variable block (float-heavy quest/state data, not field-named) + a trailing 7C.
            case 0x07 when d.Length == 9 && d[8] == 0x7C:
                yield return Field(0, "u32[0]", U32(d, 0).ToString());
                yield return Field(4, "u32[1]", U32(d, 4).ToString());
                break;
            case 0x07 when d.Length == 42 && d[8] == 0x7C && d[41] == 0x7C:
                yield return Field(0, "u32[0]", U32(d, 0).ToString());
                yield return Field(4, "u32[1]", U32(d, 4).ToString());
                yield return Gap(9, d, 32);   // [+0x09..+0x28] variable block (float-heavy), then 7C
                break;

            // 0x09 (flags 0x40000000) — one count-prefixed family covering the len-6 stub and its list variants
            // (§4l): [n:u8][7C] then n/4 × 14-byte entry ([u32][7C][8 B][7C]) then a [00][7C][00][7C] trailer.
            // len = 6 + 14·(n/4) (n = 0/4/8/12/16 → len 6/20/34/48/62), verified across vanilla + base VNV.
            case 0x09 when flags == 0x40000000 && d.Length >= 6 && d[1] == 0x7C && (d[0] & 3) == 0
                           && d.Length == 6 + 14 * (d[0] / 4)
                           && d[^4] == 0x00 && d[^3] == 0x7C && d[^2] == 0x00 && d[^1] == 0x7C:
            {
                var n09 = d[0] / 4;
                yield return Field(0, "count", $"{d[0]} ({n09} entries)");
                for (var e = 0; e < n09; e++)
                {
                    var o = 2 + e * 14;
                    yield return Field(o, $"entry[{e}]", $"u32={U32(d, o)}  data={Hex(d, o + 5, 8)}");
                }
                yield return Field(d.Length - 4, "trailer", "00 7C 00 7C");
                break;
            }

            // 0x0A player CHANGE_ACTOR record — the gated-section actor base data (added-spell list incl.
            // addictions, SPECIAL, name, then optional AI package data). Self-validating, so any 0x0A record that
            // parses as this structure is rendered as labeled sections; the rest fall to the token view below.
            case 0x0A when ChangeActorPayload.TryDecode(d, flags, out _):
                foreach (var line in ChangeActorPayload.Walk(flags, d, resolveRef)) yield return line;
                break;

            // Delimited script/animation/actor state — STRUCTURE known (0x7C-tokenized with embedded
            // [u16 len][7C][ascii][7C] strings), FIELDS not yet labelled (§4l: 0x00, 0x0A). Show the tokens so
            // the structure is visible while the whole payload remains honestly "not field-decoded".
            case 0x00 or 0x0A:
                yield return $"(0x7C-delimited {FormTypeName(formType)} state, §4l — tokens; fields not yet labelled)";
                foreach (var line in DelimitedTokens(d)) yield return line;
                break;

            // REFR/ACHR — break the payload into the deterministically-LOCATED structural spans (§4i/§4j): the
            // MOVE block, the havok/actor-value array, then the ExtraDataList + inventory. Each stays a labeled
            // span (located, not byte-decoded here); `refdump`/`inventory` give the byte-level walk + item list.
            case 0x01 or 0x02:
            {
                var hasMove = (flags & ReferenceChangeForm.ChangeRefrMove) != 0
                              && ReferenceChangeForm.MoveBlockLength < d.Length
                              && d[ReferenceChangeForm.MoveBlockLength] == ReferenceChangeForm.Delimiter;
                if (!hasMove)
                {
                    yield return "(REFR/ACHR — no MOVE block to anchor; byte-level walk via `refdump`)";
                    yield return Gap(0, d, d.Length);
                    break;
                }
                yield return Field(0, "MOVE block[27]", Hex(d, 0, 27) + "  (cell ref + pos + rot, §4i)");
                var afterMove = ReferenceChangeForm.MoveBlockLength + 1;   // past the 0x7C delimiter
                var listStart = ReferenceChangeForm.InventorySearchStart(d, 0, flags);
                if (listStart > afterMove)
                    yield return $"+0x{afterMove:X3}  unknown[{listStart - afterMove}]  havok/actor-value array (§4i/§4j; karma/XP at slots 100/101)";
                if (listStart < d.Length)
                    yield return $"+0x{listStart:X3}  unknown[{d.Length - listStart}]  ExtraDataList + inventory (§4g–§4i; decode via `refdump`/`inventory`)";
                break;
            }

            // Small single-value types (SPEC §4l), corpus-fixed across all three corpora:
            // 0x0F/0x16/0x1A = [u32][7C] (len 5 always); 0x0D = [u32][7C] (len 5) or [refID:3 BE][7C] (len 4).
            case 0x0F or 0x16 or 0x1A or 0x0D when d.Length == 5 && d[4] == 0x7C:
                yield return Field(0, "u32", U32(d, 0).ToString());
                break;
            case 0x0D when d.Length == 4 && d[3] == 0x7C:
                yield return Field(0, "ref", RefStr(d, 0, resolveRef));   // 3-byte BE refID
                break;

            default:
                yield return Gap(0, d, d.Length);
                break;
        }
    }

    static string Hex(byte[] b, int off, int len) =>
        string.Join(' ', b.Skip(off).Take(len).Select(x => x.ToString("X2")));

    // A 3-byte big-endian refID field: raw hex, plus the resolved display name when a resolver is supplied
    // and recognises it (the refID's 2-bit type is honoured by the caller's resolver, e.g. FalloutSave.ResolveRefId).
    static string RefStr(byte[] b, int off, Func<int, string?>? resolve)
    {
        var hex = Hex(b, off, 3);
        if (resolve is null)
            return hex;
        var refId = (b[off] << 16) | (b[off + 1] << 8) | b[off + 2];
        return resolve(refId) is { } name ? $"{hex} -> {name}" : hex;
    }

    static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    static string Field(int off, string label, string val) => $"+0x{off:X3}  {label} = {val}";

    static string Gap(int off, byte[] b, int len)
    {
        const int preview = 48;   // keep the gap honest about its size without flooding on a big REFR payload
        var head = Hex(b, off, Math.Min(len, preview));
        return len <= preview ? $"+0x{off:X3}  unknown[{len}]  {head}"
                              : $"+0x{off:X3}  unknown[{len}]  {head} … (+{len - preview} more)";
    }

    // Splits a 0x7C-delimited payload into tokens, rendering each as the most plausible reading: an embedded
    // length-prefixed ASCII string ([u16 len][7C][bytes]) prints as text; an all-printable run as text; a
    // 4-byte run as u32/float; otherwise raw hex. Display-only (does not claim a field layout).
    static IEnumerable<string> DelimitedTokens(byte[] d)
    {
        var start = 0;
        var idx = 0;
        for (var i = 0; i <= d.Length; i++)
        {
            if (i != d.Length && d[i] != 0x7C) continue;
            var len = i - start;
            if (len > 0)
            {
                var seg = d.Skip(start).Take(len).ToArray();
                string render;
                if (seg.All(b => b >= 0x20 && b < 0x7F))
                    render = $"\"{System.Text.Encoding.ASCII.GetString(seg)}\"";
                else if (len == 4)
                {
                    var u = (uint)(seg[0] | (seg[1] << 8) | (seg[2] << 16) | (seg[3] << 24));
                    render = $"u32={u} f32={BitConverter.ToSingle(seg, 0)}";
                }
                else
                    render = string.Join(' ', seg.Select(x => x.ToString("X2")));
                yield return $"+0x{start:X3}  tok[{idx}] ({len}b) {render}";
            }
            idx++;
            start = i + 1;
        }
    }
}
