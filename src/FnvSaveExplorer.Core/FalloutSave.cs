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

    /// <summary>Decodes the GlobalData type-2 ("TES") <b>state-changed reference registry</b> (ROADMAP §6 #16):
    /// <c>[vsval count][7C]</c> then <c>count × ([refID:3][7C][u16 status][7C])</c> then a fixed tail. Each entry is
    /// a reference whose state changed at runtime, resolved to its save-space FormID (via <see cref="ResolveRefId"/>)
    /// with its raw status code. Controlled-diff pinned on the Ghost Town Gunfight pair: killing the 6 Powder
    /// Gangers added exactly 6 entries, each <c>status 1</c> — so <c>status 1</c> is the death/kill code (other
    /// codes 2–7 are state changes whose meaning is not yet pinned — "label, don't guess"). Returns an empty list
    /// when there is no type-2 record (or it's empty).</summary>
    public IReadOnlyList<(uint FormId, int RefId, int Status)> StateChangedRefs()
    {
        var g = GlobalDataTable1.FirstOrDefault(x => x.Type == 2);
        return g is null ? [] : DecodeStateChangedRefs(g.Data, ResolveRefId);
    }

    /// <summary>Decodes a GlobalData type-2 payload into its <c>(FormId, RefId, Status)</c> registry entries, given
    /// a <paramref name="resolve"/> mapping a raw 3-byte refID to a save-space FormID. Pure/testable — separated
    /// from <see cref="StateChangedRefs"/> so it can be exercised with a synthetic payload (ROADMAP §6 #16).</summary>
    public static IReadOnlyList<(uint FormId, int RefId, int Status)> DecodeStateChangedRefs(
        byte[] data, Func<int, uint> resolve)
    {
        var count = ReferenceChangeForm.ReadVsval(data, 0, out var vlen);
        if (count <= 0)
            return [];
        var p = vlen;
        var result = new List<(uint, int, int)>((int)Math.Min(count, 4096));
        for (long i = 0; i < count; i++)
        {
            if (p < data.Length && data[p] == 0x7C) p++;        // delimiter after the previous field
            if (p + 3 > data.Length) break;
            var refId = (data[p] << 16) | (data[p + 1] << 8) | data[p + 2];
            p += 3;
            if (p < data.Length && data[p] == 0x7C) p++;        // delimiter between refID and status
            if (p + 2 > data.Length) break;
            var status = data[p] | (data[p + 1] << 8);
            p += 2;
            result.Add((resolve(refId), refId, status));
        }
        return result;
    }

    /// <summary>The references the type-2 registry records as <b>dead</b> (status 1) — the kill signal a counter/
    /// event-gated quest's completion is re-derived from (ROADMAP §6 #16 Stage 2).</summary>
    public IReadOnlySet<uint> DeadReferences() =>
        StateChangedRefs().Where(r => r.Status == 1 && r.FormId != 0).Select(r => r.FormId).ToHashSet();

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
    /// Resolves a 3-byte change-form <b>refID</b> to its FormID, honouring its 2-bit RefID type
    /// (<see cref="ReferenceChangeForm.RefIdType"/>, ROADMAP §6 #15). FNV uses only two types, both
    /// corpus-confirmed: <c>0</c> = a FormID-array index (the change-form header convention — a 0-based index,
    /// so straight <see cref="ResolveIref"/>) and <c>2</c> = a <b>created</b> form (plugin index <c>0xFF</c>,
    /// i.e. <c>0xFF000000 | value</c>). Types 1 (base-master) and 3 (unspecified) <b>never occur in FNV</b>
    /// (confirmed across all corpora), so they're left as <c>0</c> (unknown) rather than resolved on an
    /// unverified Skyrim-spec guess — surfacing such a refID as unknown is honest and would flag the surprise.
    /// Previously type-2 (created) headers indexed out of bounds and also resolved to 0.
    /// </summary>
    public uint ResolveRefId(int raw24) => ReferenceChangeForm.RefIdType(raw24) switch
    {
        0 => ResolveIref(ReferenceChangeForm.RefIdValue(raw24)),        // FormID-array index
        2 => 0xFF000000u | (uint)ReferenceChangeForm.RefIdValue(raw24), // created (plugin index 0xFF)
        _ => 0u, // types 1/3 are unused by FNV — leave unknown rather than guess
    };

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
            // FormId honours the refID's 2-bit type (§6 #15): type-0 = array index, type-2 = a created (0xFF)
            // form. (Iref keeps the raw 3-byte value; for type-2 records it isn't an array index.)
            yield return new ChangeFormHeader(at, iref, ResolveRefId(iref), flags, type, version, dataOffset, length);
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
    private const int InventoryResyncWindow = 512; // bounded forward resync — a safety guard for a stack carrying a
                                                   // genuinely-unknown future extra-data property type. As of the
                                                   // 0x0D decode (ROADMAP §4i) EVERY observed per-stack type is sized
                                                   // (fixed or structured), so the walk is exact and this guard is
                                                   // unused across all 607 saves; it replaced the old 2048-byte scan
                                                   // window, so there is no run-merging or distinct-ref scoring.

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
    /// Locates and walks the inventory list in a reference change form. The list <em>start</em> is structural:
    /// <see cref="ReferenceChangeForm.InventorySearchStart"/> skips the gated 27-byte MOVE block <em>and</em> the
    /// fixed 1160-byte havok/float array (both invariant across every real save), then
    /// <see cref="ReferenceChangeForm.TryInventoryItemsStart"/> sizes the reference's ExtraDataList and reads the
    /// inventory's <c>vsval</c> stack count to land on the first item with <em>no scan</em>. The walk decodes
    /// exactly that many stacks and accepts only when the count matches — a self-validating anchor (ROADMAP §4i).
    /// <para>If the ExtraDataList isn't the recognised shape (an atypical/modded record), it falls back to the
    /// prior behaviour: a forward scan from the ExtraDataList for the first contiguous chain with at least
    /// <see cref="InventoryMinEntries"/> <em>distinct</em> references, then from the record start. Each stack is
    /// the fixed 9-byte <c>[ref:3][7C][count:u32][7C]</c> entry plus a per-stack extra-data block whose exact
    /// length is computed (see <see cref="ReferenceChangeForm.TryReadStackExtra"/>), so <see cref="BuildChain"/>
    /// advances precisely. Returns null when the record carries no inventory (CHANGE_REFR_INVENTORY clear) or no
    /// list is found.</para>
    /// </summary>
    private PlayerInventory? WalkInventory(ChangeFormHeader cf)
    {
        // Gate: a reference with no CHANGE_REFR_INVENTORY flag holds no item list.
        if ((cf.ChangeFlags & ReferenceChangeForm.ChangeRefrInventory) == 0)
            return null;
        var end = cf.DataOffset + cf.DataLength;
        var start = ReferenceChangeForm.InventorySearchStart(
            _raw.AsSpan(cf.DataOffset, cf.DataLength), cf.DataOffset, cf.ChangeFlags);

        // Deterministic path: size the ExtraDataList + read the vsval stack count to land on the first item with
        // no scan, then decode the chain from there. The vsval is the engine's authoritative item-stack count and
        // validates the start: a correctly-located list yields at least that many stacks (it matches exactly on
        // 28/30 real saves; on the other two the decoder over-reads a couple of interspersed non-item stacks the
        // §4g/name-resolution filter hides — so we keep the full chain rather than truncate, which would drop real
        // trailing items). A mis-sized ExtraDataList yields fewer than the count and falls through to the scan.
        if (ReferenceChangeForm.TryInventoryItemsStart(_raw.AsSpan(0, end), start, out var itemsOffset, out var stackCount)
            && stackCount > 0)
        {
            var chain = BuildChain(itemsOffset, end, out _);
            if (chain.Count >= stackCount)
                return new PlayerInventory(cf.DataOffset, chain) { DeterministicStart = true };
        }

        // The direct structural skip mis-landed: either an atypical/modded ExtraDataList, or a bit2/bit10
        // CHANGE_REFR_HAVOK_MOVE record whose pre-list havok physics blob is variable-length and can't be byte-sized
        // (ROADMAP §4i gap 1). Locate the list with two complementary locators — the deterministic
        // ExtraDataList-header anchor and the §4g forward scan — and prefer the anchor (vsval-validated, and it
        // drops the leading over-read stacks the §4g scan picks up, so it is the *cleaner* decode of the same real
        // list). Defer to §4g only when the anchor found a short coincidental chain in the havok blob / zeroed slot
        // array while §4g found the genuine list: those differ by a huge factor (real endgame lists are 180–214
        // stacks, garbage chains ≤ ~12), whereas the anchor-vs-scan difference on the same real list is just the few
        // leading over-reads — so a 2× gap cleanly distinguishes "anchor missed the list" from "anchor is cleaner".
        var anchored = ScanForExtraDataListAnchor(cf, start, end);
        var scanned = ScanForInventory(cf, start, end)
            ?? (start != cf.DataOffset ? ScanForInventory(cf, cf.DataOffset, end) : null);
        if (anchored is null)
            return scanned;
        if (scanned is not null && scanned.Items.Count > 2 * anchored.Items.Count)
            return scanned; // the anchor only found a short garbage chain; §4g has the real (much longer) list
        return anchored;
    }

    /// <summary>Scans <c>[start, end)</c> for the reference ExtraDataList header
    /// (<see cref="ReferenceChangeForm.IsExtraDataListHeader"/>) whose typed-entry walk
    /// (<see cref="ReferenceChangeForm.TryInventoryItemsStart"/>) lands on a <c>vsval</c> followed by at least
    /// that many real stacks — the deterministic anchor for records whose pre-list havok region isn't byte-sized
    /// (§4i gap 1). Among all self-validating headers it picks the one with the <b>largest vsval stack count</b>:
    /// that is the engine's genuine item count, whereas a coincidental header in the havok blob / zeroed slot
    /// array yields a small/random count (and an absurd count is already excluded — no chain is that long). Ties
    /// break to the tightest chain (smallest over-read) then the earliest header. Returns the decoded inventory
    /// (flagged deterministic) or null if no header self-validates.</summary>
    private PlayerInventory? ScanForExtraDataListAnchor(ChangeFormHeader cf, int start, int end)
    {
        var span = _raw.AsSpan(0, end);
        List<InventoryItem>? best = null;
        var bestStack = -1;
        var bestOver = int.MaxValue;
        for (var h = start; h + ReferenceChangeForm.RefExtraHeaderLength <= end; h++)
        {
            if (!ReferenceChangeForm.IsExtraDataListHeader(span, h))
                continue;
            if (!ReferenceChangeForm.TryInventoryItemsStart(span, h, out var itemsOffset, out var stackCount) || stackCount <= 0)
                continue;
            var chain = BuildChain(itemsOffset, end, out _);
            if (chain.Count < stackCount)
                continue;
            var over = chain.Count - stackCount;
            if (stackCount > bestStack || (stackCount == bestStack && over < bestOver))
            {
                best = chain;
                bestStack = stackCount;
                bestOver = over;
            }
        }
        return best is null ? null : new PlayerInventory(cf.DataOffset, best) { DeterministicStart = true };
    }

    /// <summary>Forward-scans <c>[start, end)</c> for the first contiguous stack chain with at least
    /// <see cref="InventoryMinEntries"/> distinct references — the §4g/§9 distinct-ref acceptance test that
    /// distinguishes the genuine item list from a coincidental chain. Returns null if none qualifies.</summary>
    private PlayerInventory? ScanForInventory(ChangeFormHeader cf, int start, int end)
    {
        for (var p = start; p + 9 <= end;)
        {
            if (!TryReadInventoryEntry(p, out _, out _, out _))
            {
                p++;
                continue;
            }
            var chain = BuildChain(p, end, out var chainEnd);
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
                UnknownExtraType = extra.UnknownType,
                UnknownExtraOffset = extra.UnknownOffset,
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
    /// per-stack extra-data block — and outputs the decoded <paramref name="extra"/>. Every observed
    /// property type is now sized (fixed lengths + the structured <c>0x0D</c>, ROADMAP §4i), so the block
    /// decodes fully and we advance by its exact byte length. The bounded forward resync to the next
    /// structurally valid stack (within <see cref="InventoryResyncWindow"/> bytes) is kept only as a safety
    /// guard for a genuinely-unknown future type (any condition/equip decoded before it is still returned).
    /// Returns -1 if it can't advance.
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
        // Genuinely-unknown future type (none across the 607-save corpus): bounded forward resync to the next valid stack.
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

    // ---- Player karma + XP (floats in the player reference's actor-value array, §4j) ---------------

    private ChangeFormHeader? _playerStatRecord;
    private bool _playerStatRecordLocated;

    /// <summary>The player reference change form (iref = PlayerRef iref + 1) — the record that carries the
    /// inventory (§4g) and, in its post-MOVE actor-value array, the player's karma and XP (§4j). Null when
    /// the PlayerRef iref isn't found or the +1 record isn't reached.</summary>
    private ChangeFormHeader? PlayerStatRecord
    {
        get
        {
            if (!_playerStatRecordLocated)
            {
                _playerStatRecordLocated = true;
                var iref = FindIref(0x14);
                if (iref >= 0)
                    foreach (var cf in EnumerateChangeForms())
                        if (cf.Iref == iref + 1) { _playerStatRecord = cf; break; }
            }
            return _playerStatRecord;
        }
    }

    /// <summary>Absolute file offset of the float32 player-stat slot, or null if the record/slot can't be
    /// located safely (see <see cref="ReferenceChangeForm.PlayerStatSlotOffset"/>).</summary>
    private int? PlayerStatOffset(int slot)
    {
        if (PlayerStatRecord is not { } cf)
            return null;
        var off = ReferenceChangeForm.PlayerStatSlotOffset(
            _raw.AsSpan(cf.DataOffset, cf.DataLength), cf.DataOffset, cf.ChangeFlags, slot);
        return off < 0 ? null : off;
    }

    private float? ReadStatSingle(int slot) =>
        PlayerStatOffset(slot) is { } off ? BinaryPrimitives.ReadSingleLittleEndian(_raw.AsSpan(off, 4)) : null;

    private bool TrySetStat(int slot, float value)
    {
        if (PlayerStatOffset(slot) is not { } off)
            return false;
        StageSingle(off, value);
        return true;
    }

    /// <summary>The player's karma — a float32 in the player reference's actor-value array (§4j), or null
    /// when that record/slot wasn't located (e.g. a bit2/bit10 havok-physics player record). Editing it is
    /// a safe same-length float splice via <see cref="TrySetKarma"/>.</summary>
    public float? Karma => ReadStatSingle(ReferenceChangeForm.PlayerKarmaSlot);

    /// <summary>The player's experience points — a float32 just after karma in the same array (§4j), or
    /// null when not locatable. Editing it is a safe same-length float splice via <see cref="TrySetXp"/>.</summary>
    public float? Xp => ReadStatSingle(ReferenceChangeForm.PlayerXpSlot);

    /// <summary>Stages a same-length edit to the player's karma (a fixed-width float). Returns false if the
    /// player reference record / karma slot wasn't located.</summary>
    public bool TrySetKarma(float karma) => TrySetStat(ReferenceChangeForm.PlayerKarmaSlot, karma);

    /// <summary>Stages a same-length edit to the player's XP (a fixed-width float). Returns false if the
    /// player reference record / XP slot wasn't located.</summary>
    public bool TrySetXp(float xp) => TrySetStat(ReferenceChangeForm.PlayerXpSlot, xp);

    /// <summary>The base FormID of bottle caps — the FNV currency. Caps are not a standalone save field;
    /// the engine stores them as an ordinary inventory stack of this form, so they decode and edit through
    /// the inventory path (confirmed: a real save's inventory carries an <c>0x0000000F</c> "Bottle Cap"
    /// stack whose count is the player's caps). ROADMAP §6.4.</summary>
    public const uint CapsFormId = 0x0000000F;

    /// <summary>The player's caps (currency): the stack count of <see cref="CapsFormId"/> in the inventory,
    /// or null if the inventory wasn't located or carries no caps stack.</summary>
    public uint? Caps => Inventory?.Items.FirstOrDefault(i => i.FormId == CapsFormId)?.Count;

    /// <summary>Stages a same-length edit to the player's caps — a thin wrapper over
    /// <see cref="TrySetItemCount"/> for the caps stack (caps are an inventory stack, §6.4). Returns false
    /// if the inventory wasn't located or no caps stack is present.</summary>
    public bool TrySetCaps(uint caps) => TrySetItemCount(CapsFormId, caps);

    // ---- Player read notes (Pip-Boy Data -> Notes "viewed" markers, §4k) -------------------
    private PlayerNotes? _readNotes;

    /// <summary>The form-type tag (low six bits of the change-form type byte) of a note "read" marker;
    /// its high two bits are 0, so the marker's length field is a single byte (always 0 here). Corpus-verified
    /// (ROADMAP §4k.1 #2, <c>notescan</c>): <b>every</b> type-0x1F change form across 45,783 markers (vanilla +
    /// base VNV + VNV Extended) resolves via the −1 index to a <c>NOTE</c> record — 0 non-NOTE, so this filter
    /// neither misses nor over-matches.</summary>
    private const byte NoteReadMarkerType = 0x1F;

    /// <summary>The change-flags value the engine writes on a note's inventory reference when the note is
    /// read/viewed in the Pip-Boy. The marker carries no payload — its mere presence is the read state.
    /// Corpus-verified (ROADMAP §4k.1 #1, <c>notescan</c>): the flag is <b>always exactly</b> this value across
    /// all 45,783 markers — a single distinct value, never combined with other change bits.</summary>
    private const uint NoteReadMarkerFlags = 0x80000000;

    /// <summary>
    /// The player's <b>read notes</b> (Pip-Boy <i>Data → Notes</i>, shown non-bold once viewed), decoded from
    /// the per-note read markers — change forms with <c>type 0x1F</c>, <c>changeFlags 0x80000000</c> and a
    /// zero-length payload (ROADMAP §4k). A marker's <c>refID</c> is the note's inventory reference (FormID-array
    /// index + 1, as for item stacks §4g), so the note's own FormID is <c>FormIdArray[refID - 1]</c> — a
    /// <c>NOTE</c> record, resolved to a name separately via <see cref="PluginDatabase"/>. Never null (empty when
    /// the player has read no notes); read-only, since a marker is a whole change form (toggling it is
    /// length-changing — §6.7).
    /// </summary>
    public PlayerNotes ReadNotes => _readNotes ??= LocateReadNotes();

    private PlayerNotes LocateReadNotes()
    {
        var notes = new List<NoteEntry>();
        foreach (var cf in EnumerateChangeForms())
        {
            if (cf.FormType != NoteReadMarkerType || cf.ChangeFlags != NoteReadMarkerFlags || cf.DataLength != 0)
                continue;
            // The marker's iref is the note's inventory reference: FormID-array index + 1 (the §4g convention),
            // so the note itself is the form one slot earlier. iref 0 has no predecessor and can't be a note.
            if (cf.Iref <= 0)
                continue;
            var formId = ResolveIref(cf.Iref - 1);
            if (formId == 0)
                continue;
            notes.Add(new NoteEntry(cf.Iref, cf.FormId, formId));
        }
        return new PlayerNotes(notes);
    }

    /// <summary>The player inventory change form (iref = PlayerRef + 1) — the reference record that holds the
    /// item stacks (§4g) <em>and</em> the note ref-list (§4k.1 #4). Null if it can't be located.</summary>
    private ChangeFormHeader? PlayerInventoryChangeForm
    {
        get
        {
            var iref = FindIref(0x14);
            if (iref < 0)
                return null;
            foreach (var cf in EnumerateChangeForms())
                if (cf.Iref == iref + 1)
                    return cf;
            return null;
        }
    }

    /// <summary>
    /// The player's <b>full Pip-Boy notes list</b> (read <em>and</em> unread), as opposed to <see cref="ReadNotes"/>
    /// which is only the viewed markers. A note the player <i>has</i> appears as a <c>7C</c>-delimited 3-byte refID
    /// inside the player inventory change form (iref = PlayerRef + 1) whose <c>FormIdArray[ref − 1]</c> resolves to a
    /// <c>NOTE</c> record (ROADMAP §4k.1 #4 — located by the Saves 38→39→40 controlled triple). Because deciding
    /// "is this FormID a NOTE record?" needs the game masters, which live outside <c>Core</c>, the caller injects that
    /// test via <paramref name="isNoteForm"/> (e.g. <c>fid =&gt; db.RecordType(fid) == "NOTE"</c>) — exactly how
    /// inventory name resolution is layered. Each note's <see cref="PipBoyNote.Read"/> is set from the read markers
    /// (a marker's refID equals the note's ref-list entry, both = note base index + 1). Read markers whose note is no
    /// longer referenced in the record are still included (read state persists). Deduplicated by FormID; empty when
    /// the inventory record can't be found. Read-only.
    /// </summary>
    public IReadOnlyList<PipBoyNote> PipBoyNotes(Func<uint, bool> isNoteForm)
    {
        var acquired = ScanNoteRefs(isNoteForm);
        var byFormId = new Dictionary<uint, PipBoyNote>();
        foreach (var n in acquired)
            byFormId[n.FormId] = n;
        // Read markers are the authoritative read set; include any whose note isn't referenced in the record anymore.
        foreach (var rn in ReadNotes.Notes)
            if (!byFormId.ContainsKey(rn.FormId))
                byFormId[rn.FormId] = new PipBoyNote(rn.MarkerIref, rn.FormId, Read: true);
        return byFormId.Values.ToList();
    }

    private IReadOnlyList<PipBoyNote> ScanNoteRefs(Func<uint, bool> isNoteForm)
    {
        if (PlayerInventoryChangeForm is not { } cf)
            return [];
        // A note's ref-list entry refID equals its read marker's refID (both = note base FormID-array index + 1),
        // so the read flag is a set-membership test against the markers' irefs.
        var readRefs = ReadNotes.Notes.Select(n => n.MarkerIref).ToHashSet();
        var seen = new HashSet<uint>();
        var result = new List<PipBoyNote>();
        var end = cf.DataOffset + cf.DataLength;
        for (var i = cf.DataOffset; i + 4 < end; i++)
        {
            if (_raw[i] != 0x7C || _raw[i + 4] != 0x7C)
                continue;
            var refId = (_raw[i + 1] << 16) | (_raw[i + 2] << 8) | _raw[i + 3];
            if (refId <= 0)
                continue;
            var formId = ResolveIref(refId - 1);
            if (formId == 0 || !isNoteForm(formId) || !seen.Add(formId))
                continue;
            result.Add(new PipBoyNote(refId, formId, readRefs.Contains(refId)));
        }
        return result;
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
