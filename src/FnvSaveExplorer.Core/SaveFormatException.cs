namespace FnvSaveExplorer.Core;

/// <summary>
/// Thrown when a file does not match the expected Fallout <c>.fos</c> save layout. Carries the
/// byte offset where parsing failed to make reverse-engineering and debugging tractable.
/// </summary>
public sealed class SaveFormatException : Exception
{
    public int Offset { get; }

    public SaveFormatException(string message, int offset = -1) : base(Decorate(message, offset))
        => Offset = offset;

    private static string Decorate(string message, int offset)
        => offset >= 0 ? $"{message} (at offset 0x{offset:X})" : message;
}
