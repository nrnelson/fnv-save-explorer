using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

// Pins the SPEC §4l payload emitters in ChangeFormPayload.Walk (the cfwalk full-walk surface). Synthetic
// payloads mirror the corpus-aligned shapes; the discipline is "no spurious unknown[ for a sized group, and
// an honest unknown[ gap for anything not pinned".
public class ChangeFormPayloadTests
{
    static List<string> Walk(int type, uint flags, byte[] d) => ChangeFormPayload.Walk(type, flags, d).ToList();

    [Fact]
    public void Empty_payload_is_a_marker_line()
    {
        Assert.Equal("(no payload — NOTE read marker, §4k)", Walk(0x1F, 0x80000000, [])[0]);
        Assert.Equal("(no payload — marker form, §4l)", Walk(0x08, 0x80000000, [])[0]);
    }

    [Theory]
    [InlineData(0x03, "Visible|CanTravelTo")]   // discovered (§4m)
    [InlineData(0x01, "Visible|-")]             // told-about (greyed marker)
    [InlineData(0x00, "-|-")]                   // not yet visible
    public void Type00_map_marker_flags_decode(byte flags, string expectedBits)
    {
        // §4m: [04][7C][2C][7C][flags:u8][7C].
        var d = new byte[] { 0x04, 0x7C, 0x2C, 0x7C, flags, 0x7C };
        var lines = Walk(0x00, 0x80000000, d);

        Assert.DoesNotContain(lines, l => l.Contains("unknown["));
        Assert.Contains(lines, l => l.Contains("tag = 0x04"));
        Assert.Contains(lines, l => l.Contains("field = 0x2C"));
        Assert.Contains(lines, l => l.Contains($"flags = 0x{flags:X2} ({expectedBits})"));
    }

    [Fact]
    public void Type00_non_marker_falls_through_to_token_view()
    {
        // A len-6 0x00 that is NOT the [04 7C 2C 7C .. 7C] shape must not be mis-decoded as a map marker.
        var d = new byte[] { 0x09, 0x7C, 0x00, 0x7C, 0x01, 0x7C };
        var lines = Walk(0x00, 0x90000000, d);
        Assert.DoesNotContain(lines, l => l.Contains("tag = 0x04"));
        Assert.Contains(lines, l => l.Contains("tokens; fields not yet labelled"));
    }

    [Fact]
    public void Type0A_len58_020C_breaks_into_three_spans()
    {
        // §4l dominant actor/havok record: 7C at +0x07/+0x1C/+0x39, const 0x32 tag at +0x0B.
        var d = new byte[58];
        d[7] = 0x7C; d[28] = 0x7C; d[57] = 0x7C; d[11] = 0x32;
        var lines = Walk(0x0A, 0x0000020C, d);

        Assert.DoesNotContain(lines, l => l.Contains("unknown["));
        Assert.Contains(lines, l => l.Contains("span0[7]"));
        Assert.Contains(lines, l => l.Contains("span1[20]"));
        Assert.Contains(lines, l => l.Contains("span2[28]"));
    }

    [Fact]
    public void Type0A_len58_wrong_flags_falls_through_to_token_view()
    {
        var d = new byte[58];
        d[7] = 0x7C; d[28] = 0x7C; d[57] = 0x7C; d[11] = 0x32;
        var lines = Walk(0x0A, 0x00000034, d);   // not the 0x020C dominant variant
        Assert.Contains(lines, l => l.Contains("tokens; fields not yet labelled"));
    }

    [Fact]
    public void Type07_len9_is_two_u32()
    {
        // [u32=1][u32=156][7C]  (len 9).
        var d = new byte[] { 0x01, 0, 0, 0, 0x9C, 0, 0, 0, 0x7C };
        var lines = Walk(0x07, 0x60000000, d);

        Assert.DoesNotContain(lines, l => l.Contains("unknown["));
        Assert.Contains(lines, l => l.Contains("u32[0] = 1"));
        Assert.Contains(lines, l => l.Contains("u32[1] = 156"));
    }

    [Fact]
    public void Type07_len42_is_header_plus_one_gap()
    {
        // [u32][u32][7C] then a 32-byte variable block then 7C.
        var d = new byte[42];
        d[8] = 0x7C; d[41] = 0x7C;
        var lines = Walk(0x07, 0xE0000000, d);

        Assert.Contains(lines, l => l.Contains("u32[0]"));
        Assert.Contains(lines, l => l.Contains("u32[1]"));
        var gaps = lines.Where(l => l.Contains("unknown[")).ToList();
        Assert.Single(gaps);
        Assert.Contains("unknown[32]", gaps[0]);
    }

    [Theory]
    [InlineData(0, 6)]    // the bare stub: [00][7C] + [00 7C 00 7C]
    [InlineData(4, 20)]   // 1 entry
    [InlineData(8, 34)]   // 2 entries
    [InlineData(12, 48)]  // 3 entries
    public void Type09_count_prefixed_family_is_sized(byte n, int len)
    {
        // §4l: [n][7C] + n/4 × 14-byte entry + [00][7C][00][7C], len = 6 + 14·(n/4).
        Assert.Equal(len, 6 + 14 * (n / 4));
        var d = new byte[len];
        d[0] = n; d[1] = 0x7C;
        for (var e = 0; e < n / 4; e++)
        {
            var o = 2 + e * 14;
            d[o + 4] = 0x7C;   // entry internal delimiter
            d[o + 13] = 0x7C;  // entry terminator
        }
        // [00][7C][00][7C] trailer
        d[len - 3] = 0x7C; d[len - 1] = 0x7C;
        var lines = Walk(0x09, 0x40000000, d);

        Assert.DoesNotContain(lines, l => l.Contains("unknown["));
        Assert.Contains(lines, l => l.Contains($"count = {n} ({n / 4} entries)"));
        Assert.Equal(n / 4, lines.Count(l => l.Contains("entry[")));
        Assert.Contains(lines, l => l.Contains("trailer = 00 7C 00 7C"));
    }

    [Fact]
    public void Type09_wrong_trailer_is_an_honest_gap()
    {
        // A len-20 0x09/0x40000000 whose trailer is not 00 7C 00 7C must not be mis-sized.
        var d = new byte[20];
        d[0] = 4; d[1] = 0x7C;   // would imply 1 entry by the formula
        // leave trailer bytes wrong (all zero)
        var lines = Walk(0x09, 0x40000000, d);
        Assert.Contains(lines, l => l.Contains("unknown["));
        Assert.DoesNotContain(lines, l => l.Contains("count ="));
    }

    [Fact]
    public void Type25_ref_list_resolves_names_when_a_resolver_is_supplied()
    {
        // §4l: [u32 count][7C] then count × [ref:3 BE][7C]; len = 5 + 4·count. One ref = 0x000005.
        var d = new byte[] { 0x01, 0, 0, 0, 0x7C, 0x00, 0x00, 0x05, 0x7C };

        // Without a resolver: raw hex only.
        var bare = ChangeFormPayload.Walk(0x25, 0x80000000, d).ToList();
        Assert.Contains(bare, l => l.Contains("ref[0] = 00 00 05"));
        Assert.DoesNotContain(bare, l => l.Contains("->"));

        // With a resolver: the 3-byte BE refID (5) resolves and is appended.
        var named = ChangeFormPayload.Walk(0x25, 0x80000000, d, r => r == 5 ? "Some Form" : null).ToList();
        Assert.Contains(named, l => l.Contains("ref[0] = 00 00 05 -> Some Form"));
    }

    [Fact]
    public void Type0D_single_ref_resolves_when_a_resolver_is_supplied()
    {
        // §4l: [ref:3 BE][7C] (len 4). 0x000010 -> refId 16.
        var d = new byte[] { 0x00, 0x00, 0x10, 0x7C };
        var named = ChangeFormPayload.Walk(0x0D, 0x00000000, d, r => r == 16 ? "0x00000014" : null).ToList();
        Assert.Contains(named, l => l.Contains("ref = 00 00 10 -> 0x00000014"));
        // An unresolvable ref leaves the raw hex untouched.
        var unresolved = ChangeFormPayload.Walk(0x0D, 0x00000000, d, _ => null).ToList();
        Assert.Contains(unresolved, l => l.Contains("ref = 00 00 10"));
        Assert.DoesNotContain(unresolved, l => l.Contains("->"));
    }

    [Fact]
    public void Unknown_type_is_a_single_gap()
    {
        var lines = Walk(0x55, 0x12345678, [1, 2, 3, 4]);
        Assert.Single(lines);
        Assert.Contains("unknown[4]", lines[0]);
    }
}
