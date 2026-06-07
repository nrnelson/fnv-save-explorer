using System.Buffers.Binary;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class FalloutSaveTests
{
    [Fact]
    public void Parses_all_documented_header_fields()
    {
        var save = FalloutSave.Parse(SyntheticSave.Build());

        Assert.Equal(GameVariant.FalloutNewVegas, save.Variant);
        Assert.Equal("ENGLISH", save.Language);
        Assert.Equal(7u, save.SaveNumber);
        Assert.Equal("Test", save.PlayerName);
        Assert.Equal("Title", save.PlayerTitle);
        Assert.Equal(3u, save.PlayerLevel);
        Assert.Equal("Vault1", save.PlayerLocation);
        Assert.Equal("000.01.02", save.Playtime);
        Assert.Equal(4, save.Screenshot.Width);
        Assert.Equal(2, save.Screenshot.Height);
        Assert.Equal(["A.esm", "B.esp"], save.Plugins);
    }

    [Fact]
    public void Round_trips_byte_identical_with_no_edits()
    {
        var original = SyntheticSave.Build();
        var roundTripped = FalloutSave.Parse(original).ToBytes();
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void Same_length_numeric_edits_apply_without_shifting_the_file()
    {
        var original = SyntheticSave.Build();
        var save = FalloutSave.Parse(original);

        save.SetPlayerLevel(42);
        save.SetSaveNumber(99);
        var edited = save.ToBytes();

        Assert.Equal(original.Length, edited.Length); // nothing shifted
        var reparsed = FalloutSave.Parse(edited);
        Assert.Equal(42u, reparsed.PlayerLevel);
        Assert.Equal(99u, reparsed.SaveNumber);
        Assert.Equal("Test", reparsed.PlayerName); // untouched fields intact
    }

    [Fact]
    public void Rename_is_allowed_only_when_byte_length_matches()
    {
        var save = FalloutSave.Parse(SyntheticSave.Build());

        Assert.False(save.TrySetPlayerName("TooLongName")); // length-changing edit rejected
        Assert.True(save.TrySetPlayerName("Best"));         // same 4-byte length is fine

        var reparsed = FalloutSave.Parse(save.ToBytes());
        Assert.Equal("Best", reparsed.PlayerName);
    }

    [Fact]
    public void Rejects_files_without_the_FO3SAVEGAME_signature()
    {
        var garbage = Encoding.ASCII.GetBytes("NOTASAVEGAME000000000000");
        Assert.Throws<SaveFormatException>(() => FalloutSave.Parse(garbage));
    }

    [Theory]
    [MemberData(nameof(RealSaves))]
    public void Real_saves_round_trip_byte_identical(string path)
    {
        var original = File.ReadAllBytes(path);
        var roundTripped = FalloutSave.Parse(original).ToBytes();
        Assert.True(original.AsSpan().SequenceEqual(roundTripped), $"round-trip differed for {path}");
    }

    /// <summary>
    /// Discovers real <c>.fos</c> files from a local <c>samples/</c> folder and the default FNV saves
    /// directory. If none are present the theory simply yields nothing (xUnit reports it as skipped),
    /// so the suite stays green on machines without the game installed.
    /// </summary>
    public static IEnumerable<object[]> RealSaves()
    {
        var roots = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "FalloutNV", "Saves"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.fos"))
                yield return [file];
        }
    }
}

/// <summary>Builds a minimal but structurally valid New Vegas <c>.fos</c> in memory for portable tests.</summary>
internal static class SyntheticSave
{
    public static byte[] Build()
    {
        var b = new List<byte>();
        void Str(string s) => b.AddRange(Encoding.Latin1.GetBytes(s));
        void Delim() => b.Add(0x7C);
        void U16(ushort v) { var t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, v); b.AddRange(t); }
        void U32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); b.AddRange(t); }
        void StringField(string s) { U16((ushort)s.Length); Delim(); Str(s); Delim(); }

        Str("FO3SAVEGAME");
        var headerSizeAt = b.Count;
        U32(0);            // saveHeaderSize placeholder (patched below)
        U32(0x30);         // version
        Delim();
        Str("ENGLISH");    // language block
        Delim();
        U32(4); Delim();   // screenshot width
        U32(2); Delim();   // screenshot height
        U32(7); Delim();   // save number
        StringField("Test");
        StringField("Title");
        U32(3); Delim();   // level
        StringField("Vault1");
        StringField("000.01.02");

        var screenshotStart = b.Count;
        for (var i = 0; i < 4 * 2 * 3; i++) b.Add((byte)i); // screenshot pixels

        b.Add(0x15);       // trailer byte
        U32(0);            // pluginStructSize (not used for navigation)
        b.Add(2);          // plugin count
        Delim();
        StringField("A.esm");
        StringField("B.esp");

        for (var i = 0; i < 32; i++) b.Add((byte)(0xF0 + (i & 0x0F))); // arbitrary "body"

        var data = b.ToArray();
        // saveHeaderSize measures from just after its own field (offset 15) to the screenshot data.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
        return data;
    }
}
