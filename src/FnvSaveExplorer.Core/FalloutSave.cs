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

    /// <summary>Change-form record version byte — 0x1B on New Vegas (SPEC §4f).</summary>
    private const byte ChangeFormVersionNv = 0x1B;
    /// <summary>changeFlags on a type-0x2B reputation record — 0x00000002 on every real record observed (§4o).</summary>
    private const uint ReputationChangeFlags = 0x00000002;

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
    /// a <paramref name="resolve"/> mapping a raw 3-byte refID to a save-space FormID. <c>Status</c> is a u16
    /// <b>bitfield</b> of reference change-categories (bit 0 = dead; see <see cref="IsDeadStatus"/>). Pure/testable
    /// — separated from <see cref="StateChangedRefs"/> so it can be exercised with a synthetic payload.</summary>
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

    /// <summary>The decoded GlobalData <b>type-3 Global Variables</b> (ROADMAP §4c): each <c>GLOB</c> reference and
    /// its current float value, with the value's absolute file offset (the edit point). Empty when there is no
    /// type-3 record. Resolve a <see cref="GlobalVariable.FormId"/> to a name (e.g. <c>GameDaysPassed</c>) via the
    /// masters; edit a value with <see cref="TrySetGlobalVariable(uint,float)"/>.</summary>
    public IReadOnlyList<GlobalVariable> GlobalVariables()
    {
        var g = GlobalDataTable1.FirstOrDefault(x => x.Type == 3);
        return g is null ? [] : GlobalDataDecoder.DecodeGlobalVariables(g.Data, g.DataOffset, ResolveRefId, out _);
    }

    /// <summary>Stages a same-length edit to the value of the global variable whose <c>GLOB</c> resolves to
    /// <paramref name="formId"/> (safe — a 4-byte float splice shifts nothing). Returns false if no such global is
    /// present in the type-3 table.</summary>
    public bool TrySetGlobalVariable(uint formId, float value)
    {
        var v = GlobalVariables().FirstOrDefault(x => x.FormId == formId);
        if (v.ValueOffset == 0)
            return false;
        StageSingle(v.ValueOffset, value);
        return true;
    }

    /// <summary>The type-2 registry <c>Status</c> is a <b>bitfield</b> of reference change-categories, not an enum
    /// (ROADMAP §6 #2, controlled diffs 2026-06-28): observed values 1–7/9/11 are combinations of bits 0–3.
    /// <b>Bit 0 (<c>0x1</c>) = dead/killed</b> — proven by fresh kills entering the registry at status 1
    /// (gtg/fih: absent→1) and by killing an already-tracked ref bumping it by exactly +1 (the <c>killloot</c>
    /// pair: a live mantis at status 2 → 3 on death; looting it added nothing). Bits 1–3 are other change
    /// categories, not yet individually identified.</summary>
    public static bool IsDeadStatus(int status) => (status & 0x1) != 0;

    /// <summary>The references the type-2 registry records as <b>dead</b> — the kill signal a counter/event-gated
    /// quest's completion is re-derived from (ROADMAP §6 #16 Stage 2). Tests <see cref="IsDeadStatus"/> (the dead
    /// bit), so a ref that died <i>and</i> carries another change bit (e.g. status 3) still counts — the
    /// <c>== 1</c> check used to miss those (the mantis-at-3 case, ROADMAP §6 #2).</summary>
    public IReadOnlySet<uint> DeadReferences() =>
        StateChangedRefs().Where(r => IsDeadStatus(r.Status) && r.FormId != 0).Select(r => r.FormId).ToHashSet();

    /// <summary>References the save records as <b>enabled</b> (ROADMAP §6 #16 Stage 2): a reference that was
    /// <i>Initially Disabled</i> in the masters and then <c>Enable</c>d carries a <c>CHANGE_FORM_FLAGS</c> (bit0)
    /// change form whose new flags clear the <c>0x800</c> "Initially Disabled" bit, or a bare enable marker
    /// (FormType 0x08, flags 0x80000000, len 0). A quest's completion script enables specific refs (e.g. HELIOS
    /// One's power FX), so a completion-enabled ref appearing here is proof that completion ran — the signal for
    /// activator/world-state-completed quests (That Lucky Old Sun). The caller intersects this with the specific
    /// refs a quest's completion enables (so the broad <c>0x800</c>-clear set can't over-fire).</summary>
    public IReadOnlySet<uint> EnabledReferences()
    {
        var result = new HashSet<uint>();
        foreach (var cf in EnumerateChangeForms())
        {
            if (cf.FormId == 0)
                continue;
            if ((cf.ChangeFlags & 0x1) != 0 && cf.DataLength >= 4)      // CHANGE_FORM_FLAGS: new flags clear 0x800
            {
                if ((ReadUInt32At(cf.DataOffset) & 0x800) == 0)
                    result.Add(cf.FormId);
            }
            else if (cf.FormType == 0x08 && cf.ChangeFlags == 0x80000000 && cf.DataLength == 0) // bare enable marker
                result.Add(cf.FormId);
        }
        return result;
    }

    /// <summary>Each runtime-<b>created</b> reference change form (FormID high byte <c>0xFF</c>) paired with the
    /// save-space FormIDs its data references (ROADMAP §6 #16 Stage 2). A spawned actor is created from a placed
    /// <b>template</b> reference, whose FormID it embeds; resolving that template → base actor → script binds the
    /// spawned kill (the 6th Ghost Town Gunfight ganger). RefIDs end a <c>0x7C</c>-delimited field, so the last 3
    /// bytes of each token are taken as a candidate refID (this also recovers the template at the tail of the
    /// un-delimited MOVE block). The caller filters the referenced FormIDs to actual templates of interest.</summary>
    public IEnumerable<(uint CreatedFormId, IReadOnlyList<uint> ReferencedFormIds)> CreatedReferenceForms()
    {
        foreach (var cf in EnumerateChangeForms())
        {
            if ((cf.FormId >> 24) != 0xFF || cf.DataLength <= 0)
                continue;
            var refs = new HashSet<uint>();
            foreach (var tok in ReferenceChangeForm.Tokenize(_raw.AsSpan(cf.DataOffset, cf.DataLength), cf.DataOffset))
            {
                var b = tok.Bytes;
                if (b.Length < 3)
                    continue;
                var raw = (b[^3] << 16) | (b[^2] << 8) | b[^1]; // refID fields end at the 0x7C delimiter
                var formId = ResolveRefId(raw);
                if (formId != 0)
                    refs.Add(formId);
            }
            if (refs.Count > 0)
                yield return (cf.FormId, refs.ToList());
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
        // 0xFFFFFFFF (= -1) is the engine's sentinel for an equipped base/quest item that can't be
        // dropped — e.g. the starting Pip-Boy 3000 + Pip-Boy Glove, which sit as the first inventory
        // stacks (controlled saves q1/q2). It's a genuine stack, so accept it explicitly; the 1,000,000
        // sane-count cap below is only a heuristic against misaligned reads of other large junk values.
        if (count == uint.MaxValue)
            return true;
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

    // ---- Player limb condition (six floats in the same actor-value array, §4n) ---------------------

    /// <summary>One player limb's condition: its <see cref="Name"/> (e.g. "Left Leg"), the array
    /// <see cref="Slot"/> (180–185), the current <see cref="Condition"/> (0 = undamaged, −58 = healed,
    /// −100 = crippled), and the float's absolute file <see cref="ValueOffset"/> (the edit point).</summary>
    public readonly record struct PlayerLimb(int Slot, string Name, float Condition, int ValueOffset);

    /// <summary>The player's six limb conditions (Torso / Left Arm / Right Arm / Left Leg / Right Leg / Head),
    /// read from slots 180–185 of the player reference record's actor-value array (§4n). Empty when that
    /// record/array isn't locatable (same graceful decline as karma/XP). Edit a limb with
    /// <see cref="TrySetLimbCondition"/> (0 = repaired); the value is a condition/damage figure, not 0–100 HP.</summary>
    public IReadOnlyList<PlayerLimb> PlayerLimbConditions()
    {
        var names = ReferenceChangeForm.PlayerLimbNames;
        var result = new List<PlayerLimb>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            if (PlayerStatOffset(ReferenceChangeForm.PlayerLimbBaseSlot + i) is not { } off)
                return [];   // array not located — decline wholesale (don't return a partial limb set)
            var v = BinaryPrimitives.ReadSingleLittleEndian(_raw.AsSpan(off, 4));
            result.Add(new PlayerLimb(ReferenceChangeForm.PlayerLimbBaseSlot + i, names[i], v, off));
        }
        return result;
    }

    /// <summary>Stages a same-length edit to one limb's condition (match <paramref name="limb"/> by name,
    /// case-insensitive; 0 = fully repaired). Returns false if the name is unknown or the slot isn't located.</summary>
    public bool TrySetLimbCondition(string limb, float condition)
    {
        var idx = Array.FindIndex(ReferenceChangeForm.PlayerLimbNames,
            n => n.Equals(limb, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && TrySetStat(ReferenceChangeForm.PlayerLimbBaseSlot + idx, condition);
    }

    /// <summary>Repairs all six limbs to undamaged (condition 0) via same-length float splices. Returns false
    /// if the limb array isn't locatable (no edits staged then).</summary>
    public bool TryRepairAllLimbs()
    {
        if (PlayerLimbConditions().Count == 0)
            return false;
        for (var i = 0; i < ReferenceChangeForm.PlayerLimbNames.Length; i++)
            TrySetStat(ReferenceChangeForm.PlayerLimbBaseSlot + i, 0f);
        return true;
    }

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

    /// <summary>One of the player's perks (or traits — FNV stores traits as PERK forms too): the base
    /// <c>PERK</c> FormID it resolves to, its rank, and the raw 3-byte refID that names it.</summary>
    public readonly record struct PlayerPerk(int RefId, uint FormId, int Rank);

    /// <summary>
    /// The player's <b>perks and traits</b> (ROADMAP §4n). They live as count-prefixed lists inside the player
    /// reference change form (iref = PlayerRef + 1 — the same record as inventory §4g and karma/XP §4j):
    /// <c>[count*4 : u8][7C]</c> then <c>count × ( [perkRef : 3 BE][7C][rank : u8][7C] )</c>, where each
    /// <c>perkRef</c> is the FormID-array index + 1 (the §4g "+1" convention) and resolves to a <c>PERK</c> record.
    /// <b>There are several such lists, same grammar:</b> the player's <b>chosen perks + chargen trait share one
    /// list</b> (taking a perk grows it — observed 1→2 across a controlled diff), while an <b>engine-granted perk</b>
    /// (e.g. Companion Suite) sits in its <b>own</b> separate list, so the reader must <b>union every</b> list, not
    /// pick one. Like <see cref="PipBoyNotes"/>, deciding "is this FormID a PERK?" needs the game masters (outside
    /// <c>Core</c>), so the caller injects that test via <paramref name="isPerkForm"/> (e.g. <c>fid =&gt;
    /// db.RecordType(fid) == "PERK"</c>).
    /// <para><b>Anchored, not loose.</b> Rather than report any bracketed <c>7C [ref:3] 7C</c> hit (which can match
    /// coincidental refIDs in the record's havok/actor-value bytes), we only read entries that sit inside a
    /// <b>structurally validated</b> count-prefixed list (<see cref="EnumeratePerkListEntries"/>): a positive
    /// multiple-of-4 count byte, then exactly that many contiguous 6-byte entries whose refIDs resolve. Because a
    /// list entry is always itself preceded by a <c>7C</c> (the prefix's or the previous entry's trailing one), this
    /// set is a strict subset of the old loose scan — it can never over-report, and every real perk lives in such a
    /// list so it never drops one (verified across the corpus). Validation is masters-independent; the PERK filter is
    /// applied after, so a narrow single-FormID predicate still finds its perk inside a multi-perk list.</para>
    /// Deduplicated by FormID; empty if the record or masters are missing. Read-only.
    /// </summary>
    public IReadOnlyList<PlayerPerk> PlayerPerks(Func<uint, bool> isPerkForm)
    {
        ArgumentNullException.ThrowIfNull(isPerkForm);
        if (PlayerInventoryChangeForm is not { } cf)
            return [];
        var seen = new HashSet<uint>();
        var result = new List<PlayerPerk>();
        foreach (var (refId, rank, _) in EnumeratePerkListEntries(cf))
        {
            var formId = ResolveIref(refId - 1);
            if (formId == 0 || !isPerkForm(formId) || !seen.Add(formId))
                continue;
            result.Add(new PlayerPerk(refId, formId, rank));
        }
        return result;
    }

    /// <summary>
    /// Enumerates every perk-list entry — <c>(refId, rank, entryOffset)</c> for a <c>[ref:3 BE][7C][rank:u8][7C]</c>
    /// entry — that sits inside a <b>structurally validated</b> count-prefixed list (<c>[count*4:u8][7C]</c> then
    /// <c>count ×</c> entries) anywhere in the player reference record (§4n). Validation is masters-independent (see
    /// <see cref="TryValidatePerkListAt"/>), so every yielded entry is anchored to a real list rather than a
    /// coincidental <c>7C [ref] 7C</c> hit. Callers apply the PERK filter + dedup. The scan checks every byte offset
    /// (it never skips ahead), so it can't miss a real list hidden behind a spurious earlier anchor.
    /// </summary>
    private IEnumerable<(int RefId, int Rank, int EntryOffset)> EnumeratePerkListEntries(ChangeFormHeader cf)
    {
        var end = cf.DataOffset + cf.DataLength;
        for (var p = cf.DataOffset; p + 1 < end; p++)
        {
            if (!TryValidatePerkListAt(p, end, out var count))
                continue;
            var entry = p + 2;
            for (var k = 0; k < count; k++, entry += 6)
            {
                var refId = (_raw[entry] << 16) | (_raw[entry + 1] << 8) | _raw[entry + 2];
                yield return (refId, _raw[entry + 4], entry);
            }
        }
    }

    /// <summary>Validates a <c>[count*4:u8][7C]</c> perk-list prefix at <paramref name="p"/>: the count byte is a
    /// positive multiple of 4, a <c>7C</c> follows, and exactly <c>count</c> contiguous 6-byte
    /// <c>[ref:3][7C][rank][7C]</c> entries follow, each with a positive refID resolving to a non-zero form (the
    /// §4g "+1" convention). Masters-independent — the PERK test is applied by the caller.</summary>
    private bool TryValidatePerkListAt(int p, int end, out int count)
    {
        count = 0;
        var countByte = _raw[p];
        if (countByte == 0 || countByte % 4 != 0 || _raw[p + 1] != 0x7C)
            return false;
        var n = countByte / 4;
        var entry = p + 2;
        for (var k = 0; k < n; k++, entry += 6)
        {
            if (entry + 6 > end || _raw[entry + 3] != 0x7C || _raw[entry + 5] != 0x7C)
                return false;
            var refId = (_raw[entry] << 16) | (_raw[entry + 1] << 8) | _raw[entry + 2];
            if (refId <= 0 || ResolveIref(refId - 1) == 0)
                return false;
        }
        count = n;
        return true;
    }

    // ---- Player CHANGE_ACTOR record: added spells / effects, incl. addictions (SPEC §4n) ------------

    /// <summary>The player's <b>CHANGE_ACTOR</b> change form (form type <c>0x0A</c>, iref =
    /// playerBase <c>0x07</c> + 1) — the record holding the actor's added-spell list (addictions live here),
    /// SPECIAL, name, and AI package data. Located by the iref convention and validated by
    /// <see cref="ChangeActorPayload.TryDecode"/> (so a stray <c>0x0A</c> record can't be mistaken for it).
    /// Null if it can't be located/decoded.</summary>
    public ChangeFormHeader? PlayerActorChangeForm
    {
        get
        {
            var iref = FindIref(0x07);
            if (iref < 0)
                return null;
            foreach (var cf in EnumerateChangeForms())
            {
                if (cf.Iref != iref + 1 || cf.FormType != ChangeActorPayload.FormType)
                    continue;
                var data = _raw.AsSpan(cf.DataOffset, cf.DataLength);
                return ChangeActorPayload.TryDecode(data, cf.ChangeFlags, out _) ? cf : (ChangeFormHeader?)null;
            }
            return null;
        }
    }

    /// <summary>One entry of the player's added-spell list: the base <c>SPEL</c> FormID it resolves to and the raw
    /// 3-byte refID that names it (the FormID-array index + 1, §4g convention).</summary>
    public readonly record struct ActorSpell(int RefId, uint FormId);

    /// <summary>
    /// The player's <b>added spells / actor effects</b> (SPEC §4n) — a count-prefixed list in the player
    /// CHANGE_ACTOR record (<see cref="PlayerActorChangeForm"/>): <c>[count×4][7C]</c> then
    /// <c>count × ([spellRef:3 BE][7C])</c> then a <c>[00][7C]</c> trailer, each <c>spellRef</c> resolving via
    /// <c>FormIdArray[spellRef − 1]</c> to a <c>SPEL</c> record. The list mixes <b>addictions</b> (SPEL spell-type
    /// <c>Addiction</c>, e.g. "Buffout Withdrawal"), chargen <b>traits</b> ("Built to Destroy"), and mod
    /// <b>abilities</b> ("Skilled Bonus"). As with <see cref="PlayerPerks"/>, deciding "is this FormID the kind I
    /// want?" needs the masters, so the caller injects <paramref name="isSpellForm"/> (e.g.
    /// <c>fid =&gt; db.IsAddiction(fid)</c> for addictions only, or <c>fid =&gt; db.RecordType(fid) == "SPEL"</c>
    /// for every actor effect). Deduplicated by FormID; empty if the record can't be located. Read-only (adding/
    /// removing a spell is length-changing).
    /// <para>Cracked by the <c>beadley-addiction-*</c> controlled FIFO diff: adding Buffout → Alcohol → Med-X grew
    /// the list newest-first (Buffout <c>0x0030BB</c> → "Buffout Withdrawal" via the −1 index), FIFO removal dropped
    /// the oldest.</para>
    /// </summary>
    public IReadOnlyList<ActorSpell> PlayerActorSpells(Func<uint, bool> isSpellForm)
    {
        ArgumentNullException.ThrowIfNull(isSpellForm);
        if (PlayerActorChangeForm is not { } cf
            || !ChangeActorPayload.TryDecode(_raw.AsSpan(cf.DataOffset, cf.DataLength), cf.ChangeFlags, out var rec))
            return [];
        var seen = new HashSet<uint>();
        var result = new List<ActorSpell>();
        foreach (var (refId, _) in rec.Spells)
        {
            var formId = ResolveIref(refId - 1);   // the §4g "+1" convention
            if (formId == 0 || !isSpellForm(formId) || !seen.Add(formId))
                continue;
            result.Add(new ActorSpell(refId, formId));
        }
        return result;
    }

    /// <summary>The player's standing with one faction: the <c>REPU</c> faction FormID and its
    /// <see cref="Fame"/>/<see cref="Infamy"/> (0–100 floats). A faction with both 0 shows no standing in the
    /// Pip-Boy.</summary>
    public readonly record struct FactionReputation(int RefId, uint FactionFormId, float Fame, float Infamy);

    /// <summary>
    /// The player's <b>faction reputation</b> (ROADMAP §4o). Each faction the player has standing with gets a
    /// <b>type-<c>0x2B</c> change form</b> whose payload is <c>[fame:f32 LE][7C][infamy:f32 LE][7C]</c> (len 10),
    /// keyed by the faction's <c>REPU</c> record: the change form's refID is the REPU's FormID-array index + 1
    /// (the §4g "+1"/persisted-reference convention), so the faction is <c>FormIdArray[refID − 1]</c>. As with
    /// <see cref="PipBoyNotes"/>/<see cref="PlayerPerks"/>, deciding "is this FormID a REPU?" needs the masters, so
    /// the caller injects <paramref name="isRepuForm"/> (e.g. <c>fid =&gt; db.RecordType(fid) == "REPU"</c>).
    /// Cracked by a controlled diff (Goodsprings idolized → wiped: fame 100.0 → 0.0). Read-only; fame/infamy are
    /// same-length float splices if editing is added later.
    /// </summary>
    public IReadOnlyList<FactionReputation> Reputations(Func<uint, bool> isRepuForm)
    {
        var result = new List<FactionReputation>();
        foreach (var cf in EnumerateChangeForms())
        {
            if (cf.FormType != 0x2B || cf.DataLength != 10)
                continue;
            if (_raw[cf.DataOffset + 4] != 0x7C || _raw[cf.DataOffset + 9] != 0x7C)
                continue;
            var faction = ResolveIref(cf.Iref - 1);             // the +1 (persisted-reference) convention
            if (faction == 0 || !isRepuForm(faction))
                continue;
            var fame = BitConverter.ToSingle(_raw, cf.DataOffset);
            var infamy = BitConverter.ToSingle(_raw, cf.DataOffset + 5);
            result.Add(new FactionReputation(cf.Iref, faction, fame, infamy));
        }
        return result;
    }

    /// <summary>Sets a faction's <see cref="FactionReputation.Fame"/> and <see cref="FactionReputation.Infamy"/>
    /// (§4o) by splicing the two float32s in its type-<c>0x2B</c> change form (same-length, like karma/XP §4j).
    /// Matches the faction by FormID directly (the change form's <c>FormIdArray[refID − 1]</c>), so no masters are
    /// needed for the edit. Returns false if the faction has no reputation record in this save.</summary>
    public bool TrySetReputation(uint factionFormId, float fame, float infamy)
    {
        foreach (var cf in EnumerateChangeForms())
        {
            if (cf.FormType != 0x2B || cf.DataLength != 10) continue;
            if (_raw[cf.DataOffset + 4] != 0x7C || _raw[cf.DataOffset + 9] != 0x7C) continue;
            if (ResolveIref(cf.Iref - 1) != factionFormId) continue;
            StageSingle(cf.DataOffset, fame);          // fame  @ +0
            StageSingle(cf.DataOffset + 5, infamy);    // infamy @ +5 (after the 0x7C at +4)
            return true;
        }
        return false;
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

    // ---- Editing (length-changing — offset-fixup) --------------------------
    // The File Location Table holds *absolute* file offsets, so inserting/removing body bytes shifts
    // every section after the edit. RebuildWithBodyEdits applies a set of body splices and recomputes
    // the five FLT offsets + the three counts so the result re-parses cleanly (the change-form walker
    // still lands exactly on GlobalData3Offset and the FormID array re-parses with its new count).
    // ROADMAP §6 #5; the no-op path (no splices) still equals ToBytes() byte-for-byte.

    /// <summary>One length-changing body edit in original-file coordinates: replace the
    /// <paramref name="RemoveLength"/> bytes at <paramref name="At"/> with <paramref name="Insert"/>.
    /// A pure insertion has RemoveLength 0; a same-length rewrite has Insert.Length == RemoveLength.
    ///
    /// <para><paramref name="ShiftBoundaryOffset"/> decides what happens to an FLT offset that exactly
    /// equals <paramref name="At"/> (a region boundary). Default <c>true</c> (an <b>append</b>: the new
    /// bytes belong to the preceding region, so a following region whose start equals <c>At</c> shifts).
    /// <c>false</c> (a <b>prepend</b>: the new bytes become the new start of the region at <c>At</c>, so
    /// that region's own start offset must <i>not</i> move while later regions do).</para></summary>
    public readonly record struct BodySplice(int At, int RemoveLength, byte[] Insert, bool ShiftBoundaryOffset = true)
    {
        /// <summary>Net change in file length this splice contributes (insert minus removed).</summary>
        public int NetDelta => Insert.Length - RemoveLength;

        /// <summary>Append <paramref name="bytes"/> at <paramref name="at"/> — the bytes belong to the region
        /// ending there, so a following region whose start equals <paramref name="at"/> shifts forward.</summary>
        public static BodySplice InsertAt(int at, byte[] bytes) => new(at, 0, bytes);

        /// <summary>Prepend <paramref name="bytes"/> as the new start of the region beginning at
        /// <paramref name="at"/> — that region's own start offset stays put while later regions shift.</summary>
        public static BodySplice Prepend(int at, byte[] bytes) => new(at, 0, bytes, ShiftBoundaryOffset: false);
    }

    /// <summary>
    /// Rebuilds the save applying length-changing <paramref name="splices"/> (in original-file coordinates,
    /// non-overlapping) on top of any staged same-length edits, recomputing the File Location Table's five
    /// absolute offsets and bumping the requested counts. Returns fresh bytes (the caller re-parses); this
    /// instance is untouched. With no splices and no count deltas the result equals <see cref="ToBytes"/>
    /// byte-for-byte. Splices may target the body (the common case — add-reputation/perk/item) or the
    /// <b>header</b> (a length-changing rename, which also resizes <c>SaveHeaderSize</c> and shifts the whole body).
    ///
    /// <para><b>Offset rule:</b> each FLT offset shifts by the summed <see cref="BodySplice.NetDelta"/>
    /// of every splice before it — strictly before (<c>splice.At &lt; value</c>) for a prepend, or at-or-
    /// before (<c>splice.At &lt;= value</c>) for an append (see <see cref="BodySplice.ShiftBoundaryOffset"/>).
    /// A pre-body (header) splice is before every FLT offset, so it shifts them all — and it also shifts the
    /// FLT's own position, so the eight u32 slots are written at the FLT's <em>new</em> base, not
    /// <see cref="BodyOffset"/>.</para>
    /// </summary>
    public byte[] RebuildWithBodyEdits(
        IReadOnlyList<BodySplice> splices,
        int changeFormCountDelta = 0,
        int globalData1CountDelta = 0,
        int globalData3CountDelta = 0)
    {
        ArgumentNullException.ThrowIfNull(splices);
        var baseBytes = ToBytes(); // honours staged same-length edits; same length as _raw

        var ordered = splices.OrderBy(s => s.At).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            var s = ordered[i];
            // Splices may target the header (pre-body, e.g. a length-changing rename) as well as the body; only
            // the bounds + non-overlap matter. A pre-body splice shifts the whole body, including the FLT (below).
            if (s.RemoveLength < 0 || s.At < 0 || s.At + s.RemoveLength > baseBytes.Length)
                throw new ArgumentOutOfRangeException(nameof(splices), $"splice at 0x{s.At:X} is out of range");
            if (i > 0 && ordered[i - 1].At + ordered[i - 1].RemoveLength > s.At)
                throw new ArgumentException("overlapping splices", nameof(splices));
        }

        // Emit the new bytes: copy each gap between splices, then write the splice's Insert.
        var output = new byte[baseBytes.Length + ordered.Sum(s => s.NetDelta)];
        var src = 0; // read cursor in baseBytes
        var dst = 0; // write cursor in output
        foreach (var s in ordered)
        {
            var gap = s.At - src;
            Array.Copy(baseBytes, src, output, dst, gap);
            dst += gap;
            Array.Copy(s.Insert, 0, output, dst, s.Insert.Length);
            dst += s.Insert.Length;
            src = s.At + s.RemoveLength;
        }
        Array.Copy(baseBytes, src, output, dst, baseBytes.Length - src);

        // Recompute the five FLT offsets (shift by splices strictly before each) and bump the counts.
        var raw = Flt.Raw;
        Span<uint> slots = stackalloc uint[8];
        for (var i = 0; i < 5; i++)
        {
            long shift = 0;
            foreach (var s in ordered)
                // A boundary offset (== At) shifts for an append but not a prepend (see ShiftBoundaryOffset).
                if (s.ShiftBoundaryOffset ? s.At <= raw[i] : s.At < raw[i])
                    shift += s.NetDelta;
            slots[i] = (uint)(raw[i] + shift);
        }
        slots[5] = (uint)(raw[5] + globalData1CountDelta);
        slots[6] = (uint)(raw[6] + globalData3CountDelta);
        slots[7] = (uint)(raw[7] + changeFormCountDelta);
        // The FLT sits at the start of the body; a pre-body (header) splice shifts it forward by that splice's
        // net delta, so locate the slots at their new base in the output. Body-only edits leave this at BodyOffset.
        var fltBase = BodyOffset;
        foreach (var s in ordered)
            if (s.At < BodyOffset)
                fltBase += s.NetDelta;
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(fltBase + i * 4, 4), slots[i]);

        return output;
    }

    /// <summary>
    /// Adds a faction reputation record (§4o) for a faction that has none yet — the first length-changing
    /// consumer of <see cref="RebuildWithBodyEdits"/>. Appends the faction's FormID to the FormID array if
    /// absent (array + count grow), creates a type-<c>0x2B</c> change form
    /// <c>[fame:f32][7C][infamy:f32][7C]</c> with refID = (its array index)+1 (the persisted-reference
    /// convention), prepends it to the change-forms region (ChangeFormCount++), and fixes up every
    /// absolute offset. Returns a freshly parsed save containing the new record. Throws
    /// <see cref="InvalidOperationException"/> if the faction already has a reputation record (use
    /// <see cref="TrySetReputation"/> instead).
    /// </summary>
    public FalloutSave AddReputation(uint factionFormId, float fame, float infamy)
    {
        foreach (var cf in EnumerateChangeForms())
        {
            if (cf.FormType != 0x2B || cf.DataLength != 10) continue;
            if (_raw[cf.DataOffset + 4] != 0x7C || _raw[cf.DataOffset + 9] != 0x7C) continue;
            if (ResolveIref(cf.Iref - 1) == factionFormId)
                throw new InvalidOperationException(
                    $"faction 0x{factionFormId:X8} already has a reputation record — use TrySetReputation");
        }

        var splices = new List<BodySplice>();

        // Find the faction's iref (FormID-array index); append it to the array end if missing.
        var iref = ResolveOrAppendFormId(factionFormId, splices);

        // Build the 20-byte type-0x2B record and prepend it to the change-forms region.
        var refId = iref + 1; // persisted-reference "+1" convention; fits 22 bits => RefID type 0 (array index)
        var record = new byte[20];
        record[0] = (byte)(refId >> 16);
        record[1] = (byte)(refId >> 8);
        record[2] = (byte)refId;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(3, 4), ReputationChangeFlags);
        record[7] = 0x2B;               // formType 0x2B; length 10 => u8 width (top 2 bits 0)
        record[8] = ChangeFormVersionNv; // 0x1B
        record[9] = 10;                 // payload length
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(10, 4), fame);
        record[14] = Delimiter;
        BinaryPrimitives.WriteSingleLittleEndian(record.AsSpan(15, 4), infamy);
        record[19] = Delimiter;
        splices.Add(BodySplice.Prepend((int)Flt.ChangeFormsOffset, record)); // new record becomes the region start

        return Parse(RebuildWithBodyEdits(splices, changeFormCountDelta: 1));
    }

    /// <summary>The change form's length-field width in bytes — fixed by the type byte's top 2 bits
    /// (<c>type &gt;&gt; 6</c> → 1/2/4), independent of the value. So growing a record's payload (within the
    /// width's range) rewrites the field in place; it never shifts the data start.</summary>
    private static int LengthFieldWidth(byte typeByte) => (typeByte >> 6) switch { 0 => 1, 1 => 2, _ => 4 };

    /// <summary>A same-length <see cref="BodySplice"/> that rewrites change form <paramref name="cf"/>'s length
    /// field (at <c>cf.Offset + 9</c>, width <see cref="LengthFieldWidth"/>) to <c>cf.DataLength + delta</c> — the
    /// shared piece for growing an existing record's payload (grant-perk / add-item). Throws if the new length
    /// doesn't fit the field's fixed width (would need a wider header — not supported here).</summary>
    private static BodySplice GrowRecordLengthSplice(ChangeFormHeader cf, int delta)
    {
        var width = LengthFieldWidth(cf.TypeByte);
        var newLen = cf.DataLength + delta;
        var max = width switch { 1 => 0xFF, 2 => 0xFFFF, _ => int.MaxValue };
        if (newLen < 0 || newLen > max)
            throw new InvalidOperationException(
                $"record at 0x{cf.Offset:X} new length {newLen} doesn't fit its {width}-byte length field");
        var bytes = new byte[width];
        switch (width)
        {
            case 1: bytes[0] = (byte)newLen; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)newLen); break;
            default: BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)newLen); break;
        }
        return new BodySplice(cf.Offset + 9, width, bytes); // in-place rewrite (RemoveLength == Insert.Length)
    }

    /// <summary>Resolves <paramref name="formId"/> to its FormID-array index, or appends it to the array end
    /// (emitting the count-bump + entry splices into <paramref name="splices"/>) when absent. Returns the index;
    /// the persisted-reference refID is <c>index + 1</c>. Mirrors the array-extend in <see cref="AddReputation"/>.</summary>
    private int ResolveOrAppendFormId(uint formId, List<BodySplice> splices)
    {
        var iref = FindIref(formId);
        if (iref >= 0)
            return iref;
        var arrayAt = (int)Flt.FormIdArrayCountOffset;
        if (arrayAt <= 0 || arrayAt + 4 > _raw.Length)
            throw new InvalidOperationException("save has no FormID array to extend");
        var oldCount = (int)ReadUInt32At(arrayAt);
        var newCount = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(newCount, (uint)(oldCount + 1));
        splices.Add(new BodySplice(arrayAt, 4, newCount));                       // bump count (same length)
        var entry = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, formId);
        splices.Add(BodySplice.InsertAt(arrayAt + 4 + oldCount * 4, entry));     // append FormID
        return oldCount;
    }

    /// <summary>
    /// Grants the player a perk (or trait — FNV stores both as <c>PERK</c> forms; ROADMAP §4n / §6 #5). Appends a
    /// <c>[perkRef:3 BE][7C][rank:u8][7C]</c> entry to the count-prefixed perk list inside the player reference
    /// change form, bumps the <c>[count*4:u8]</c> prefix, grows the record's length field, and appends the perk's
    /// FormID to the FormID array if absent — all via <see cref="RebuildWithBodyEdits"/>. Because "is this FormID a
    /// PERK?" needs the masters (outside Core), the caller injects <paramref name="isPerkForm"/> (e.g.
    /// <c>fid =&gt; db.RecordType(fid) == "PERK"</c>), used to locate the existing list. Returns a freshly parsed
    /// save. Throws <see cref="InvalidOperationException"/> if the perk is already present, or if the perk list
    /// can't be located (v1 requires at least one existing perk/trait — present from chargen).
    /// </summary>
    public FalloutSave AddPerk(uint perkFormId, int rank, Func<uint, bool> isPerkForm)
    {
        ArgumentNullException.ThrowIfNull(isPerkForm);
        if (rank is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(rank), "perk rank must be 0..255");
        if (PlayerPerks(isPerkForm).Any(p => p.FormId == perkFormId))
            throw new InvalidOperationException($"perk 0x{perkFormId:X8} is already present");
        if (PlayerInventoryChangeForm is not { } cf)
            throw new InvalidOperationException("player reference change form not found");
        if (!TryLocatePerkList(cf, isPerkForm, out var countPrefixOffset, out var storedCount))
            throw new InvalidOperationException(
                "could not locate the perk list (v1 requires at least one existing perk/trait)");
        if (storedCount >= 63)
            throw new InvalidOperationException("perk list is full (count*4 would overflow its u8 prefix)");

        var splices = new List<BodySplice>();
        var perkRef = ResolveOrAppendFormId(perkFormId, splices) + 1; // persisted-reference "+1" convention

        // New 6-byte entry, inserted as the new first entry (right after [count*4][7C]); order is irrelevant.
        var entry = new byte[] { (byte)(perkRef >> 16), (byte)(perkRef >> 8), (byte)perkRef, Delimiter, (byte)rank, Delimiter };
        splices.Add(BodySplice.InsertAt(countPrefixOffset + 2, entry));

        // Bump the [count*4] prefix by 4 (one more entry) — a same-length 1-byte rewrite.
        splices.Add(new BodySplice(countPrefixOffset, 1, [(byte)(_raw[countPrefixOffset] + 4)]));

        // Grow the player-ref record's length field by the inserted entry.
        splices.Add(GrowRecordLengthSplice(cf, entry.Length));

        return Parse(RebuildWithBodyEdits(splices)); // no new record: change-form count unchanged
    }

    /// <summary>Locates the <b>chosen-perks/trait</b> list's <c>[count*4:u8][7C]</c> prefix inside the player
    /// reference record for <see cref="AddPerk"/>: the first <b>validated</b> count-prefixed list
    /// (<see cref="TryValidatePerkListAt"/>) that holds at least one entry resolving to a PERK via
    /// <paramref name="isPerkForm"/> (engine-granted perks like Companion Suite sit in their own separate list
    /// later in the record, so the first PERK-bearing list by offset is the chosen/trait one a new perk joins).
    /// Returns false if no such list exists — the zero-perk first-perk case is handled by <see cref="AddPerk"/>.</summary>
    private bool TryLocatePerkList(ChangeFormHeader cf, Func<uint, bool> isPerkForm, out int countPrefixOffset, out int storedCount)
    {
        countPrefixOffset = -1;
        storedCount = 0;
        var end = cf.DataOffset + cf.DataLength;
        for (var p = cf.DataOffset; p + 1 < end; p++)
        {
            if (!TryValidatePerkListAt(p, end, out var count))
                continue;
            var entry = p + 2;
            for (var k = 0; k < count; k++, entry += 6)
            {
                var refId = (_raw[entry] << 16) | (_raw[entry + 1] << 8) | _raw[entry + 2];
                if (isPerkForm(ResolveIref(refId - 1)))
                {
                    countPrefixOffset = p;
                    storedCount = count;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Adds an inventory stack of <paramref name="count"/> × <paramref name="itemFormId"/> to the player
    /// (ROADMAP §4g/§4i / §6 #5). Appends a minimal stack <c>[ref:3 BE][7C][count:u32 LE][7C][00][7C]</c> (no
    /// extra data) after the existing item run, bumps the inventory's <c>vsval</c> stack count (growing its width
    /// if it crosses a 63/16383 boundary), grows the record's length field, and appends the item's FormID to the
    /// FormID array if absent — all via <see cref="RebuildWithBodyEdits"/>. Returns a freshly parsed save. Throws
    /// <see cref="InvalidOperationException"/> if the player inventory list can't be located deterministically
    /// (an atypical/modded ExtraDataList whose vsval the structural walk can't pin).
    /// </summary>
    public FalloutSave AddInventoryItem(uint itemFormId, uint count)
    {
        if (PlayerInventoryChangeForm is not { } cf)
            throw new InvalidOperationException("player inventory change form not found");
        var end = cf.DataOffset + cf.DataLength;
        var span = _raw.AsSpan(0, end); // index == absolute file offset
        var start = ReferenceChangeForm.InventorySearchStart(
            _raw.AsSpan(cf.DataOffset, cf.DataLength), cf.DataOffset, cf.ChangeFlags);
        if (!ReferenceChangeForm.TryInventoryItemsStart(span, start, out var itemsOffset, out var stackCount) || stackCount <= 0)
            throw new InvalidOperationException("inventory list start not located deterministically (atypical/modded record)");

        // The vsval stack count + its 0x7C delimiter sit just before the first item; recover its offset + width.
        var vsvalOffset = -1;
        var oldWidth = 0;
        foreach (var w in (ReadOnlySpan<int>)[1, 2, 4])
        {
            var vp = itemsOffset - 1 - w;
            if (vp >= cf.DataOffset && ReferenceChangeForm.ReadVsval(span, vp, out var vl) == stackCount && vl == w)
            {
                vsvalOffset = vp;
                oldWidth = w;
                break;
            }
        }
        if (vsvalOffset < 0)
            throw new InvalidOperationException("could not locate the inventory vsval stack count");

        // Walk exactly stackCount stacks to find the true end of the item run (the insertion point) — not the
        // full decoded chain, which can over-read a few non-item bytes past the engine's count.
        var p = itemsOffset;
        for (var k = 0; k < stackCount; k++)
        {
            if (!TryReadInventoryEntry(p, out _, out _, out _))
                throw new InvalidOperationException($"inventory stack {k} not where the vsval count expects it");
            p = AdvancePastStack(p, end, out _);
            if (p <= itemsOffset || p > end)
                throw new InvalidOperationException("inventory stack walk did not advance");
        }
        var listEnd = p;

        var splices = new List<BodySplice>();
        var refId = ResolveOrAppendFormId(itemFormId, splices) + 1; // persisted-reference "+1" convention

        // Minimal stack: [ref:3 BE][7C][count:u32 LE][7C][00][7C] (the [00][7C] = "no extra data" block, §4i).
        var stack = new byte[11];
        stack[0] = (byte)(refId >> 16);
        stack[1] = (byte)(refId >> 8);
        stack[2] = (byte)refId;
        stack[3] = Delimiter;
        BinaryPrimitives.WriteUInt32LittleEndian(stack.AsSpan(4, 4), count);
        stack[8] = Delimiter;
        stack[9] = 0x00;
        stack[10] = Delimiter;
        splices.Add(BodySplice.InsertAt(listEnd, stack));

        // Bump the vsval stack count (may widen the field); track the width delta for the record-length growth.
        var newVsval = ReferenceChangeForm.WriteVsval(stackCount + 1);
        splices.Add(new BodySplice(vsvalOffset, oldWidth, newVsval));
        var vsvalDelta = newVsval.Length - oldWidth;

        splices.Add(GrowRecordLengthSplice(cf, stack.Length + vsvalDelta));

        return Parse(RebuildWithBodyEdits(splices)); // no new record: change-form count unchanged
    }

    /// <summary>
    /// Renames the player — a <b>length-changing</b> edit (ROADMAP §6 #5), unlike same-length
    /// <see cref="TrySetPlayerName"/>. The name appears twice, with different accounting, and both are updated:
    /// the <b>file header</b> field (<c>[u16 len][7C][name][7C]</c>), which also resizes <c>SaveHeaderSize</c> and
    /// thereby shifts the whole body (every absolute FLT offset moves), and the <b>player-actor change-form
    /// record</b> in the body (grown via <see cref="GrowRecordLengthSplice"/>). Offset fixup is done by
    /// <see cref="RebuildWithBodyEdits"/>. Returns a freshly parsed save (this instance is untouched). A
    /// same-length name reduces to in-place rewrites (result equals the original size). Throws
    /// <see cref="ArgumentException"/> for an empty/over-long name or one that overflows the body record's
    /// length field.
    /// </summary>
    public FalloutSave RenamePlayer(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var newBytes = Latin1.GetBytes(name);
        if (newBytes.Length is 0 or > 0xFFFF)
            throw new ArgumentException("player name must be 1..65535 bytes", nameof(name));

        var oldBytes = Latin1.GetBytes(PlayerName);
        var headerDelta = newBytes.Length - _playerNameByteLength;
        var splices = new List<BodySplice>();

        // --- Header copy: [u16 len][7C][name][7C]; resize SaveHeaderSize by the same delta (the header grew). ---
        splices.Add(new BodySplice(_playerNameValueOffset - 3, 2, U16LE((ushort)newBytes.Length)));
        splices.Add(new BodySplice(_playerNameValueOffset, _playerNameByteLength, newBytes));
        splices.Add(new BodySplice(SaveHeaderSizeOffset, 4, U32LE((uint)(SaveHeaderSize + headerDelta))));

        // --- Body copy: the same [u16 len][7C][name][7C] inside the player-actor change-form record. ---
        if (TryLocateBodyNameField(oldBytes, out var bodyNameValueOffset, out var bodyCf))
        {
            splices.Add(new BodySplice(bodyNameValueOffset - 3, 2, U16LE((ushort)newBytes.Length)));
            splices.Add(new BodySplice(bodyNameValueOffset, oldBytes.Length, newBytes));
            splices.Add(GrowRecordLengthSplice(bodyCf, headerDelta));
        }

        return Parse(RebuildWithBodyEdits(splices)); // no new record: counts unchanged
    }

    /// <summary>Finds the player-name field <c>[u16 len][7C][name][7C]</c> inside the change-forms region (the
    /// body copy <see cref="LocateSpecial"/> keys on, distinct from the header copy) and the change form that
    /// contains it. Returns false if the name isn't serialized in a change form (e.g. a very-early save).</summary>
    private bool TryLocateBodyNameField(byte[] nameBytes, out int valueOffset, out ChangeFormHeader containingForm)
    {
        valueOffset = -1;
        containingForm = default;
        if (nameBytes.Length == 0)
            return false;

        var pattern = new byte[3 + nameBytes.Length + 1];
        pattern[0] = (byte)(nameBytes.Length & 0xFF);
        pattern[1] = (byte)((nameBytes.Length >> 8) & 0xFF);
        pattern[2] = Delimiter;
        Array.Copy(nameBytes, 0, pattern, 3, nameBytes.Length);
        pattern[^1] = Delimiter;

        var start = (int)Flt.ChangeFormsOffset;
        var end = Math.Min((int)Flt.GlobalData3Offset, _raw.Length) - pattern.Length;
        for (var i = start; i <= end; i++)
        {
            if (!MatchAt(i, pattern))
                continue;
            // Identify the change form whose payload [DataOffset, DataOffset+DataLength) covers this field.
            foreach (var cf in EnumerateChangeForms())
            {
                if (i < cf.DataOffset || i + pattern.Length > cf.DataOffset + cf.DataLength)
                    continue;
                valueOffset = i + 3; // past [u16 len][7C]
                containingForm = cf;
                return true;
            }
        }
        return false;
    }

    private static byte[] U16LE(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
    private static byte[] U32LE(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
}
