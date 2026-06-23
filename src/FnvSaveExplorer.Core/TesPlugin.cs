using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FnvSaveExplorer.Core;

/// <summary>
/// A minimal, read-only reader for a Bethesda TES4-generation plugin (<c>.esm</c>/<c>.esp</c>) as used
/// by Fallout 3 / New Vegas. It extracts just what FormID → display-name resolution needs: the ordered
/// master list (from the <c>TES4</c> header's <c>MAST</c> subrecords) and, for item-bearing record types
/// (and quests), each form's FormID and display name (the <c>FULL</c> field, falling back to the editor id
/// <c>EDID</c>).
///
/// <para><b>Streaming + GRUP-skipping.</b> The file is read forward; top-level <c>GRUP</c>s whose record
/// type we don't care about are skipped by seeking past them — <c>FalloutNV.esm</c> is ~245 MB, so only
/// the comparatively tiny item groups are actually decoded. FO3/FNV use <b>24-byte</b> record and group
/// headers; a record's <c>DataSize</c> excludes its 24-byte header, a group's <c>GroupSize</c> includes it.</para>
///
/// <para>This is read-only against the game files; it never modifies them and is built on demand (it is not
/// part of <see cref="FalloutSave"/> parsing), so save handling stays fast and offline.</para>
/// </summary>
public sealed class TesPlugin
{
    private const uint CompressedFlag = 0x00040000; // record flag: data is [u32 decompSize][zlib]
    private const uint LocalizedFlag  = 0x00000080; // TES4 header flag: FULL is a string-table id, not inline text

    private const int HeaderSize = 24; // record + group header width on FO3/FNV

    /// <summary>Record types whose forms can appear in an inventory and so warrant a readable name.</summary>
    private static readonly HashSet<string> ItemTypes =
    [
        "WEAP", "ARMO", "ALCH", "AMMO", "MISC", "BOOK", "NOTE", "KEYM", "IMOD",
        "CCRD", "CHIP", "CMNY", "CDCK", // New Vegas: caravan card, casino chip, caps ("money"), caravan deck
    ];

    /// <summary>
    /// Record types we decode for FormID → name resolution: <see cref="ItemTypes"/> plus <c>QUST</c>. A quest
    /// is not an inventory item, so it never reaches the inventory-only Pip-Boy tab mapping
    /// (<see cref="PluginDatabase.PipBoyTab"/>); naming quest forms is what lets quest change forms be
    /// identified by record type (ROADMAP §6 #10), so <c>QUST</c> is indexed here yet deliberately kept out
    /// of <see cref="ItemTypes"/>.
    /// </summary>
    private static readonly HashSet<string> NamedTypes = [.. ItemTypes, "QUST"];

    /// <summary>The plugin's file name (e.g. <c>FalloutNV.esm</c>).</summary>
    public string FileName { get; }

    /// <summary>The plugin's masters, in order — index <c>i</c> is the plugin-local high byte that refers to it.</summary>
    public IReadOnlyList<string> Masters { get; }

    /// <summary>
    /// Named forms, keyed by <b>plugin-local</b> FormID (its high byte is an index into
    /// <see cref="Masters"/>, or == <c>Masters.Count</c> for forms the plugin defines itself). <c>Type</c>
    /// is the record signature (<c>WEAP</c>/<c>ARMO</c>/<c>ALCH</c>/<c>AMMO</c>/<c>MISC</c>/…, or <c>QUST</c>) —
    /// for item types it is the basis for the item's Pip-Boy tab (see <see cref="PluginDatabase.PipBoyTab"/>);
    /// <c>QUST</c> is indexed only so quests can be named/identified, not as inventory. <c>NoteType</c> is the
    /// <c>NOTE</c> record's <c>DATA</c> media byte (0=Sound, 1=Text, 2=Image, 3=Voice — the holodisk-vs-text
    /// distinction, ROADMAP §4k.1 #6), or <c>-1</c> for non-notes / notes with no <c>DATA</c>.
    /// </summary>
    public IReadOnlyList<(uint LocalFormId, string Name, string Type, int NoteType)> Forms { get; }

    /// <summary>The <c>QUST</c> records' decoded stage/objective structure, keyed by <b>plugin-local</b>
    /// FormID (re-keyed into save space by <see cref="PluginDatabase"/>) — the masters side of the quest-log
    /// reader (ROADMAP §6 #10). Empty unless the plugin defines quests.</summary>
    public IReadOnlyList<QuestDefinition> Quests { get; }

    private TesPlugin(
        string fileName,
        IReadOnlyList<string> masters,
        IReadOnlyList<(uint, string, string, int)> forms,
        IReadOnlyList<QuestDefinition> quests)
    {
        FileName = fileName;
        Masters = masters;
        Forms = forms;
        Quests = quests;
    }

    public static TesPlugin Load(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs, Path.GetFileName(path));
    }

    /// <summary>Parses a plugin from an arbitrary (seekable) stream — used by tests with an in-memory plugin.</summary>
    public static TesPlugin Read(Stream fs, string fileName)
    {
        // ---- TES4 header record (always first) ----
        var sig = ReadSignature(fs) ?? throw new SaveFormatException($"{fileName}: empty file", 0);
        if (sig != "TES4")
            throw new SaveFormatException($"{fileName}: not a TES4 plugin (found '{sig}')", 0);
        var headerDataSize = ReadU32(fs);
        var headerFlags = ReadU32(fs);
        ReadExactly(fs, 12); // FormID + version-control / form-version
        var localized = (headerFlags & LocalizedFlag) != 0;
        var headerData = ReadExactly(fs, (int)headerDataSize);

        var masters = new List<string>();
        foreach (var (type, data) in ParseSubrecords(headerData))
            if (type == "MAST")
                masters.Add(ZString(data));

        // ---- top-level groups ----
        var forms = new List<(uint, string, string, int)>();
        var quests = new List<QuestDefinition>();
        while (true)
        {
            var gsig = ReadSignature(fs);
            if (gsig is null)
                break; // clean EOF
            if (gsig != "GRUP")
                throw new SaveFormatException($"{fileName}: expected GRUP at top level, found '{gsig}'", (int)fs.Position - 4);

            var groupSize = ReadU32(fs);
            var label = ReadExactly(fs, 4);
            var groupType = ReadU32(fs);
            ReadExactly(fs, 8); // stamp + version + unknown
            var contentSize = (long)groupSize - HeaderSize;
            if (contentSize < 0)
                throw new SaveFormatException($"{fileName}: bad GRUP size {groupSize}", (int)fs.Position);

            var labelType = Encoding.ASCII.GetString(label);
            if (groupType == 0 && NamedTypes.Contains(labelType))
                ReadRecords(fs, contentSize, localized, forms, quests);
            else
                fs.Seek(contentSize, SeekOrigin.Current); // skip the whole group without reading its bytes
        }

        return new TesPlugin(fileName, masters, forms, quests);
    }

    /// <summary>Reads the records (and defensively skips any nested groups) inside one top-level item group.</summary>
    private static void ReadRecords(
        Stream fs, long contentSize, bool localized,
        List<(uint, string, string, int)> forms, List<QuestDefinition> quests)
    {
        var end = fs.Position + contentSize;
        while (fs.Position < end)
        {
            var sig = ReadSignature(fs);
            if (sig is null)
                break;
            var size = ReadU32(fs);
            if (sig == "GRUP")
            {
                ReadExactly(fs, 16); // rest of the 24-byte group header
                fs.Seek((long)size - HeaderSize, SeekOrigin.Current);
                continue;
            }

            var flags = ReadU32(fs);
            var formId = ReadU32(fs);
            ReadExactly(fs, 8); // version-control / form-version
            var data = ReadExactly(fs, (int)size);
            if ((flags & CompressedFlag) != 0)
                data = Decompress(data);

            var subs = ParseSubrecords(data);
            string? edid = null, full = null;
            var noteType = -1;
            foreach (var (type, sub) in subs)
            {
                if (type == "EDID") edid = ZString(sub);
                else if (type == "FULL" && !localized) full = ZString(sub);
                else if (type == "DATA" && sig == "NOTE" && sub.Length >= 1) noteType = sub[0]; // 0=Sound 1=Text 2=Image 3=Voice
            }
            var name = full ?? edid;
            if (!string.IsNullOrEmpty(name))
                forms.Add((formId, name, sig, noteType)); // sig is the record type (WEAP/ARMO/ALCH/AMMO/MISC/…)
            if (sig == "QUST")
                quests.Add(ParseQuest(formId, subs, localized, full));
        }
    }

    /// <summary>
    /// Decodes a <c>QUST</c> record's ordered subrecords into its <see cref="QuestDefinition"/> (ROADMAP §6 #10).
    /// FO3/FNV lay out a quest as a <b>stages block</b> then an <b>objectives block</b>:
    /// <list type="bullet">
    /// <item>A stage begins with <c>INDX</c> (the stage index); its <c>QSDT</c> carries the stage flag byte and
    /// <c>CNAM</c> the Pip-Boy log-entry text. (The exact <c>QSDT</c> bit meaning is kept raw — see
    /// <see cref="QuestStageDef.Flags"/>.)</item>
    /// <item>An objective begins with <c>QOBJ</c> (the objective index), <c>NNAM</c> is its display text, and
    /// one or more <c>QSTA</c> name its target reference(s) — the placed refs whose enable-state encodes
    /// objective progress in the save (ROADMAP §6 #10). A <c>QSTA</c>'s leading u32 is the target FormID.</item>
    /// </list>
    /// Parsing is defensive: a malformed/short subrecord is skipped rather than throwing.
    /// </summary>
    private static QuestDefinition ParseQuest(uint formId, List<(string Type, byte[] Data)> subs, bool localized, string? full)
    {
        var stages = new List<QuestStageDef>();
        var objectives = new List<QuestObjectiveDef>();
        byte dataFlags = 0; // QUST DATA byte 0: bit0 = Start Game Enabled (ROADMAP §6 #16)

        int? pendingStage = null;
        byte pendingFlags = 0;
        string? pendingLog = null;

        void FlushStage()
        {
            if (pendingStage is { } idx)
                stages.Add(new QuestStageDef(idx, pendingFlags, pendingLog));
            pendingStage = null;
            pendingFlags = 0;
            pendingLog = null;
        }

        int curObjIndex = 0;
        string? curObjText = null;
        var curTargets = new List<uint>();
        var inObjective = false;

        void FlushObjective()
        {
            if (inObjective)
                objectives.Add(new QuestObjectiveDef(curObjIndex, curObjText, curTargets));
            curObjIndex = 0;
            curObjText = null;
            curTargets = [];
            inObjective = false;
        }

        foreach (var (type, sub) in subs)
        {
            switch (type)
            {
                case "DATA" when sub.Length >= 1:
                    dataFlags = sub[0]; // QUST DATA: byte 0 = quest flags (bit0 = Start Game Enabled)
                    break;
                case "INDX":
                    FlushStage();
                    pendingStage = sub.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(sub) : sub.Length == 1 ? sub[0] : 0;
                    break;
                case "QSDT" when pendingStage is not null && sub.Length >= 1:
                    pendingFlags = sub[0];
                    break;
                case "CNAM" when pendingStage is not null && !localized:
                    pendingLog ??= ZString(sub);
                    break;
                case "QOBJ":
                    FlushStage();
                    FlushObjective();
                    inObjective = true;
                    curObjIndex = sub.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(sub)
                        : sub.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(sub) : 0;
                    break;
                case "NNAM" when inObjective && !localized:
                    curObjText ??= ZString(sub);
                    break;
                case "QSTA" when inObjective && sub.Length >= 4:
                    curTargets.Add(BinaryPrimitives.ReadUInt32LittleEndian(sub)); // leading u32 = target reference FormID
                    break;
            }
        }
        FlushStage();
        FlushObjective();

        return new QuestDefinition(formId, stages, objectives, dataFlags, full);
    }

    /// <summary>R&amp;D (ROADMAP §6 #16): dump the raw subrecords of one <c>QUST</c> record from a plugin file,
    /// matched by <b>plugin-local</b> FormID. Returns each subrecord's signature, length, and (for text fields)
    /// decoded text — used to discover whether FNV masters retain stage result-script <b>source text</b>
    /// (<c>SCTX</c>) and conditions (<c>CTDA</c>) we can statically scan, or only compiled bytecode (<c>SCDA</c>).</summary>
    public static IReadOnlyList<(string Type, int Size, string? Text)> DumpQust(Stream fs, uint localFormId)
    {
        if (ReadSignature(fs) != "TES4")
            throw new SaveFormatException("not a TES4 plugin", 0);
        var headerDataSize = ReadU32(fs);
        ReadExactly(fs, 16); // flags(4) + formId(4) + version-control(8)
        ReadExactly(fs, (int)headerDataSize);

        while (true)
        {
            var gsig = ReadSignature(fs);
            if (gsig is null) break;
            if (gsig != "GRUP") break;
            var groupSize = ReadU32(fs);
            var label = Encoding.ASCII.GetString(ReadExactly(fs, 4));
            ReadU32(fs);
            ReadExactly(fs, 8);
            var contentSize = (long)groupSize - HeaderSize;
            if (label != "QUST") { fs.Seek(contentSize, SeekOrigin.Current); continue; }

            var end = fs.Position + contentSize;
            while (fs.Position < end)
            {
                var sig = ReadSignature(fs);
                if (sig is null) break;
                var size = ReadU32(fs);
                if (sig == "GRUP") { ReadExactly(fs, 16); fs.Seek((long)size - HeaderSize, SeekOrigin.Current); continue; }
                var flags = ReadU32(fs);
                var formId = ReadU32(fs);
                ReadExactly(fs, 8);
                var data = ReadExactly(fs, (int)size);
                if (formId != localFormId) continue;
                if ((flags & CompressedFlag) != 0) data = Decompress(data);
                var result = new List<(string, int, string?)>();
                foreach (var (type, sub) in ParseSubrecords(data))
                {
                    string? text = type is "SCTX" or "CNAM" or "NNAM" or "FULL" or "EDID" ? ZString(sub) : null;
                    result.Add((type, sub.Length, text));
                }
                return result;
            }
            break;
        }
        return [];
    }

    /// <summary>
    /// Splits a record's data block into <c>[type:4][size:u16][data]</c> subrecords. Handles the <c>XXXX</c>
    /// escape, where an <c>XXXX</c> subrecord carries a u32 holding the real (over-u16) size of the next field.
    /// </summary>
    private static List<(string Type, byte[] Data)> ParseSubrecords(byte[] data)
    {
        var result = new List<(string, byte[])>();
        var p = 0;
        var oversize = -1;
        while (p + 6 <= data.Length)
        {
            var type = Encoding.ASCII.GetString(data, p, 4);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4, 2));
            p += 6;
            if (type == "XXXX")
            {
                if (p + 4 <= data.Length)
                    oversize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p, 4));
                p += size;
                continue;
            }
            var realSize = oversize >= 0 ? oversize : size;
            oversize = -1;
            if (realSize < 0 || p + realSize > data.Length)
                realSize = data.Length - p; // truncated/garbled — take what's left rather than throwing
            result.Add((type, data.AsSpan(p, realSize).ToArray()));
            p += realSize;
        }
        return result;
    }

    /// <summary>Inflates a compressed record's data: <c>[u32 decompressedSize][zlib stream]</c>.</summary>
    private static byte[] Decompress(byte[] data)
    {
        if (data.Length < 4)
            return data;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data);
        using var src = new MemoryStream(data, 4, data.Length - 4);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        var outBuf = new byte[size];
        var read = 0;
        while (read < outBuf.Length)
        {
            var r = z.Read(outBuf, read, outBuf.Length - read);
            if (r <= 0) break;
            read += r;
        }
        return outBuf;
    }

    /// <summary>Reads a zero-terminated Latin1/Windows-1252 string (names/editor ids are stored this way).</summary>
    private static string ZString(byte[] data)
    {
        var n = Array.IndexOf(data, (byte)0);
        if (n < 0) n = data.Length;
        return Encoding.Latin1.GetString(data, 0, n);
    }

    // ---- stream primitives (little-endian; throw SaveFormatException with offset on truncation) ----

    private static string? ReadSignature(Stream s)
    {
        var buf = new byte[4];
        var n = s.Read(buf, 0, 4);
        if (n == 0) return null; // clean EOF
        if (n < 4) throw new SaveFormatException("truncated 4-byte signature", (int)s.Position);
        return Encoding.ASCII.GetString(buf);
    }

    private static uint ReadU32(Stream s) => BinaryPrimitives.ReadUInt32LittleEndian(ReadExactly(s, 4));

    private static byte[] ReadExactly(Stream s, int count)
    {
        if (count < 0)
            throw new SaveFormatException($"negative length {count}", (int)s.Position);
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var r = s.Read(buf, off, count - off);
            if (r <= 0)
                throw new SaveFormatException($"unexpected EOF: needed {count} byte(s)", (int)s.Position);
            off += r;
        }
        return buf;
    }
}

/// <summary>One stage of a <c>QUST</c> definition: its <paramref name="Index"/> (the <c>INDX</c> stage index),
/// the raw <c>QSDT</c> <paramref name="Flags"/> byte (kept raw — its completion/fail bit semantics are not yet
/// FNV-verified, ROADMAP §6 #10), and the Pip-Boy log-entry text (<c>CNAM</c>), or null when none.</summary>
public sealed record QuestStageDef(int Index, byte Flags, string? LogText);

/// <summary>One objective of a <c>QUST</c> definition: its <paramref name="Index"/> (<c>QOBJ</c>), display
/// <paramref name="Text"/> (<c>NNAM</c>), and the FormIDs of its target reference(s) (<c>QSTA</c>) — the placed
/// refs whose save-side enable-state encodes whether the objective is active (ROADMAP §6 #10).</summary>
public sealed record QuestObjectiveDef(int Index, string? Text, IReadOnlyList<uint> TargetFormIds);

/// <summary>A <c>QUST</c> record's decoded structure: its FormID (plugin-local as read from a
/// <see cref="TesPlugin"/>, re-keyed into save space by <see cref="PluginDatabase"/>) plus its stages and
/// objectives. This is the static quest <i>definition</i> from the masters; the player's progress against it
/// lives in the save's change forms (read by <c>QuestLog</c>).
/// <para><see cref="DataFlags"/> is the <c>DATA</c> subrecord's first byte (FO3/FNV quest flags): bit0
/// <c>0x01</c> = <b>Start Game Enabled</b> (the engine starts the quest at game load), bit2 <c>0x04</c> =
/// Allow repeated conversation topics, bit3 <c>0x08</c> = Allow repeated stages. <see cref="Name"/> is the
/// quest's <c>FULL</c> display name (null when the quest has none — a dialogue/script container that never
/// appears in the Pip-Boy); a player-facing quest has a name <i>and</i> objectives (ROADMAP §6 #16).</para></summary>
public sealed record QuestDefinition(
    uint FormId, IReadOnlyList<QuestStageDef> Stages, IReadOnlyList<QuestObjectiveDef> Objectives,
    byte DataFlags = 0, string? Name = null)
{
    /// <summary>The quest's <c>DATA</c> "Start Game Enabled" flag (bit0): the engine starts these at game load,
    /// so a Start-Game-Enabled quest with a displayed objective shows in the Pip-Boy with no save delta.</summary>
    public bool StartGameEnabled => (DataFlags & 0x01) != 0;

    /// <summary>A player-facing quest carries a display name and at least one objective; only these can appear in
    /// the Pip-Boy Quests list (dialogue/script-only quests have neither). The first-order Pip-Boy filter.</summary>
    public bool IsPlayerFacing => !string.IsNullOrEmpty(Name) && Objectives.Count > 0;
}
