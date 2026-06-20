using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FnvSaveExplorer.Core;

/// <summary>
/// A minimal, read-only reader for a Bethesda TES4-generation plugin (<c>.esm</c>/<c>.esp</c>) as used
/// by Fallout 3 / New Vegas. It extracts just what FormID → display-name resolution needs: the ordered
/// master list (from the <c>TES4</c> header's <c>MAST</c> subrecords) and, for item-bearing record types,
/// each form's FormID and display name (the <c>FULL</c> field, falling back to the editor id <c>EDID</c>).
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

    /// <summary>The plugin's file name (e.g. <c>FalloutNV.esm</c>).</summary>
    public string FileName { get; }

    /// <summary>The plugin's masters, in order — index <c>i</c> is the plugin-local high byte that refers to it.</summary>
    public IReadOnlyList<string> Masters { get; }

    /// <summary>
    /// Named item forms, keyed by <b>plugin-local</b> FormID (its high byte is an index into
    /// <see cref="Masters"/>, or == <c>Masters.Count</c> for forms the plugin defines itself). <c>Type</c>
    /// is the record signature (<c>WEAP</c>/<c>ARMO</c>/<c>ALCH</c>/<c>AMMO</c>/<c>MISC</c>/…) — the basis
    /// for the item's Pip-Boy tab (see <see cref="PluginDatabase.PipBoyTab"/>).
    /// </summary>
    public IReadOnlyList<(uint LocalFormId, string Name, string Type)> Forms { get; }

    private TesPlugin(string fileName, IReadOnlyList<string> masters, IReadOnlyList<(uint, string, string)> forms)
    {
        FileName = fileName;
        Masters = masters;
        Forms = forms;
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
        var forms = new List<(uint, string, string)>();
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
            if (groupType == 0 && ItemTypes.Contains(labelType))
                ReadRecords(fs, contentSize, localized, forms);
            else
                fs.Seek(contentSize, SeekOrigin.Current); // skip the whole group without reading its bytes
        }

        return new TesPlugin(fileName, masters, forms);
    }

    /// <summary>Reads the records (and defensively skips any nested groups) inside one top-level item group.</summary>
    private static void ReadRecords(Stream fs, long contentSize, bool localized, List<(uint, string, string)> forms)
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

            string? edid = null, full = null;
            foreach (var (type, sub) in ParseSubrecords(data))
            {
                if (type == "EDID") edid = ZString(sub);
                else if (type == "FULL" && !localized) full = ZString(sub);
            }
            var name = full ?? edid;
            if (!string.IsNullOrEmpty(name))
                forms.Add((formId, name, sig)); // sig is the record type (WEAP/ARMO/ALCH/AMMO/MISC/…)
        }
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
