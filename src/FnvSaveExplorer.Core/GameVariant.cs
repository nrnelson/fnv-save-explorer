namespace FnvSaveExplorer.Core;

/// <summary>
/// Which Gamebryo title produced a <c>FO3SAVEGAME</c> file. Fallout 3 and New Vegas share
/// the same signature and overall layout; New Vegas inserts a 64-byte language block in the
/// header that Fallout 3 lacks.
/// </summary>
public enum GameVariant
{
    Unknown = 0,
    FalloutNewVegas,
    Fallout3,
}
