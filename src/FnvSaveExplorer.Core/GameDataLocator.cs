namespace FnvSaveExplorer.Core;

/// <summary>
/// Locates the Fallout: New Vegas <c>Data</c> folder (where the ESM/ESP masters live) so FormIDs can be
/// resolved to display names. It tries an explicit override first, then the known local install path and a
/// few common Steam/GOG/Bethesda locations, returning the first folder that exists and contains
/// <see cref="MasterEsm"/>. Returns <c>null</c> when none is found, so callers gracefully fall back to
/// showing raw-hex FormIDs.
/// </summary>
public static class GameDataLocator
{
    /// <summary>The base-game master; its presence marks a valid Data folder.</summary>
    public const string MasterEsm = "FalloutNV.esm";

    public static string? FindDataFolder(string? overridePath = null)
    {
        foreach (var candidate in Candidates(overridePath))
            if (HasMaster(candidate))
                return candidate;
        return null;
    }

    /// <summary>
    /// Locates a Mod Organizer 2 <c>mods\</c> folder for a save loaded from an MO2 profile. MO2 stores saves
    /// at <c>&lt;root&gt;\profiles\&lt;profile&gt;\saves\</c> and mods at <c>&lt;root&gt;\mods\</c>, so the mods folder
    /// is derived from the save's path. An explicit override (the mods folder, or the MO2 root) is honored
    /// first. Returns <c>null</c> when the save isn't inside an MO2 profile and no valid override is given.
    /// </summary>
    public static string? FindMo2Mods(string? savePath, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (Directory.Exists(Path.Combine(overridePath, "mods")))
                return Path.Combine(overridePath, "mods"); // pointed at the MO2 root
            return Directory.Exists(overridePath) ? overridePath : null; // pointed at the mods folder itself
        }

        if (string.IsNullOrWhiteSpace(savePath))
            return null;
        try
        {
            // <root>\profiles\<profile>\saves\<save>.fos  ->  <root>\mods
            var savesDir = Path.GetDirectoryName(Path.GetFullPath(savePath));
            if (savesDir is null || !Eq(Path.GetFileName(savesDir), "saves"))
                return null;
            var profilesDir = Path.GetDirectoryName(Path.GetDirectoryName(savesDir));
            if (profilesDir is null || !Eq(Path.GetFileName(profilesDir), "profiles"))
                return null;
            var root = Path.GetDirectoryName(profilesDir);
            var mods = root is null ? null : Path.Combine(root, "mods");
            return mods is not null && Directory.Exists(mods) ? mods : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool HasMaster(string folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, MasterEsm));

    private static IEnumerable<string> Candidates(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            // Accept either the Data folder itself or the game root that contains it.
            yield return overridePath;
            yield return Path.Combine(overridePath, "Data");
        }

        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string?[] roots =
        [
            @"C:\Games\Steam\steamapps\common\Fallout New Vegas",        // known local install (project notes)
            @"C:\Games\Steam\steamapps\common\Fallout New Vegas English",
            Join(x86, "Steam", "steamapps", "common", "Fallout New Vegas"),
            Join(x86, "Steam", "steamapps", "common", "Fallout New Vegas English"),
            Join(pf,  "Steam", "steamapps", "common", "Fallout New Vegas"),
            Join(x86, "GOG Galaxy", "Games", "Fallout New Vegas"),
            Join(x86, "GalaxyClient", "Games", "Fallout New Vegas"),
            Join(x86, "Bethesda.net Launcher", "games", "Fallout New Vegas"),
        ];
        foreach (var root in roots)
            if (!string.IsNullOrEmpty(root))
                yield return Path.Combine(root, "Data");
    }

    private static string? Join(string root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine([root, .. parts]);
}
