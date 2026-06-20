using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class PluginDatabaseTests
{
    [Fact]
    public void TesPlugin_reads_masters_and_named_item_forms()
    {
        var bytes = EsmBuilder.Plugin(
            masters: ["FalloutNV.esm"],
            new TestRecord("WEAP", 0x01000ABC, Edid: "WeapTestRifle", Full: "Test Rifle"),
            new TestRecord("MISC", 0x01000DEF, Edid: "MiscJunk01", Full: null)); // EDID-only → name falls back to EDID

        var plugin = TesPlugin.Read(new MemoryStream(bytes), "Child.esm");

        Assert.Equal(["FalloutNV.esm"], plugin.Masters);
        var byId = plugin.Forms.ToDictionary(f => f.LocalFormId, f => f.Name);
        Assert.Equal("Test Rifle", byId[0x01000ABC]);
        Assert.Equal("MiscJunk01", byId[0x01000DEF]);
    }

    [Fact]
    public void TesPlugin_skips_non_item_groups_and_decodes_compressed_records()
    {
        var bytes = EsmBuilder.Plugin(
            masters: [],
            new TestRecord("CELL", 0x00000111, Edid: "SomeCell", Full: "A Place"),   // not an item type — must be ignored
            new TestRecord("WEAP", 0x00000222, Edid: "ZipGun", Full: "Zipped Gun", Compressed: true));

        var plugin = TesPlugin.Read(new MemoryStream(bytes), "Base.esm");

        Assert.DoesNotContain(plugin.Forms, f => f.LocalFormId == 0x00000111); // CELL skipped
        Assert.Equal("Zipped Gun", plugin.Forms.Single(f => f.LocalFormId == 0x00000222).Name);
    }

    [Fact]
    public void Build_resolves_base_game_form_in_save_space()
    {
        using var dir = new TempDataFolder();
        dir.Write("FalloutNV.esm", EsmBuilder.Plugin([], new TestRecord("ALCH", 0x00015169, Edid: "Stimpak", Full: "Stimpak")));

        var db = PluginDatabase.Build(["FalloutNV.esm"], dir.Path);

        Assert.Equal("Stimpak", db.Resolve(0x00015169));
        Assert.Equal(["FalloutNV.esm"], db.ResolvedPlugins);
    }

    [Fact]
    public void Build_remaps_dlc_self_index_into_save_load_order_space()
    {
        using var dir = new TempDataFolder();
        dir.Write("FalloutNV.esm", EsmBuilder.Plugin([], new TestRecord("WEAP", 0x00000ABC, Edid: "BaseRifle", Full: "Base Rifle")));
        // Child declares two masters, so its own forms use local high byte 0x02 ("self" = masterCount).
        // In this save's load order Child is index 1, so 0x02xxxxxx must remap to 0x01xxxxxx.
        dir.Write("Child.esm", EsmBuilder.Plugin(
            masters: ["FalloutNV.esm", "Extra.esm"],
            new TestRecord("ALCH", 0x02000077, Edid: "ChildStim", Full: "Child Stim"),     // self form
            new TestRecord("WEAP", 0x00000ABC, Edid: "BaseRifle", Full: "Overridden Rifle"))); // overrides the base form

        var db = PluginDatabase.Build(["FalloutNV.esm", "Child.esm"], dir.Path); // Extra.esm absent — fine, no form uses it

        Assert.Equal("Child Stim", db.Resolve(0x01000077));   // local 0x02 → save 0x01 (divergent)
        Assert.Null(db.Resolve(0x02000077));                  // the un-remapped id must NOT resolve
        Assert.Equal("Overridden Rifle", db.Resolve(0x00000ABC)); // later plugin in load order wins
    }

    [Fact]
    public void Resolve_handles_runtime_and_unknown_forms()
    {
        Assert.Equal("(created)", PluginDatabase.Empty.Resolve(0xFF001234));
        Assert.Null(PluginDatabase.Empty.Resolve(0x00012345));
    }

    [Theory]
    [InlineData("FalloutNV.esm", "Fallout: New Vegas")]      // official table
    [InlineData("GunRunnersArsenal.esm", "Gun Runners' Arsenal")]
    [InlineData("MercenaryPack.esm", "Mercenary Pack")]
    [InlineData("falloutnv.ESM", "Fallout: New Vegas")]      // case-insensitive
    [InlineData("WeaponModsExpanded.esp", "Weapon Mods Expanded")] // PascalCase fallback
    [InlineData("NVInteriors.esm", "NV Interiors")]          // acronym boundary
    [InlineData("FOOK.esm", "FOOK")]                          // all-caps stays whole
    public void PluginNames_maps_official_and_prettifies_others(string file, string expected)
        => Assert.Equal(expected, PluginNames.Friendly(file));

    /// <summary>
    /// When the game is installed, the real <c>FalloutNV.esm</c> must parse and yield item names (e.g. Stimpak).
    /// Skips quietly on machines without the game so the suite stays green.
    /// </summary>
    [Fact]
    public void Real_master_resolves_known_item_names()
    {
        var folder = GameDataLocator.FindDataFolder();
        if (folder is null)
            return; // game not installed here

        var db = PluginDatabase.Build(["FalloutNV.esm"], folder);

        Assert.True(db.Count > 0);
        Assert.Contains(db.Names.Values, n => n.Contains("Stimpak", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>A throwaway directory for writing synthetic <c>.esm</c> files, cleaned up on dispose.</summary>
internal sealed class TempDataFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "fnv-esm-tests-" + Guid.NewGuid().ToString("N"));

    public TempDataFolder() => Directory.CreateDirectory(Path);

    public void Write(string fileName, byte[] bytes) =>
        File.WriteAllBytes(System.IO.Path.Combine(Path, fileName), bytes);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>One record to embed in a synthetic plugin (see <see cref="EsmBuilder"/>).</summary>
internal sealed record TestRecord(string Type, uint FormId, string? Edid, string? Full, bool Compressed = false);

/// <summary>
/// Builds a minimal but structurally valid FO3/FNV TES4 plugin in memory: a <c>TES4</c> header (with an
/// ordered master list) followed by one top-level <c>GRUP</c> per record type, each holding the records of
/// that type. FO3/FNV headers are 24 bytes; a group's size includes its header, a record's excludes it.
/// </summary>
internal static class EsmBuilder
{
    public static byte[] Plugin(IEnumerable<string> masters, params TestRecord[] records)
    {
        var b = new List<byte>();
        b.AddRange(Header(masters));
        foreach (var group in records.GroupBy(r => r.Type))
            b.AddRange(Group(group.Key, group.Select(Record)));
        return [.. b];
    }

    private static byte[] Header(IEnumerable<string> masters)
    {
        var fields = new List<byte>();
        var hedr = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(hedr.AsSpan(0, 4), 1.34f); // version; rest = counts (ignored)
        fields.AddRange(Sub("HEDR", hedr));
        fields.AddRange(Sub("CNAM", ZStr("tests")));
        foreach (var m in masters)
        {
            fields.AddRange(Sub("MAST", ZStr(m)));
            fields.AddRange(Sub("DATA", new byte[8])); // u64 master file size (ignored)
        }
        return RecordHeader("TES4", flags: 0, formId: 0, [.. fields]);
    }

    private static byte[] Record(TestRecord rec)
    {
        var fields = new List<byte>();
        if (rec.Edid is not null) fields.AddRange(Sub("EDID", ZStr(rec.Edid)));
        if (rec.Full is not null) fields.AddRange(Sub("FULL", ZStr(rec.Full)));
        var data = (byte[])[.. fields];
        uint flags = 0;
        if (rec.Compressed)
        {
            var compressed = Deflate(data);
            var framed = new byte[4 + compressed.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(framed, (uint)data.Length);
            Array.Copy(compressed, 0, framed, 4, compressed.Length);
            data = framed;
            flags |= 0x00040000;
        }
        return RecordHeader(rec.Type, flags, rec.FormId, data);
    }

    private static byte[] RecordHeader(string type, uint flags, uint formId, byte[] data)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes(type));
        b.AddRange(U32((uint)data.Length)); // DataSize excludes the 24-byte header
        b.AddRange(U32(flags));
        b.AddRange(U32(formId));
        b.AddRange(new byte[8]); // version-control / form-version
        b.AddRange(data);
        return [.. b];
    }

    private static byte[] Group(string label, IEnumerable<byte[]> records)
    {
        var content = new List<byte>();
        foreach (var r in records) content.AddRange(r);
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("GRUP"));
        b.AddRange(U32((uint)(24 + content.Count))); // GroupSize includes the 24-byte header
        b.AddRange(Encoding.ASCII.GetBytes(label));   // top-level label = record type
        b.AddRange(U32(0));                            // groupType 0 = top-level
        b.AddRange(new byte[8]);                       // stamp + version + unknown
        b.AddRange(content);
        return [.. b];
    }

    private static byte[] Sub(string type, byte[] data)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes(type));
        var sz = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(sz, (ushort)data.Length);
        b.AddRange(sz);
        b.AddRange(data);
        return [.. b];
    }

    private static byte[] ZStr(string s)
    {
        var d = Encoding.Latin1.GetBytes(s);
        var r = new byte[d.Length + 1]; // null-terminated
        Array.Copy(d, r, d.Length);
        return r;
    }

    private static byte[] U32(uint v)
    {
        var t = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(t, v);
        return t;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}
