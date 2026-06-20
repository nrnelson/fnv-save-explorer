using System.Buffers.Binary;
using System.Text;

namespace FnvSaveExplorer.Core;

/// <summary>
/// A parsed Fallout: New Vegas <c>.fos</c> save.
///
/// <para><b>Design — "retention model":</b> the entire original byte array is kept verbatim. Parsing
/// only decodes the well-understood header / screenshot / plugin region and records the byte offset
/// of each editable field; everything from the File Location Table onward (globals, change forms,
/// the FormID array — the parts that were never fully publicly decoded) is preserved untouched.
/// Saving an unedited file therefore reproduces it byte-for-byte, and edits are applied as in-place
/// splices. This makes round-tripping provably safe even though the body is only partially understood.</para>
///
/// <para><b>Editing rule:</b> the File Location Table stores <i>absolute</i> file offsets into the body,
/// so only <i>same-length</i> edits are safe today — they shift nothing. Length-changing edits (e.g.
/// renaming to a different length) would require rewriting every absolute offset and are rejected for now.</para>
/// </summary>
public sealed class FalloutSave
{
    public const string Signature = "FO3SAVEGAME";

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private const byte Delimiter = 0x7C; // '|'

    private readonly byte[] _raw;
    private readonly Dictionary<int, byte[]> _edits = [];

    private FalloutSave(byte[] raw) => _raw = raw;

    // ---- Header / metadata -------------------------------------------------
    public GameVariant Variant { get; private set; } = GameVariant.FalloutNewVegas;
    public uint SaveHeaderSize { get; private set; }
    public uint Version { get; private set; }
    public string Language { get; private set; } = "";
    public uint SaveNumber { get; private set; }
    public string PlayerName { get; private set; } = "";
    public string PlayerTitle { get; private set; } = "";
    public uint PlayerLevel { get; private set; }
    public string PlayerLocation { get; private set; } = "";
    public string Playtime { get; private set; } = "";
    public SaveScreenshot Screenshot { get; private set; } = null!;
    public IReadOnlyList<string> Plugins { get; private set; } = [];

    /// <summary>The marker byte that follows the screenshot (documented as 0x15, but varies in practice).</summary>
    public byte ScreenshotTrailerByte { get; private set; }
    public uint PluginStructSize { get; private set; }

    // ---- Offsets used for editing / R&D ------------------------------------
    private const int SaveHeaderSizeOffset = 11; // == Signature.Length
    private int _saveNumberOffset;
    private int _playerNameValueOffset;
    private int _playerNameByteLength;
    private int _playerLevelOffset;
    private int _screenshotDataOffset;

    /// <summary>Offset where the File Location Table / undecoded body begins (after the plugin list).</summary>
    public int BodyOffset { get; private set; }

    public int FileLength => _raw.Length;
    public bool HasPendingEdits => _edits.Count > 0;

    /// <summary>The undecoded body (globals, change forms, FormID array), preserved verbatim.</summary>
    public ReadOnlyMemory<byte> Body => _raw.AsMemory(BodyOffset);

    // ---- Loading -----------------------------------------------------------
    public static FalloutSave Load(string path) => Parse(File.ReadAllBytes(path));

    public static FalloutSave Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var save = new FalloutSave(bytes);
        save.ParseHeader();
        return save;
    }

    private void ParseHeader()
    {
        var r = new ByteReader(_raw);

        var sig = Latin1.GetString(r.ReadArray(Signature.Length));
        if (sig != Signature)
            throw new SaveFormatException($"not a Fallout save: signature was '{sig}', expected '{Signature}'", 0);

        SaveHeaderSize = r.ReadUInt32();
        Version = r.ReadUInt32();
        r.Expect(Delimiter, "after version");

        // New Vegas inserts a null-padded language block here; Fallout 3 does not.
        var language = r.ReadUntil(Delimiter);
        Language = Latin1.GetString(language).TrimEnd('\0');
        Variant = GameVariant.FalloutNewVegas;

        var width = ReadDelimited32(r);
        var height = ReadDelimited32(r);
        _saveNumberOffset = r.Position;
        SaveNumber = ReadDelimited32(r);

        PlayerName = ReadStringField(r, "player name", out _playerNameValueOffset, out _playerNameByteLength);
        PlayerTitle = ReadStringField(r, "player title", out _, out _);
        _playerLevelOffset = r.Position;
        PlayerLevel = ReadDelimited32(r);
        PlayerLocation = ReadStringField(r, "player location", out _, out _);
        Playtime = ReadStringField(r, "playtime", out _, out _);

        // Cross-check: the header size field must point exactly at the screenshot data.
        var expectedScreenshotStart = SaveHeaderSizeOffset + 4 + (int)SaveHeaderSize;
        if (r.Position != expectedScreenshotStart)
            throw new SaveFormatException(
                $"header parse ended at 0x{r.Position:X} but saveHeaderSize implies screenshot at 0x{expectedScreenshotStart:X}",
                r.Position);

        if (width is 0 or > 10000 || height is 0 or > 10000)
            throw new SaveFormatException($"implausible screenshot dimensions {width}x{height}", r.Position);

        _screenshotDataOffset = r.Position;
        var shotLen = (long)width * height * 3;
        if (shotLen > r.Remaining)
            throw new SaveFormatException($"screenshot ({shotLen} bytes) exceeds file", r.Position);
        Screenshot = new SaveScreenshot((int)width, (int)height, r.ReadArray((int)shotLen));

        ScreenshotTrailerByte = r.ReadByte();
        PluginStructSize = r.ReadUInt32();
        int pluginCount = r.ReadByte();
        r.Expect(Delimiter, "after plugin count");

        var plugins = new List<string>(pluginCount);
        for (var i = 0; i < pluginCount; i++)
        {
            int nameLen = r.ReadUInt16();
            r.Expect(Delimiter, $"plugin[{i}] length/name separator");
            plugins.Add(Latin1.GetString(r.ReadArray(nameLen)));
            r.Expect(Delimiter, $"plugin[{i}] terminator");
        }
        Plugins = plugins;
        BodyOffset = r.Position;
    }

    private static uint ReadDelimited32(ByteReader r)
    {
        var value = r.ReadUInt32();
        r.Expect(Delimiter, "field terminator");
        return value;
    }

    private static string ReadStringField(ByteReader r, string what, out int valueOffset, out int byteLength)
    {
        int len = r.ReadUInt16();
        r.Expect(Delimiter, $"{what} length/value separator");
        valueOffset = r.Position;
        byteLength = len;
        var bytes = r.ReadArray(len);
        r.Expect(Delimiter, $"{what} terminator");
        return Latin1.GetString(bytes);
    }

    // ---- R&D helpers -------------------------------------------------------
    /// <summary>
    /// Reads <paramref name="count"/> little-endian uint32s from the start of the body. These are the
    /// raw File Location Table values (absolute offsets + counts into the undecoded sections) — exposed
    /// for reverse-engineering, not yet semantically labelled.
    /// </summary>
    public uint[] PeekBodyUInt32(int count)
    {
        var result = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var at = BodyOffset + i * 4;
            result[i] = at + 4 <= _raw.Length
                ? BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(at, 4))
                : 0u;
        }
        return result;
    }

    /// <summary>Reads up to <paramref name="count"/> raw bytes at an absolute file offset (clamped to EOF).</summary>
    public byte[] ReadAt(int offset, int count)
    {
        if (offset < 0 || offset >= _raw.Length)
            return [];
        count = Math.Min(count, _raw.Length - offset);
        return _raw.AsSpan(offset, count).ToArray();
    }

    /// <summary>Reads a little-endian uint32 at an absolute file offset.</summary>
    public uint ReadUInt32At(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_raw.AsSpan(offset, 4));

    /// <summary>Reads a little-endian uint16 at an absolute file offset.</summary>
    public ushort ReadUInt16At(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_raw.AsSpan(offset, 2));

    /// <summary>True when <paramref name="value"/> is a plausible in-file offset into the body.</summary>
    public bool IsBodyOffset(uint value) => value >= (uint)BodyOffset && value < (uint)_raw.Length;

    // ---- Body decoding (File Location Table / globals / stats) -------------
    private FileLocationTable? _flt;
    private IReadOnlyList<GlobalData>? _globalDataTable1;
    private MiscStatsBlock? _miscStats;
    private bool _miscStatsParsed;

    /// <summary>The File Location Table at the start of the body (offsets + counts into the sections).</summary>
    public FileLocationTable Flt => _flt ??= new FileLocationTable(PeekBodyUInt32(8));

    /// <summary>Global data table 1 records (types 0–11): Misc Stats, Player Location, Global Variables, etc.</summary>
    public IReadOnlyList<GlobalData> GlobalDataTable1 =>
        _globalDataTable1 ??= ParseGlobalDataTable(Flt.GlobalData1Offset, Flt.GlobalData1Count);

    /// <summary>The decoded Misc Stats counters (global data type 0), or null if absent.</summary>
    public MiscStatsBlock? MiscStats
    {
        get
        {
            if (!_miscStatsParsed)
            {
                _miscStatsParsed = true;
                var record = GlobalDataTable1.FirstOrDefault(g => g.Type == 0);
                if (record is not null)
                    _miscStats = MiscStatsBlock.Parse(record.Data, record.DataOffset);
            }
            return _miscStats;
        }
    }

    private List<GlobalData> ParseGlobalDataTable(uint offset, uint count)
    {
        var list = new List<GlobalData>((int)Math.Min(count, 1024));
        var at = (int)offset;
        for (var i = 0; i < count; i++)
        {
            if (at + 8 > _raw.Length)
                break;
            var type = ReadUInt32At(at);
            var length = ReadUInt32At(at + 4);
            if (at + 8L + length > _raw.Length)
                break;
            list.Add(new GlobalData { Type = type, Data = ReadAt(at + 8, (int)length), Offset = at });
            at += 8 + (int)length;
        }
        return list;
    }

    /// <summary>Stages a same-length edit to a Misc Stat value (safe — shifts nothing). Returns false if out of range.</summary>
    public bool TrySetMiscStat(int index, uint value)
    {
        var stats = MiscStats;
        if (stats is null || index < 0 || index >= stats.Stats.Count)
            return false;
        StageUInt32(stats.Stats[index].ValueOffset, value);
        return true;
    }

    // ---- FormID array + change-form location ------------------------------
    // Change forms reference forms by "iref" — an index into the FormID array. Resolving irefs is the
    // bedrock for all change-form work; finding a known form's iref then locating its 3-byte big-endian
    // refID in the change-forms region is how we pinpoint a specific change form (e.g. the player's).

    private uint[]? _formIdArray;

    /// <summary>The save's FormID array: index (iref) → full 32-bit FormID.</summary>
    public IReadOnlyList<uint> FormIdArray => _formIdArray ??= ParseFormIdArray();

    private uint[] ParseFormIdArray()
    {
        var at = (int)Flt.FormIdArrayCountOffset;
        if (at <= 0 || at + 4 > _raw.Length)
            return [];
        var count = ReadUInt32At(at);
        if (count > 2_000_000 || at + 4L + count * 4 > _raw.Length)
            return [];
        var array = new uint[count];
        var p = at + 4;
        for (var i = 0; i < count; i++, p += 4)
            array[i] = ReadUInt32At(p);
        return array;
    }

    /// <summary>Returns the iref (FormID array index) of <paramref name="formId"/>, or -1 if not present.</summary>
    public int FindIref(uint formId)
    {
        var array = FormIdArray;
        for (var i = 0; i < array.Count; i++)
            if (array[i] == formId)
                return i;
        return -1;
    }

    /// <summary>Resolves an iref to its FormID (0 if out of range).</summary>
    public uint ResolveIref(int iref) =>
        iref >= 0 && iref < FormIdArray.Count ? FormIdArray[iref] : 0u;

    /// <summary>
    /// The plugin (ESM/ESP) that a FormID's <paramref name="modIndex"/> (high byte) refers to — its entry in
    /// the save's load order (<see cref="Plugins"/>). Returns null for a runtime-created <c>0xFF</c> index or
    /// any value past the load order. The mod index is just the FormID's top byte, so this is the canonical
    /// "which mod is this item from" lookup (no ESM read needed).
    /// </summary>
    public string? PluginForModIndex(int modIndex) =>
        modIndex >= 0 && modIndex < Plugins.Count ? Plugins[modIndex] : null;

    /// <summary>
    /// The human-friendly source for a FormID's <paramref name="modIndex"/>: the owning plugin's display
    /// name (see <see cref="PluginNames"/>), <c>"(created)"</c> for a runtime <c>0xFF</c> index, or null if
    /// the index is past the load order.
    /// </summary>
    public string? FriendlySourceForModIndex(int modIndex) =>
        modIndex == 0xFF ? "(created)"
        : PluginForModIndex(modIndex) is { } plugin ? PluginNames.Friendly(plugin)
        : null;

    /// <summary>
    /// Finds offsets in the change-forms region where <paramref name="iref"/> appears as a 3-byte
    /// big-endian refID — i.e. candidate change-form record starts for that form. (A raw byte scan;
    /// for distinctive irefs like the player's it returns the single real record.)
    /// </summary>
    public IReadOnlyList<int> FindRefIdInChangeForms(int iref)
    {
        var hits = new List<int>();
        if (iref < 0)
            return hits;
        byte b0 = (byte)(iref >> 16), b1 = (byte)(iref >> 8), b2 = (byte)iref;
        var start = (int)Flt.ChangeFormsOffset;
        var end = Math.Min((int)Flt.GlobalData3Offset, _raw.Length - 3);
        for (var i = start; i < end; i++)
            if (_raw[i] == b0 && _raw[i + 1] == b1 && _raw[i + 2] == b2)
                hits.Add(i);
        return hits;
    }

    // ---- Change-form record walker ----------------------------------------
    // Each change form is [refID:3 BE][changeFlags:u32 LE][type:u8][version:u8][length][data]. The top
    // two bits of the type byte select the length field's width (0 -> u8, 1 -> u16, 2/3 -> u32); the low
    // six bits are the form type. Verified by walking from ChangeFormsOffset and landing exactly on
    // GlobalData3Offset after ChangeFormCount records.

    /// <summary>One change-form record header plus the absolute span of its data payload.</summary>
    public readonly record struct ChangeFormHeader(
        int Offset, int Iref, uint FormId, uint ChangeFlags, byte TypeByte, byte Version, int DataOffset, int DataLength)
    {
        /// <summary>The form type (low six bits of the type byte).</summary>
        public int FormType => TypeByte & 0x3F;

        /// <summary>Absolute offset of the next record (end of this record's data).</summary>
        public int Next => DataOffset + DataLength;
    }

    /// <summary>
    /// Walks the change-forms region record-by-record using the per-record header. Stops at
    /// GlobalData3Offset or on the first malformed/over-running record (so a wrong assumption can't read
    /// out of bounds). The number of records yielded should equal <see cref="FileLocationTable.ChangeFormCount"/>.
    /// </summary>
    public IEnumerable<ChangeFormHeader> EnumerateChangeForms()
    {
        var at = (int)Flt.ChangeFormsOffset;
        var end = Math.Min((int)Flt.GlobalData3Offset, _raw.Length);
        while (at + 9 <= end)
        {
            var iref = (_raw[at] << 16) | (_raw[at + 1] << 8) | _raw[at + 2];
            var flags = ReadUInt32At(at + 3);
            var type = _raw[at + 7];
            var version = _raw[at + 8];
            var lenWidth = (type >> 6) switch { 0 => 1, 1 => 2, _ => 4 };
            if (at + 9 + lenWidth > end)
                yield break;
            int length = lenWidth switch
            {
                1 => _raw[at + 9],
                2 => ReadUInt16At(at + 9),
                _ => (int)ReadUInt32At(at + 9),
            };
            var dataOffset = at + 9 + lenWidth;
            if (length < 0 || dataOffset + length > end)
                yield break;
            yield return new ChangeFormHeader(at, iref, ResolveIref(iref), flags, type, version, dataOffset, length);
            at = dataOffset + length;
        }
    }

    /// <summary>
    /// The PlayerRef (ACHR, FormID 0x14) change-form record — the one that holds the player's
    /// inventory and actor-value modifications — located by walking the change forms for its iref.
    /// Null if the iref isn't in the FormID array or the walk doesn't reach it.
    /// </summary>
    public ChangeFormHeader? PlayerRefChangeForm
    {
        get
        {
            var iref = FindIref(0x14);
            if (iref < 0)
                return null;
            foreach (var cf in EnumerateChangeForms())
                if (cf.Iref == iref)
                    return cf;
            return null;
        }
    }

    /// <summary>A located player change form and where its 3-byte big-endian refID appears in the
    /// change-forms region (candidate record starts — a raw byte scan, so it may include false positives).</summary>
    public readonly record struct PlayerAnchor(string Label, uint FormId, int Iref, IReadOnlyList<int> RecordStarts);

    /// <summary>
    /// Locates the player's change forms — the base actor (TESNPC_, FormID 0x07) and the PlayerRef
    /// (ACHR, FormID 0x14) — by resolving each FormID to its iref and scanning for its 3-byte refID.
    /// Useful as stable anchors when reporting controlled-diff hits (e.g. "playerBase+0x3C").
    /// </summary>
    public IReadOnlyList<PlayerAnchor> PlayerAnchors()
    {
        var anchors = new List<PlayerAnchor>(2);
        foreach (var (formId, label) in new (uint FormId, string Label)[] { (0x07u, "playerBase"), (0x14u, "playerRef") })
        {
            var iref = FindIref(formId);
            if (iref >= 0)
                anchors.Add(new PlayerAnchor(label, formId, iref, FindRefIdInChangeForms(iref)));
        }
        return anchors;
    }

    /// <summary>
    /// The player base (TESNPC_) change-form record start, disambiguated from any false-positive
    /// refID hits as the candidate immediately preceding the validated (name-anchored) SPECIAL block.
    /// Null if SPECIAL or the player base iref wasn't found. This is the stable anchor the skills
    /// locator and diff-relative reporting build on.
    /// </summary>
    public int? PlayerBaseRecordStart
    {
        get
        {
            if (Special is not { } sp)
                return null;
            var iref = FindIref(0x07);
            if (iref < 0)
                return null;
            var best = -1;
            foreach (var hit in FindRefIdInChangeForms(iref))
                if (hit <= sp.Offset && hit > best)
                    best = hit;
            return best >= 0 ? best : null;
        }
    }

    // ---- Player SPECIAL ---------------------------------------------------
    private PlayerSpecial? _special;
    private bool _specialLocated;

    /// <summary>The player's SPECIAL attributes (located in the player base change form), or null if not found.</summary>
    public PlayerSpecial? Special
    {
        get
        {
            if (!_specialLocated)
            {
                _specialLocated = true;
                _special = LocateSpecial();
            }
            return _special;
        }
    }

    /// <summary>
    /// Locates SPECIAL by finding the player-name field (<c>[u16 len][7C][name][7C]</c>) inside the
    /// change-forms region — the 7 SPECIAL bytes sit just before it, fenced by 0x7C delimiters. The
    /// search is scoped to the change forms so it skips the identical name field in the file header.
    /// </summary>
    private PlayerSpecial? LocateSpecial()
    {
        var name = Latin1.GetBytes(PlayerName);
        if (name.Length == 0)
            return null;

        // pattern: [lenLo][lenHi][7C] name... [7C]
        var pattern = new byte[3 + name.Length + 1];
        pattern[0] = (byte)(name.Length & 0xFF);
        pattern[1] = (byte)((name.Length >> 8) & 0xFF);
        pattern[2] = Delimiter;
        Array.Copy(name, 0, pattern, 3, name.Length);
        pattern[^1] = Delimiter;

        var start = (int)Flt.ChangeFormsOffset;
        var end = Math.Min((int)Flt.GlobalData3Offset, _raw.Length) - pattern.Length;
        for (var i = start; i <= end; i++)
        {
            if (!MatchAt(i, pattern))
                continue;
            var specialOffset = i - 8; // 7 SPECIAL bytes + the 0x7C just before the name length
            if (specialOffset < 1)
                continue;
            // Fenced by delimiters on both sides, and each value plausible (1..15).
            if (_raw[i - 1] != Delimiter || _raw[specialOffset - 1] != Delimiter)
                continue;
            var values = _raw.AsSpan(specialOffset, 7);
            if (!AllInRange(values, 1, 15))
                continue;
            return new PlayerSpecial(specialOffset, values);
        }
        return null;
    }

    private bool MatchAt(int offset, byte[] pattern)
    {
        for (var k = 0; k < pattern.Length; k++)
            if (_raw[offset + k] != pattern[k])
                return false;
        return true;
    }

    private static bool AllInRange(ReadOnlySpan<byte> values, byte lo, byte hi)
    {
        foreach (var v in values)
            if (v < lo || v > hi)
                return false;
        return true;
    }

    /// <summary>Stages a same-length edit to the 7 SPECIAL bytes (safe). Returns false if SPECIAL wasn't located or input isn't 7 values.</summary>
    public bool TrySetSpecial(ReadOnlySpan<byte> values)
    {
        if (Special is null || values.Length != 7)
            return false;
        _edits[Special.Offset] = values.ToArray();
        return true;
    }

    // ---- Player skills (actor-value modification block in the ACHR change form) ----------
    private PlayerSkills? _skills;
    private bool _skillsLocated;

    /// <summary>
    /// The player's stored skill modifications, or null if fewer than two recognised skills are
    /// stored (the engine only persists deviations from a skill's computed base — see <see cref="PlayerSkills"/>).
    /// </summary>
    public PlayerSkills? Skills
    {
        get
        {
            if (!_skillsLocated)
            {
                _skillsLocated = true;
                _skills = LocateSkills();
            }
            return _skills;
        }
    }

    /// <summary>
    /// Locates the actor-value modification block within the change-forms region. Entries are
    /// <c>[avIndex:u8][7C][f32][7C]</c> (7 bytes), preceded by a length prefix <c>[count*4:u8][7C]</c>.
    /// Because the lone <c>0x7C</c> delimiter also occurs inside binary data, single-entry blocks are
    /// indistinguishable from noise; we anchor on the length prefix and choose the validating block
    /// that contains the most recognised <i>skills</i>, requiring at least two to accept it.
    /// </summary>
    private PlayerSkills? LocateSkills()
    {
        var start = Math.Max(3, (int)Flt.ChangeFormsOffset);
        var end = Math.Min((int)Flt.GlobalData3Offset, _raw.Length) - 7;
        var bestSkillCount = 0;
        PlayerSkills? best = null;

        for (var s = start; s <= end; s++)
        {
            // Length prefix immediately before the first entry: [7C][len][7C], len = entryCount * 4.
            if (_raw[s - 1] != Delimiter || _raw[s - 3] != Delimiter)
                continue;
            int len = _raw[s - 2];
            if (len == 0 || len % 4 != 0)
                continue;
            var count = len / 4;
            if (count > 63)
                continue;

            var entries = new List<ActorValueMod>(count);
            var p = s;
            var ok = true;
            for (var k = 0; k < count; k++)
            {
                if (p + 7 > _raw.Length || _raw[p + 1] != Delimiter || _raw[p + 6] != Delimiter || _raw[p] > 0x4F)
                {
                    ok = false;
                    break;
                }
                var value = BinaryPrimitives.ReadSingleLittleEndian(_raw.AsSpan(p + 2, 4));
                if (float.IsNaN(value) || value < -1000f || value > 1000f)
                {
                    ok = false;
                    break;
                }
                entries.Add(new ActorValueMod(_raw[p], value, p + 2));
                p += 7;
            }
            if (!ok)
                continue;

            var skillCount = entries.Count(e => PlayerSkills.SkillNames.ContainsKey(e.Index));
            if (skillCount > bestSkillCount)
            {
                bestSkillCount = skillCount;
                best = new PlayerSkills(s, entries);
            }
        }

        return bestSkillCount >= 2 ? best : null;
    }

    /// <summary>
    /// Stages a same-length edit to a stored skill's float value (safe — shifts nothing). Returns false
    /// if skills weren't located or that skill has no stored entry (adding one would change the length).
    /// </summary>
    public bool TrySetSkill(string skillName, float value)
    {
        var index = PlayerSkills.IndexForSkill(skillName);
        if (index is null || Skills is not { } skills)
            return false;
        var entry = skills.Skills.FirstOrDefault(s => s.Index == index);
        if (entry is null)
            return false;
        StageSingle(entry.ValueOffset, value);
        return true;
    }

    // ---- Player inventory (item list in the player's inventory change form) ----------
    private PlayerInventory? _inventory;
    private bool _inventoryLocated;

    /// <summary>
    /// The player's inventory (carried item stacks), or null if it can't be located. Decoded from a
    /// dedicated reference change form, not the PlayerRef record — see <see cref="PlayerInventory"/>.
    /// </summary>
    public PlayerInventory? Inventory
    {
        get
        {
            if (!_inventoryLocated)
            {
                _inventoryLocated = true;
                _inventory = LocateInventory();
            }
            return _inventory;
        }
    }

    private const int InventoryMinEntries = 3;     // enough to distinguish a real item list from coincidence
    private const int InventoryResyncWindow = 512; // bounded forward resync used ONLY when a stack carries an
                                                   // extra-data property type we can't yet size (rare, modded
                                                   // weapons). Replaces the old 2048-byte scan window — the walk
                                                   // is otherwise exact (each stack's length comes from its decoded
                                                   // extra data), so there is no run-merging or distinct-ref scoring.

    /// <summary>
    /// Locates the player's inventory change form and decodes its item stacks. The player's inventory
    /// lives in the reference change form whose iref is (PlayerRef iref) + 1 — verified across saves; the
    /// player ACHR (0x14) record itself holds actor state, not items. Returns null if that record can't be
    /// found or doesn't parse as a recognisable item list (handled gracefully, like SPECIAL/skills).
    /// </summary>
    private PlayerInventory? LocateInventory()
    {
        var playerRefIref = FindIref(0x14);
        if (playerRefIref < 0)
            return null;
        foreach (var cf in EnumerateChangeForms())
        {
            if (cf.Iref != playerRefIref + 1)
                continue;
            var inv = WalkInventory(cf);
            return inv is { } v && v.Items.Count >= InventoryMinEntries ? v : null;
        }
        return null;
    }

    /// <summary>
    /// Locates and walks the inventory list in a reference change form. The list <em>start</em> is
    /// anchored by changeFlags, not by ranking every run in the record: skip the gated 27-byte MOVE block
    /// (<see cref="ReferenceChangeForm.InventorySearchStart"/>), then take the <em>first</em> contiguous
    /// stack chain with at least <see cref="InventoryMinEntries"/> <em>distinct</em> references. The genuine
    /// list references distinct items, whereas the variable gated sections between MOVE and the items (the
    /// zeroed havok arrays + the reference's own ExtraDataList) yield only short coincidental chains — their
    /// pseudo-refs mostly carry a <c>0x7C</c> inside the would-be count and reject, and the few that pass
    /// repeat a ref or read a count-0 phantom (e.g. a <c>2.0</c> float's bytes read as ref 63), so they never
    /// reach the distinct-ref bar. Each stack is the fixed 9-byte <c>[ref:3][7C][count:u32][7C]</c> entry
    /// followed by a per-stack extra-data block whose exact byte length is computed from its decoded
    /// properties (condition / equipped / weapon mods; see
    /// <see cref="ReferenceChangeForm.TryReadStackExtra"/>), so <see cref="BuildChain"/> advances to the next
    /// stack precisely. Returns null when the record carries no inventory (CHANGE_REFR_INVENTORY clear) or no
    /// qualifying chain is found.
    /// </summary>
    private PlayerInventory? WalkInventory(ChangeFormHeader cf)
    {
        // Gate: a reference with no CHANGE_REFR_INVENTORY flag holds no item list.
        if ((cf.ChangeFlags & ReferenceChangeForm.ChangeRefrInventory) == 0)
            return null;
        var end = cf.DataOffset + cf.DataLength;
        var start = ReferenceChangeForm.InventorySearchStart(
            _raw.AsSpan(cf.DataOffset, cf.DataLength), cf.DataOffset, cf.ChangeFlags);
        for (var p = start; p + 9 <= end;)
        {
            if (!TryReadInventoryEntry(p, out _, out _, out _))
            {
                p++;
                continue;
            }
            var chain = BuildChain(p, end, out var chainEnd);
            // The item list's stacks reference distinct items; a coincidental chain in the gated havok/float
            // arrays repeats a ref or two (e.g. a float byte run reads as ref 63 + count-0 phantoms). So the
            // list is the first chain with >= InventoryMinEntries *distinct* references — the §4g/§9 discriminator
            // applied as a forward-scan acceptance test (no global ranking of every run in the record).
            if (chain.Select(i => i.Iref).Distinct().Count() >= InventoryMinEntries)
                return new PlayerInventory(cf.DataOffset, chain);
            p = chainEnd > p ? chainEnd : p + 1; // not the list — skip past this coincidental chain and keep scanning
        }
        return null;
    }

    /// <summary>Walks one contiguous chain of inventory stacks from <paramref name="start"/>, decoding each
    /// stack's extra data. Outputs <paramref name="chainEnd"/> (the offset where the chain stopped).</summary>
    private List<InventoryItem> BuildChain(int start, int end, out int chainEnd)
    {
        var items = new List<InventoryItem>();
        var p = start;
        while (p + 9 <= end && TryReadInventoryEntry(p, out var iref, out var formId, out var count))
        {
            var after = AdvancePastStack(p, end, out var extra);
            items.Add(new InventoryItem(iref, formId, count, p + 4)
            {
                Condition = extra.Condition,
                ConditionValueOffset = extra.ConditionOffset,
                Equipped = extra.Equipped,
                ExtraRefIds = extra.ModRefIds,
            });
            if (after <= p)
            {
                p = after <= p ? p + 9 : after; // chain ends; still account for this stack's bytes
                break;
            }
            p = after;
        }
        chainEnd = p;
        return items;
    }

    /// <summary>
    /// Returns the offset just past the stack at <paramref name="p"/> — its fixed 9-byte entry plus the
    /// per-stack extra-data block — and outputs the decoded <paramref name="extra"/>. When the extra data
    /// uses a property type we can't yet size (structured/mod-added), it resyncs to the next structurally
    /// valid stack within <see cref="InventoryResyncWindow"/> bytes (any condition/equip we did decode is
    /// still returned). Returns -1 if it can't advance.
    /// </summary>
    private int AdvancePastStack(int p, int end, out ReferenceChangeForm.StackExtra extra)
    {
        extra = ReferenceChangeForm.StackExtra.None;
        var exStart = p + 9;
        if (exStart > end)
            return -1;
        var data = _raw.AsSpan(0, end); // index == absolute file offset
        if (ReferenceChangeForm.TryReadStackExtra(data, exStart, 0, out var ex))
        {
            extra = ex;
            if (ex.FullyDecoded)
                return exStart + ex.ByteLength;
        }
        // Unknown/structured extra-data type: fall back to a bounded forward resync to the next valid stack.
        for (var r = exStart + 1; r <= Math.Min(end - 9, exStart + InventoryResyncWindow); r++)
            if (TryReadInventoryEntry(r, out _, out _, out _))
                return r;
        return -1;
    }

    /// <summary>
    /// Tries to read an inventory entry <c>[ref:3 BE][7C][count:u32 LE][7C]</c> at <paramref name="p"/>.
    /// The 3-byte reference is the FormID-array index <b>plus one</b> (index 0 is reserved), so the item
    /// is <c>FormIdArray[ref - 1]</c> and the count is the entry's own u32 — both confirmed by a controlled
    /// in-game diff (adding/consuming one Antivenom moved exactly this u32: 1→2→1). Requires both
    /// delimiters, a non-zero reference that resolves to a real FormID, and a sane stack count.
    /// </summary>
    private bool TryReadInventoryEntry(int p, out int iref, out uint formId, out uint count)
    {
        iref = 0; formId = 0; count = 0;
        if (p + 9 > _raw.Length)
            return false;
        if (_raw[p + 3] != Delimiter || _raw[p + 8] != Delimiter)
            return false;
        var refPlusOne = (_raw[p] << 16) | (_raw[p + 1] << 8) | _raw[p + 2];
        if (refPlusOne == 0)
            return false;
        iref = refPlusOne - 1; // the inventory reference is (FormID-array index + 1)
        formId = ResolveIref(iref);
        if (formId == 0)
            return false;
        count = ReadUInt32At(p + 4);
        // A real stack count is a small integer: its upper three bytes are never the 0x7C delimiter.
        // Rejecting a delimiter there discards misaligned reads of a stack's extra data (where a 0x7C
        // field separator falls inside the misread count), which is the main source of false entries.
        if (((count >> 8) & 0xFF) == Delimiter || ((count >> 16) & 0xFF) == Delimiter || ((count >> 24) & 0xFF) == Delimiter)
            return false;
        return count <= 1_000_000;
    }

    /// <summary>
    /// Stages a same-length edit to a stored item's stack count (safe — the count is a fixed-width u32).
    /// Returns false if the inventory wasn't located or no stack of <paramref name="formId"/> is present.
    /// </summary>
    public bool TrySetItemCount(uint formId, uint count)
    {
        if (Inventory is not { } inv)
            return false;
        var item = inv.Items.FirstOrDefault(i => i.FormId == formId);
        if (item is null)
            return false;
        StageUInt32(item.CountValueOffset, count);
        return true;
    }

    /// <summary>
    /// Stages a same-length edit to a stored item's condition/health (the <c>0x25</c> extra-data float —
    /// e.g. repair-to-full). Safe: the condition is a fixed-width little-endian float. Returns false if the
    /// inventory wasn't located, no stack of <paramref name="formId"/> is present, or that stack carries no
    /// condition extra-data (ammo/aid/misc, or an item the engine never tracked condition for).
    /// </summary>
    public bool TrySetItemCondition(uint formId, float condition)
    {
        if (Inventory is not { } inv)
            return false;
        var item = inv.Items.FirstOrDefault(i => i.FormId == formId && i.ConditionValueOffset is not null);
        if (item?.ConditionValueOffset is not { } offset)
            return false;
        StageSingle(offset, condition);
        return true;
    }

    // ---- Editing (same-length splices only) --------------------------------
    public void SetPlayerLevel(uint level) => StageUInt32(_playerLevelOffset, level);

    public void SetSaveNumber(uint number) => StageUInt32(_saveNumberOffset, number);

    /// <summary>
    /// Renames the player only if the new name encodes to the same byte length as the current one
    /// (a same-length splice that shifts nothing). Returns false otherwise — a different length would
    /// require rewriting the File Location Table's absolute offsets, which is not yet supported.
    /// </summary>
    public bool TrySetPlayerName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var bytes = Latin1.GetBytes(name);
        if (bytes.Length != _playerNameByteLength)
            return false;
        _edits[_playerNameValueOffset] = bytes;
        PlayerName = name;
        return true;
    }

    private void StageUInt32(int offset, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _edits[offset] = bytes;
    }

    private void StageSingle(int offset, float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        _edits[offset] = bytes;
    }

    public void ClearPendingEdits() => _edits.Clear();

    /// <summary>Serializes the save, applying any staged same-length edits. With no edits this returns
    /// an exact copy of the original bytes.</summary>
    public byte[] ToBytes()
    {
        var output = (byte[])_raw.Clone();
        foreach (var (offset, bytes) in _edits)
            Array.Copy(bytes, 0, output, offset, bytes.Length);
        return output;
    }

    /// <summary>Writes the save to <paramref name="path"/>. Pass <paramref name="backup"/> true to copy
    /// an existing target to a timestamped <c>.bak</c> first.</summary>
    public void Save(string path, bool backup = true)
    {
        if (backup && File.Exists(path))
            File.Copy(path, $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak", overwrite: false);
        File.WriteAllBytes(path, ToBytes());
    }
}
