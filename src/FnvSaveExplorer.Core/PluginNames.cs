using System.Text;

namespace FnvSaveExplorer.Core;

/// <summary>
/// Maps a plugin (ESM/ESP) file name to a human-friendly display name. FO3/FNV plugins carry **no**
/// content-name field — the TES4 header holds only author + masters + overrides, and the friendly DLC
/// names only survive incidentally inside gameplay <c>MESG</c> records (with no consistent EDID/FormID
/// convention), so there is nothing uniform to read. The in-game "Downloadable Content" menu instead
/// shows the engine's built-in known-content names (a mod <c>.esm</c> gets no friendly name there). This
/// table mirrors that: the official Fallout: New Vegas files map to their exact menu names (verified
/// against the ESMs), and anything else falls back to its file name with the extension stripped and
/// PascalCase split into words.
/// </summary>
public static class PluginNames
{
    private static readonly Dictionary<string, string> Official = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FalloutNV.esm"]         = "Fallout: New Vegas",
        ["DeadMoney.esm"]         = "Dead Money",
        ["HonestHearts.esm"]      = "Honest Hearts",
        ["OldWorldBlues.esm"]     = "Old World Blues",
        ["LonesomeRoad.esm"]      = "Lonesome Road",
        ["GunRunnersArsenal.esm"] = "Gun Runners' Arsenal",
        ["ClassicPack.esm"]       = "Classic Pack",
        ["MercenaryPack.esm"]     = "Mercenary Pack",
        ["TribalPack.esm"]        = "Tribal Pack",
        ["CaravanPack.esm"]       = "Caravan Pack",
    };

    /// <summary>The friendly display name for a plugin file name (official table, else a prettified stem).</summary>
    public static string Friendly(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return fileName;
        return Official.TryGetValue(fileName, out var name) ? name : Prettify(fileName);
    }

    /// <summary>Strips the extension and splits a PascalCase/CamelCase stem into words (best-effort, for mods).</summary>
    private static string Prettify(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var sb = new StringBuilder(stem.Length + 8);
        for (var i = 0; i < stem.Length; i++)
        {
            var c = stem[i];
            if (i > 0)
            {
                var prev = stem[i - 1];
                var boundary =
                    (char.IsLower(prev) && char.IsUpper(c)) ||                                                  // aB  -> a B
                    (char.IsLetter(prev) && char.IsDigit(c)) ||                                                 // a1  -> a 1
                    (char.IsUpper(prev) && char.IsUpper(c) && i + 1 < stem.Length && char.IsLower(stem[i + 1])); // ABc -> A Bc (acronym)
                if (boundary)
                    sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
