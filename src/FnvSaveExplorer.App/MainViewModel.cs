using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.App;

public sealed class FltRow
{
    public int Index { get; init; }
    public uint Value { get; init; }
    public string Hex => $"0x{Value:X8}";
}

public sealed class MiscStatRow
{
    public int Index { get; init; }
    public string? Name { get; init; }
    public uint Value { get; init; }
}

/// <summary>One editable SPECIAL attribute row for the GUI grid.</summary>
public sealed class SpecialAttr : INotifyPropertyChanged
{
    public required string Name { get; init; }

    private byte _value;
    public byte Value
    {
        get => _value;
        set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class SkillRow : INotifyPropertyChanged
{
    public required string Name { get; init; }

    private float _value;
    public float Value
    {
        get => _value;
        set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class InventoryRow : INotifyPropertyChanged
{
    public uint FormId { get; init; }
    public int ModIndex { get; init; }
    public uint OriginalCount { get; init; }
    public string Item => $"0x{FormId:X8} (mod {ModIndex:X2})";

    /// <summary>The plugin (ESM/ESP) the item's mod index points at — its source mod/DLC.</summary>
    public string Source { get; init; } = "";

    /// <summary>The Pip-Boy tab the item appears under (Weapons / Apparel / Aid / Ammo / Misc / Notes),
    /// derived from the base form's record type; empty when names aren't resolved.</summary>
    public string Category { get; init; } = "";

    /// <summary>The base-form record signature (WEAP/ARMO/ALCH/AMMO/MISC/…), or empty if unknown.</summary>
    public string RecordType { get; init; } = "";

    /// <summary>Tab + record type for display, e.g. "Aid (BOOK)".</summary>
    public string Tab => string.IsNullOrEmpty(RecordType) ? Category : $"{Category} ({RecordType})";

    private string _name = "";
    /// <summary>Display name resolved from the game masters; empty until/unless a Data folder is found.</summary>
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
    }

    private uint _count;
    public uint Count
    {
        get => _count;
        set { if (_count != value) { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count))); } }
    }

    /// <summary>True when the stack is equipped/worn (read-only display).</summary>
    public bool Equipped { get; init; }

    /// <summary>The stack's condition when loaded, or null if it carries no condition extra-data.</summary>
    public float? OriginalCondition { get; init; }

    private float? _condition;
    /// <summary>The stack's editable condition/health (a same-length float splice), or null for stacks
    /// (ammo/aid/misc) that carry none — those reject edits.</summary>
    public float? Condition
    {
        get => _condition;
        set { if (!Nullable.Equals(_condition, value)) { _condition = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Condition))); } }
    }

    /// <summary>The base-form max condition (WEAP/ARMO DATA health, ROADMAP §6 #4), or null for items that
    /// carry no condition cap. Read-only display — the scale the editable <see cref="Condition"/> is measured
    /// against.</summary>
    public int? MaxCondition { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One note in the player's Pip-Boy Data → Notes list; read-only display (ROADMAP §4k / §4k.1 #4).</summary>
public sealed class NoteRow
{
    public uint FormId { get; init; }
    public int ModIndex { get; init; }
    public string Note => $"0x{FormId:X8} (mod {ModIndex:X2})";

    /// <summary>"Read" once viewed (a marker exists) or "Unread" (acquired but never opened — bold in-game).</summary>
    public bool Read { get; init; }
    public string Status => Read ? "Read" : "Unread";

    /// <summary>The note's media type — Text / Voice / Sound / Image — read from the base form (§4k.1 #6).</summary>
    public string Media { get; init; } = "";

    /// <summary>Display name resolved from the game masters; empty until/unless a Data folder is found.</summary>
    public string Name { get; init; } = "";

    /// <summary>The plugin (ESM/ESP) the note's mod index points at — its source mod/DLC.</summary>
    public string Source { get; init; } = "";
}

/// <summary>One of the player's perks or traits; read-only display (ROADMAP §4n).</summary>
public sealed class PerkRow
{
    public uint FormId { get; init; }
    public int ModIndex { get; init; }
    public string Form => $"0x{FormId:X8} (mod {ModIndex:X2})";
    public string Name { get; init; } = "";
    public int Rank { get; init; }
    public string RankText => Rank > 1 ? Rank.ToString() : "";
    public string Source { get; init; } = "";
}

/// <summary>One faction's reputation (fame/infamy); read-only display (ROADMAP §4o).</summary>
public sealed class ReputationRow
{
    public uint FormId { get; init; }
    public string Faction { get; init; } = "";
    public float Fame { get; init; }
    public float Infamy { get; init; }
    public string Form => $"0x{FormId:X8}";
}

/// <summary>One GlobalData type-3 global variable (ROADMAP §4c): a named <c>GLOB</c> and its editable float
/// value (a same-length splice). Mirrors <see cref="SkillRow"/> — <see cref="Value"/> is two-way bound.</summary>
public sealed class GlobalRow : INotifyPropertyChanged
{
    public uint FormId { get; init; }
    public string Form => $"0x{FormId:X8}";

    /// <summary>The GLOB editor id (e.g. <c>GameDaysPassed</c>), or empty when names aren't resolved.</summary>
    public string Name { get; init; } = "";

    public float OriginalValue { get; init; }

    private float _value;
    public float Value
    {
        get => _value;
        set { if (_value != value) { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One quest in the read-only quest-log view (ROADMAP §6 #10). Its decoded stages and objectives
/// are flattened into <see cref="Lines"/> for the master-detail row, with summary columns alongside.</summary>
public sealed class QuestRow
{
    public uint FormId { get; init; }
    public string FormIdHex => $"0x{FormId:X8}";
    public string Name { get; init; } = "";

    /// <summary>Active / Completed / Failed — see <see cref="PipboyQuestState"/>.</summary>
    public string State { get; init; } = "";

    /// <summary>Displayed objective count and how many are ticked (the Pip-Boy task list).</summary>
    public int StageCount { get; init; }
    public int DoneStageCount { get; init; }

    /// <summary>"2/4 done" — completed vs displayed objectives — or blank when the quest shows none.</summary>
    public string Progress => StageCount > 0 ? $"{DoneStageCount}/{StageCount} done" : "";

    /// <summary>The quest's displayed objectives, shown in the expandable row-details panel.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private FalloutSave? _save;
    private string? _loadedPath;

    public ObservableCollection<string> Plugins { get; } = [];
    public ObservableCollection<FltRow> FileLocationTable { get; } = [];
    public ObservableCollection<SpecialAttr> Special { get; } = [];
    public ObservableCollection<SkillRow> Skills { get; } = [];
    public ObservableCollection<InventoryRow> Inventory { get; } = [];
    public ObservableCollection<NoteRow> Notes { get; } = [];
    public ObservableCollection<PerkRow> Perks { get; } = [];
    public ObservableCollection<ReputationRow> Reputation { get; } = [];
    public ObservableCollection<GlobalRow> Globals { get; } = [];
    public ObservableCollection<QuestRow> Quests { get; } = [];
    public ObservableCollection<MiscStatRow> MiscStats { get; } = [];

    private static readonly string[] SpecialNames =
        ["Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck"];

    // ---- Display state -----------------------------------------------------
    private string _windowTitle = "FNV Save Explorer";
    public string WindowTitle { get => _windowTitle; private set => Set(ref _windowTitle, value); }

    private string _status = "Open a .fos save to begin.";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private ImageSource? _screenshot;
    public ImageSource? Screenshot { get => _screenshot; private set => Set(ref _screenshot, value); }

    private bool _hasSave;
    public bool HasSave { get => _hasSave; private set => Set(ref _hasSave, value); }

    private string _playerName = "";
    public string PlayerName { get => _playerName; private set => Set(ref _playerName, value); }

    private string _playerTitle = "";
    public string PlayerTitle { get => _playerTitle; private set => Set(ref _playerTitle, value); }

    private string _language = "";
    public string Language { get => _language; private set => Set(ref _language, value); }

    private string _location = "";
    public string Location { get => _location; private set => Set(ref _location, value); }

    private string _playtime = "";
    public string Playtime { get => _playtime; private set => Set(ref _playtime, value); }

    private uint _level;
    public uint Level { get => _level; private set => Set(ref _level, value); }

    private uint _saveNumber;
    public uint SaveNumber { get => _saveNumber; private set => Set(ref _saveNumber, value); }

    private string _bodyInfo = "";
    public string BodyInfo { get => _bodyInfo; private set => Set(ref _bodyInfo, value); }

    private string _skillsInfo = "";
    public string SkillsInfo { get => _skillsInfo; private set => Set(ref _skillsInfo, value); }

    private string _inventoryInfo = "";
    public string InventoryInfo { get => _inventoryInfo; private set => Set(ref _inventoryInfo, value); }

    private string _notesInfo = "";
    public string NotesInfo { get => _notesInfo; private set => Set(ref _notesInfo, value); }

    private string _perksInfo = "";
    public string PerksInfo { get => _perksInfo; private set => Set(ref _perksInfo, value); }

    private string _reputationInfo = "";
    public string ReputationInfo { get => _reputationInfo; private set => Set(ref _reputationInfo, value); }

    private string _globalsInfo = "";
    public string GlobalsInfo { get => _globalsInfo; private set => Set(ref _globalsInfo, value); }

    private string _questsInfo = "";
    public string QuestsInfo { get => _questsInfo; private set => Set(ref _questsInfo, value); }

    // ---- Edit fields (two-way bound) --------------------------------------
    private string _editName = "";
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    private string _editLevel = "";
    public string EditLevel { get => _editLevel; set => Set(ref _editLevel, value); }

    private string _editSaveNumber = "";
    public string EditSaveNumber { get => _editSaveNumber; set => Set(ref _editSaveNumber, value); }

    private string _editCaps = "";
    /// <summary>The player's caps (the <c>0x0000000F</c> inventory stack, §6.4). Blank when the save carries
    /// no caps stack; editing it is a same-length count splice via <see cref="FalloutSave.TrySetCaps"/>.</summary>
    public string EditCaps { get => _editCaps; set => Set(ref _editCaps, value); }

    private string _editKarma = "";
    /// <summary>The player's karma (a float in the player reference's actor-value array, §4j). Blank when
    /// not locatable; edited same-length via <see cref="FalloutSave.TrySetKarma"/>.</summary>
    public string EditKarma { get => _editKarma; set => Set(ref _editKarma, value); }

    private string _editXp = "";
    /// <summary>The player's XP (a float, §4j). Blank when not locatable; edited same-length via
    /// <see cref="FalloutSave.TrySetXp"/>.</summary>
    public string EditXp { get => _editXp; set => Set(ref _editXp, value); }

    private string _editDataFolder = "";
    /// <summary>Optional override for the game Data folder used to resolve item names.</summary>
    public string EditDataFolder { get => _editDataFolder; set => Set(ref _editDataFolder, value); }

    private string _editModsFolder = "";
    /// <summary>Optional Mod Organizer 2 <c>mods\</c> folder (or MO2 root) for resolving mod item names.</summary>
    public string EditModsFolder { get => _editModsFolder; set => Set(ref _editModsFolder, value); }

    // ---- Operations --------------------------------------------------------
    public void Load(string path)
    {
        try
        {
            var save = FalloutSave.Load(path);
            _save = save;
            _loadedPath = path;

            PlayerName = save.PlayerName;
            PlayerTitle = save.PlayerTitle;
            Language = save.Language;
            Location = save.PlayerLocation;
            Playtime = save.Playtime;
            Level = save.PlayerLevel;
            SaveNumber = save.SaveNumber;

            EditName = save.PlayerName;
            EditLevel = save.PlayerLevel.ToString();
            EditSaveNumber = save.SaveNumber.ToString();
            EditCaps = save.Caps?.ToString() ?? "";
            EditKarma = save.Karma?.ToString("0.###") ?? "";
            EditXp = save.Xp?.ToString("0.###") ?? "";

            Screenshot = BuildScreenshot(save.Screenshot);

            Plugins.Clear();
            foreach (var p in save.Plugins)
                Plugins.Add(p);

            FileLocationTable.Clear();
            var flt = save.PeekBodyUInt32(28);
            for (var i = 0; i < flt.Length; i++)
                FileLocationTable.Add(new FltRow { Index = i, Value = flt[i] });

            Special.Clear();
            if (save.Special is { } sp)
                for (var i = 0; i < SpecialNames.Length; i++)
                    Special.Add(new SpecialAttr { Name = SpecialNames[i], Value = sp.Values[i] });

            Skills.Clear();
            if (save.Skills is { } sk)
            {
                foreach (var s in sk.Skills.OrderBy(s => s.Name))
                    Skills.Add(new SkillRow { Name = s.Name, Value = s.Value });
                SkillsInfo = "Stored skill modifications (actor-value entries). Only skills the game has " +
                             "modified from their computed base are stored, so this may be a subset; values " +
                             "edit in place (same-length).";
            }
            else
            {
                SkillsInfo = "This save stores no editable skill modifications. The engine computes skills " +
                             "from base + SPECIAL + perks and only writes deviations (e.g. from a console " +
                             "setav, an implant, or certain effects), of which this save has fewer than two.";
            }

            var mods = GameDataLocator.FindMo2Mods(path, string.IsNullOrWhiteSpace(EditModsFolder) ? null : EditModsFolder);
            // withDialogue: the Pip-Boy quest interpreter needs INFO result scripts (Phase B) — otherwise
            // dialogue-started quests (e.g. Ghost Town Gunfight) are missing on first load. ~120 ms once.
            var invDb = PluginDatabase.ForSave(save, string.IsNullOrWhiteSpace(EditDataFolder) ? null : EditDataFolder, mods, withDialogue: true);
            if (invDb.DataFolder is not null && string.IsNullOrWhiteSpace(EditDataFolder))
                EditDataFolder = invDb.DataFolder;
            if (invDb.ModsFolder is not null && string.IsNullOrWhiteSpace(EditModsFolder))
                EditModsFolder = invDb.ModsFolder;
            PopulateInventory(save, invDb);
            PopulateNotes(save, invDb);
            PopulatePerks(save, invDb);
            PopulateReputation(save, invDb);
            PopulateGlobals(save, invDb);
            PopulateQuests(save, invDb);

            MiscStats.Clear();
            if (save.MiscStats is { } ms)
                foreach (var stat in ms.Stats)
                    MiscStats.Add(new MiscStatRow { Index = stat.Index, Name = MiscStatNames.Get(stat.Index), Value = stat.Value });

            var bodyBytes = save.FileLength - save.BodyOffset;
            BodyInfo =
                $"Body begins at offset 0x{save.BodyOffset:X} ({save.BodyOffset:N0}).\n" +
                $"{bodyBytes:N0} bytes of globals, change forms and the FormID array follow — " +
                "preserved verbatim and not yet decoded.";

            HasSave = true;
            WindowTitle = $"FNV Save Explorer — {Path.GetFileName(path)}";
            Status = $"Loaded {Path.GetFileName(path)} ({save.FileLength:N0} bytes).";
        }
        catch (SaveFormatException ex)
        {
            Status = $"Could not parse save: {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-resolves inventory item names using the current <see cref="EditDataFolder"/> override (or
    /// auto-detection when it's blank), updating rows in place so staged count edits are preserved.
    /// </summary>
    public void ReresolveNames()
    {
        if (_save is null)
            return;
        var mods = GameDataLocator.FindMo2Mods(_loadedPath, string.IsNullOrWhiteSpace(EditModsFolder) ? null : EditModsFolder);
        // withDialogue: the Pip-Boy quest interpreter needs INFO result scripts (Phase B); ~120 ms once on load.
        var db = PluginDatabase.ForSave(_save, string.IsNullOrWhiteSpace(EditDataFolder) ? null : EditDataFolder, mods, withDialogue: true);
        if (db.DataFolder is not null)
            EditDataFolder = db.DataFolder;
        if (db.ModsFolder is not null)
            EditModsFolder = db.ModsFolder;
        PopulateInventory(_save, db);
        PopulateNotes(_save, db);
        PopulatePerks(_save, db);
        PopulateReputation(_save, db);
        PopulateGlobals(_save, db);
        PopulateQuests(_save, db);
    }

    /// <summary>
    /// Fills the inventory grid from the save. When item names are available, entries that don't resolve to
    /// an item are hidden — on large records the decoder's run can absorb a few non-item bytes that validate
    /// structurally but aren't real stacks; offline (no names), everything is shown so nothing is lost.
    /// </summary>
    private void PopulateInventory(FalloutSave save, PluginDatabase db)
    {
        Inventory.Clear();
        if (save.Inventory is not { } inv)
        {
            InventoryInfo = "Inventory not located in this save (no change form parsed as a recognisable item list).";
            return;
        }
        var items = db.Count > 0 ? inv.Items.Where(i => db.Resolve(i.FormId) is not null) : inv.Items;
        foreach (var item in items.OrderByDescending(i => i.Count))
            Inventory.Add(new InventoryRow
            {
                FormId = item.FormId,
                ModIndex = item.ModIndex,
                Name = db.Resolve(item.FormId) ?? "",
                Source = save.FriendlySourceForModIndex(item.ModIndex) ?? "",
                Category = db.Category(item.FormId) ?? "",
                RecordType = db.RecordType(item.FormId) ?? "",
                Count = item.Count,
                OriginalCount = item.Count,
                Equipped = item.Equipped,
                Condition = item.Condition,
                OriginalCondition = item.Condition,
                MaxCondition = db.ItemHealthMax(item.FormId),
            });
        InventoryInfo = DescribeInventory(Inventory.Count, db);
    }

    /// <summary>Fills the Notes grid with the player's full Pip-Boy Data → Notes list — read <em>and</em> unread
    /// (§4k / §4k.1 #4) — resolving names from the masters. The unread set needs the masters (it's identified by
    /// which references in the player inventory record are NOTE records), so without a Data folder we fall back to
    /// just the read markers. Read-only — toggling read state is length-changing.</summary>
    private void PopulateNotes(FalloutSave save, PluginDatabase db)
    {
        Notes.Clear();
        if (db.Count > 0)
        {
            var notes = save.PipBoyNotes(fid => db.RecordType(fid) == "NOTE");
            foreach (var n in notes
                         .OrderBy(n => n.Read)                                   // unread first
                         .ThenBy(n => db.Resolve(n.FormId) ?? "￿", StringComparer.OrdinalIgnoreCase))
                Notes.Add(new NoteRow
                {
                    FormId = n.FormId,
                    ModIndex = n.ModIndex,
                    Read = n.Read,
                    Media = db.NoteMediaType(n.FormId) ?? "",
                    Name = db.Resolve(n.FormId) ?? "",
                    Source = save.FriendlySourceForModIndex(n.ModIndex) ?? "",
                });
            var unread = Notes.Count(r => !r.Read);
            NotesInfo = $"{Notes.Count} note(s) — {Notes.Count - unread} read, {unread} unread (Pip-Boy Data → Notes). " +
                        "Read-only — toggling read state is a length-changing edit.";
        }
        else
        {
            foreach (var n in save.ReadNotes.Notes)
                Notes.Add(new NoteRow { FormId = n.FormId, ModIndex = n.ModIndex, Read = true });
            NotesInfo = $"{Notes.Count} note(s) read. Names + the unread list need the game's Data folder " +
                        "(FalloutNV.esm) — set it on the Edit tab.";
        }
    }

    /// <summary>Fills the perks/traits grid (ROADMAP §4n). Identifying PERK forms needs the masters; without
    /// them the list can't be resolved (PERK refs are indistinguishable from other refs in the player record).</summary>
    private void PopulatePerks(FalloutSave save, PluginDatabase db)
    {
        Perks.Clear();
        if (db.Count == 0)
        {
            PerksInfo = "Perks + traits need the game's Data folder (FalloutNV.esm) to identify/name PERK forms " +
                        "— set it on the Edit tab.";
            return;
        }
        foreach (var p in save.PlayerPerks(fid => db.RecordType(fid) == "PERK")
                     .OrderBy(p => db.Resolve(p.FormId) ?? "￿", StringComparer.OrdinalIgnoreCase))
            Perks.Add(new PerkRow
            {
                FormId = p.FormId,
                ModIndex = (int)(p.FormId >> 24),
                Name = db.Resolve(p.FormId) ?? "",
                Rank = p.Rank,
                Source = save.FriendlySourceForModIndex((int)(p.FormId >> 24)) ?? "",
            });
        PerksInfo = $"{Perks.Count} perk(s) + trait(s) (FNV stores traits as PERK forms). Read-only — adding a " +
                    "perk is a length-changing edit.";
    }

    /// <summary>Fills the reputation grid (ROADMAP §4o). Identifying REPU forms needs the masters.</summary>
    private void PopulateReputation(FalloutSave save, PluginDatabase db)
    {
        Reputation.Clear();
        if (db.Count == 0)
        {
            ReputationInfo = "Faction reputation needs the game's Data folder (FalloutNV.esm) to identify/name " +
                             "REPU factions — set it on the Edit tab.";
            return;
        }
        foreach (var r in save.Reputations(fid => db.RecordType(fid) == "REPU")
                     .OrderByDescending(r => r.Fame + r.Infamy))
            Reputation.Add(new ReputationRow
            {
                FormId = r.FactionFormId,
                Faction = db.Resolve(r.FactionFormId) ?? "",
                Fame = r.Fame,
                Infamy = r.Infamy,
            });
        ReputationInfo = $"{Reputation.Count} faction(s) with standing (fame / infamy, 0–100). Editable via the " +
                         "CLI (setreputation); a faction with both 0 shows no standing in the Pip-Boy.";
    }

    /// <summary>Fills the Globals grid from GlobalData type 3 (ROADMAP §4c). Named (<c>GLOB</c> editor id) rows
    /// sort first; each value is an editable same-length float splice (edit a value and Apply, then Save As).</summary>
    private void PopulateGlobals(FalloutSave save, PluginDatabase db)
    {
        Globals.Clear();
        var vars = save.GlobalVariables();
        foreach (var v in vars
                     .Select(v => (v, Name: db.Resolve(v.FormId) ?? ""))
                     .OrderByDescending(x => x.Name.Length > 0)        // named first
                     .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.v.FormId))
            Globals.Add(new GlobalRow
            {
                FormId = v.v.FormId,
                Name = v.Name,
                OriginalValue = v.v.Value,
                Value = v.v.Value,
            });
        var named = Globals.Count(g => g.Name.Length > 0);
        GlobalsInfo = vars.Count == 0
            ? "This save has no Global Variables table (type 3)."
            : $"{Globals.Count} global variable(s){(db.Count == 0 ? " — set the Data folder on the Edit tab to name them (GLOB editor ids)." : $", {named} named")}. " +
              "Edit a Value and click Apply, then Save As (same-length float splice).";
    }

    /// <summary>Fills the read-only Quests grid with the <b>computed Pip-Boy quest list</b> (ROADMAP §6 #16):
    /// <see cref="QuestPipboy"/> reconstructs which quests the in-game Pip-Boy shows by interpreting the masters'
    /// quest scripts (Start-Game-Enabled startup, reached-stage effects, condition-evaluated guards), not by
    /// reading the save's recorded progress (which over- and under-includes). Each shown quest is flattened to its
    /// displayed objectives. Needs the masters, so without a Data folder the grid stays empty.</summary>
    private void PopulateQuests(FalloutSave save, PluginDatabase db)
    {
        Quests.Clear();
        if (db.Count == 0)
        {
            QuestsInfo = "The computed Pip-Boy quest list needs the game's Data folder (FalloutNV.esm) to read the " +
                         "quest scripts — set it on the Edit tab.";
            return;
        }

        var pip = QuestPipboy.Compute(save, db); // already ordered: active first, then completed/failed, by name
        foreach (var q in pip.Quests)
        {
            var lines = q.Objectives
                .Select(o => $"{(o.Completed ? "[x]" : "[ ]")} {(o.Optional ? "(optional) " : "")}{o.Text ?? "?"}")
                .ToList();

            Quests.Add(new QuestRow
            {
                FormId = q.FormId,
                Name = q.Name ?? "",
                State = q.State.ToString(),
                StageCount = q.Objectives.Count,
                DoneStageCount = q.Objectives.Count(o => o.Completed),
                Lines = lines,
            });
        }

        var active = pip.Quests.Count(q => q.State == PipboyQuestState.Active);
        var done = pip.Quests.Count(q => q.State == PipboyQuestState.Completed);
        QuestsInfo = $"Computed Pip-Boy quest list: {Quests.Count} quest(s) — {active} active, {done} completed. " +
                     "Reconstructed by interpreting the masters' quest scripts (ROADMAP §6 #16), not just read from " +
                     "the save — so it excludes background-initialized quests the save records but the Pip-Boy doesn't " +
                     "show. It also reads dialogue (INFO) result scripts and seeds quests whose start/advance/" +
                     "complete dialogue the player actually had (the said-INFO is recorded in the save). Matches the " +
                     "full Pip-Boy on the early-game reference save; recall on mid/late saves is improving but not " +
                     "yet measured against a ground-truth screenshot. Read-only.";
    }

    private static string DescribeInventory(int stacks, PluginDatabase db) =>
        db.Count > 0
            ? $"Player inventory ({stacks} stacks). Names resolved from the game masters in {db.DataFolder}. " +
              "Count and Condition (weapon/armor health) edit in place (same-length); edit a value and Apply, then Save As."
            : $"Player inventory ({stacks} stacks). Item names need the game's Data folder (FalloutNV.esm) — " +
              "not found automatically. Set the Data folder below and click Resolve names. Count and Condition " +
              "edit in place (same-length); edit a value and Apply, then Save As.";

    /// <summary>Stages the edit-tab values onto the loaded save (same-length / fixed-width only).</summary>
    public void ApplyEdits()
    {
        if (_save is null)
            return;

        var messages = new List<string>();

        if (uint.TryParse(EditLevel, out var lvl))
        {
            _save.SetPlayerLevel(lvl);
            Level = lvl;
        }
        else messages.Add("level must be a whole number");

        if (uint.TryParse(EditSaveNumber, out var num))
        {
            _save.SetSaveNumber(num);
            SaveNumber = num;
        }
        else messages.Add("save number must be a whole number");

        if (EditName != PlayerName)
        {
            if (_save.TrySetPlayerName(EditName))
                PlayerName = EditName;
            else
                messages.Add($"name must stay {PlayerName.Length} characters (same-length edits only)");
        }

        if (Special.Count == 7 && !_save.TrySetSpecial(Special.Select(a => a.Value).ToArray()))
            messages.Add("could not apply SPECIAL");

        foreach (var skill in Skills)
            if (!_save.TrySetSkill(skill.Name, skill.Value))
                messages.Add($"could not apply skill {skill.Name}");

        // Only stage stacks whose count actually changed (editing by FormID targets the first matching
        // stack, so applying unchanged rows would be needless and could disturb duplicate-FormID stacks).
        foreach (var row in Inventory)
        {
            if (row.Count != row.OriginalCount && !_save.TrySetItemCount(row.FormId, row.Count))
                messages.Add($"could not apply count for 0x{row.FormId:X8}");
            if (row.Condition is { } c && !Nullable.Equals(row.Condition, row.OriginalCondition)
                && !_save.TrySetItemCondition(row.FormId, c))
                messages.Add($"could not apply condition for 0x{row.FormId:X8}");
        }

        // Caps (the 0x0000000F stack) — staged after the inventory loop so the dedicated field wins if the
        // same stack was also edited in the grid. Only when changed and the save actually carries caps.
        if (!string.IsNullOrWhiteSpace(EditCaps))
        {
            if (uint.TryParse(EditCaps, out var caps))
            {
                if (caps != _save.Caps && !_save.TrySetCaps(caps))
                    messages.Add("could not apply caps (no caps stack in this inventory)");
            }
            else messages.Add("caps must be a whole number");
        }

        // Karma / XP (floats in the player reference record, §4j) — staged only when changed and parseable.
        if (!string.IsNullOrWhiteSpace(EditKarma))
        {
            if (float.TryParse(EditKarma, out var k))
            {
                if (k != _save.Karma && !_save.TrySetKarma(k))
                    messages.Add("could not apply karma (player reference record/slot not located)");
            }
            else messages.Add("karma must be a number");
        }
        if (!string.IsNullOrWhiteSpace(EditXp))
        {
            if (float.TryParse(EditXp, out var xp))
            {
                if (xp != _save.Xp && !_save.TrySetXp(xp))
                    messages.Add("could not apply XP (player reference record/slot not located)");
            }
            else messages.Add("XP must be a number");
        }

        // Global variables (GlobalData type 3, §4c) — stage only the rows whose value changed (a same-length
        // float splice). Editing by FormID targets the first matching global, so unchanged rows are left alone.
        foreach (var g in Globals)
            if (g.Value != g.OriginalValue && !_save.TrySetGlobalVariable(g.FormId, g.Value))
                messages.Add($"could not apply global 0x{g.FormId:X8}");

        Status = messages.Count == 0
            ? "Edits staged. Use \"Save As…\" to write a new .fos."
            : "Some edits were skipped: " + string.Join("; ", messages);
    }

    public void SaveAs(string path)
    {
        if (_save is null)
            return;
        try
        {
            _save.Save(path, backup: true);
            Status = $"Saved to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            Status = $"Save failed: {ex.Message}";
        }
    }

    public string? SuggestedSavePath => _loadedPath;

    private static ImageSource BuildScreenshot(SaveScreenshot shot)
    {
        var bgra = shot.ToBgra32();
        var bmp = BitmapSource.Create(
            shot.Width, shot.Height, 96, 96, PixelFormats.Bgra32, null, bgra, shot.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---- INotifyPropertyChanged -------------------------------------------
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
