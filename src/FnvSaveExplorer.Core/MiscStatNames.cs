namespace FnvSaveExplorer.Core;

/// <summary>
/// Names for the positional Misc Stats counters (global-data type 0). The save stores only values,
/// addressed by index (<see cref="MiscStat.Index"/>); the engine maps each index to a counter name.
/// This is the fixed FO3/FNV misc-stat array order — the engine's own slot names, so a few are
/// vestigial in FNV (e.g. "Bobbleheads Found"), but the label matches what the save actually stores.
/// </summary>
/// <remarks>
/// Order verified against the real-save corpus (ROADMAP §6.8 / §7): there are exactly 43 counters,
/// index 35 "Total Things Killed" equals index 2 "People Killed" + index 3 "Creatures Killed" on every
/// save, and index 39 "Barter Amount Traded" is the large, fast-growing caps total — both anchor the
/// alignment. Source: the FNV <c>MiscStatEnum</c> (matortheeternal/esp.json), cross-checked against
/// slfx77/fallout-xbox-360-utils' save-statistics decoder.
/// </remarks>
public static class MiscStatNames
{
    private static readonly string[] Names =
    [
        "Quests Completed",            // 0
        "Locations Discovered",        // 1
        "People Killed",               // 2
        "Creatures Killed",            // 3
        "Locks Picked",                // 4
        "Computers Hacked",            // 5
        "Stimpaks Taken",              // 6
        "Rad-X Taken",                 // 7
        "RadAway Taken",               // 8
        "Chems Taken",                 // 9
        "Times Addicted",              // 10
        "Mines Disarmed",              // 11
        "Speech Successes",            // 12
        "Pockets Picked",              // 13
        "Pants Exploded",              // 14
        "Books Read",                  // 15
        "Bobbleheads Found",           // 16
        "Weapons Created",             // 17
        "People Mezzed",               // 18
        "Captives Rescued",            // 19
        "Sandman Kills",               // 20
        "Paralyzing Punches",          // 21
        "Robots Disabled",             // 22
        "Contracts Completed",         // 23
        "Corpses Eaten",               // 24
        "Mysterious Stranger Visits",  // 25
        "Doctor Bags Used",            // 26
        "Challenges Completed",        // 27
        "Miss Fortunate Occurrences",  // 28
        "Disintegrations",             // 29
        "Have Limbs Crippled",         // 30
        "Speech Failures",             // 31
        "Items Crafted",               // 32
        "Weapon Modifications",        // 33
        "Items Repaired",              // 34
        "Total Things Killed",         // 35
        "Dismembered Limbs",           // 36
        "Caravan Games Won",           // 37
        "Caravan Games Lost",          // 38
        "Barter Amount Traded",        // 39
        "Roulette Games Played",       // 40
        "Blackjack Games Played",      // 41
        "Slots Games Played",          // 42
    ];

    /// <summary>The number of misc-stat indices with a known name (43 for FO3/FNV).</summary>
    public static int Count => Names.Length;

    /// <summary>
    /// The counter name for a misc-stat index, or <c>null</c> when the index is outside the known
    /// range (the record's count is read from the save and is not assumed to be 43).
    /// </summary>
    public static string? Get(int index) =>
        index >= 0 && index < Names.Length ? Names[index] : null;
}
