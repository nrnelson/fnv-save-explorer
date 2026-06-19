using System.Buffers.Binary;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class PlayerSkillsTests
{
    [Fact]
    public void IndexForSkill_resolves_names_case_and_space_insensitively()
    {
        Assert.Equal((byte)0x24, PlayerSkills.IndexForSkill("Lockpick"));
        Assert.Equal((byte)0x22, PlayerSkills.IndexForSkill("energyweapons")); // spaces optional, case-insensitive
        Assert.Equal((byte)0x22, PlayerSkills.IndexForSkill("Energy Weapons"));
        Assert.Null(PlayerSkills.IndexForSkill("NotASkill"));
    }

    [Fact]
    public void Skills_property_filters_modifications_to_recognised_skills()
    {
        var mods = new List<ActorValueMod>
        {
            new(0x24, 50f, 100), // Lockpick
            new(0x10, 7f, 200),  // some non-skill actor value
            new(0x29, 33f, 300), // Guns
        };
        var skills = new PlayerSkills(100, mods);

        Assert.Equal(3, skills.Modifications.Count);
        Assert.Equal(["Guns", "Lockpick"], skills.Skills.Select(s => s.Name).OrderBy(n => n));
    }

    [Fact]
    public void Synthetic_save_locates_decodes_and_edits_skill_block()
    {
        var save = FalloutSave.Parse(SkillSave.Build());

        Assert.NotNull(save.Skills);
        var byName = save.Skills!.Skills.ToDictionary(s => s.Name, s => s.Value);
        Assert.Equal(20f, byName["Lockpick"]);
        Assert.Equal(11f, byName["Repair"]);
        Assert.Equal(5f, byName["Guns"]);
    }

    [Fact]
    public void Synthetic_skill_edit_is_same_length_and_reparses()
    {
        var original = SkillSave.Build();
        var save = FalloutSave.Parse(original);

        Assert.True(save.TrySetSkill("Lockpick", 100f));
        var edited = save.ToBytes();

        Assert.Equal(original.Length, edited.Length); // nothing shifted
        Assert.Equal(100f, FalloutSave.Parse(edited).Skills!.Skills.Single(s => s.Name == "Lockpick").Value);
    }

    [Fact]
    public void TrySetSkill_rejects_unknown_and_absent_skills()
    {
        var save = FalloutSave.Parse(SkillSave.Build());

        Assert.False(save.TrySetSkill("NotASkill", 1f)); // not a skill at all
        Assert.False(save.TrySetSkill("Science", 50f));  // a real skill, but not stored in this save
        Assert.False(save.HasPendingEdits);
    }

    [Fact]
    public void Skill_save_round_trips_byte_identical_with_no_edits()
    {
        var original = SkillSave.Build();
        Assert.Equal(original, FalloutSave.Parse(original).ToBytes());
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_decode_and_safely_edit_any_stored_skills(string path)
    {
        var save = FalloutSave.Load(path);
        if (save.Skills is not { } skills)
            return; // a save with fewer than two stored skill modifications — nothing to assert

        Assert.All(skills.Skills, s =>
        {
            Assert.InRange(s.ValueOffset, save.BodyOffset, save.FileLength - 4);
            Assert.False(float.IsNaN(s.Value));
        });

        // A same-length float edit of an existing skill must not shift the file and must re-parse.
        var first = skills.Skills[0];
        Assert.True(save.TrySetSkill(first.Name, 100f));
        var edited = save.ToBytes();
        Assert.Equal(save.FileLength, edited.Length);
        Assert.Equal(100f, FalloutSave.Parse(edited).Skills!.Skills.Single(s => s.Index == first.Index).Value);
    }
}

/// <summary>
/// Builds a minimal New Vegas <c>.fos</c> whose body carries a File Location Table and a change-forms
/// region containing a known actor-value (skill) block: a length prefix <c>[count*4][7C]</c> followed
/// by <c>count</c> × <c>[avIndex][7C][f32][7C]</c> entries. Lets the skill locator/editor be tested
/// deterministically without a real save.
/// </summary>
internal static class SkillSave
{
    public static byte[] Build()
    {
        var b = new List<byte>();
        void Str(string s) => b.AddRange(Encoding.Latin1.GetBytes(s));
        void Delim() => b.Add(0x7C);
        void U16(ushort v) { var t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, v); b.AddRange(t); }
        void U32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); b.AddRange(t); }
        void F32(float v) { var t = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(t, v); b.AddRange(t); }
        void StringField(string s) { U16((ushort)s.Length); Delim(); Str(s); Delim(); }

        Str("FO3SAVEGAME");
        var headerSizeAt = b.Count;
        U32(0);            // saveHeaderSize placeholder (patched below)
        U32(0x30);         // version
        Delim();
        Str("ENGLISH"); Delim();
        U32(4); Delim();   // width
        U32(2); Delim();   // height
        U32(7); Delim();   // save number
        StringField("Test");
        StringField("Title");
        U32(3); Delim();   // level
        StringField("Vault1");
        StringField("000.01.02");

        var screenshotStart = b.Count;
        for (var i = 0; i < 4 * 2 * 3; i++) b.Add((byte)i);

        b.Add(0x15);       // trailer
        U32(0);            // pluginStructSize
        b.Add(1); Delim(); // one plugin
        StringField("A.esm");

        // ---- Body: File Location Table (8 u32) then a change-forms region with a skill block ----
        var bodyStart = b.Count;
        var changeFormsOffset = bodyStart + 32;        // immediately after the 8-u32 FLT
        var globalData3Offset = changeFormsOffset + 40; // end bound for the locator scan

        U32(0);                          // [0] FormIdArrayCountOffset (0 -> empty array)
        U32(0);                          // [1] UnknownTable3Offset
        U32(0);                          // [2] GlobalData1Offset
        U32((uint)changeFormsOffset);    // [3] ChangeFormsOffset
        U32((uint)globalData3Offset);    // [4] GlobalData3Offset
        U32(12);                         // [5] GlobalData1Count
        U32(1);                          // [6] GlobalData3Count
        U32(3);                          // [7] ChangeFormCount

        // change-forms region (40 bytes): filler, then [7C][len][7C] + 3 skill entries, then trailing.
        b.Add(0x00); Delim();            // filler ending in the 7C that precedes the length byte
        b.Add(3 * 4);                    // length prefix = entryCount * 4
        Delim();
        b.Add(0x24); Delim(); F32(20f); Delim(); // Lockpick = 20
        b.Add(0x27); Delim(); F32(11f); Delim(); // Repair   = 11
        b.Add(0x29); Delim(); F32(5f); Delim();  // Guns     = 5
        while (b.Count < globalData3Offset) b.Add(0xFF); // pad to the end bound
        b.AddRange([0x00, 0x7C, 0x00, 0x00]);            // a little trailing body past the scan bound

        var data = b.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
        return data;
    }
}
