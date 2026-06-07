using System.Buffers.Binary;

namespace FnvSaveExplorer.Core;

/// <summary>
/// Forward-only little-endian cursor over a save's bytes. Every read advances <see cref="Position"/>;
/// helpers throw <see cref="SaveFormatException"/> (with the failing offset) on truncation or a
/// missing delimiter, so malformed input fails loudly instead of silently mis-parsing.
/// </summary>
internal sealed class ByteReader(byte[] data)
{
    private readonly byte[] _data = data;

    public int Position { get; private set; }
    public int Length => _data.Length;
    public int Remaining => _data.Length - Position;

    public byte ReadByte()
    {
        Require(1);
        return _data[Position++];
    }

    public byte PeekByte()
    {
        Require(1);
        return _data[Position];
    }

    public ushort ReadUInt16()
    {
        Require(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Position, 2));
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        Require(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4));
        Position += 4;
        return value;
    }

    public byte[] ReadArray(int count)
    {
        if (count < 0)
            throw new SaveFormatException($"negative length {count}", Position);
        Require(count);
        var slice = _data.AsSpan(Position, count).ToArray();
        Position += count;
        return slice;
    }

    /// <summary>Reads bytes up to (but not including) <paramref name="delimiter"/>, then consumes it.</summary>
    public byte[] ReadUntil(byte delimiter)
    {
        var start = Position;
        while (Position < _data.Length)
        {
            if (_data[Position] == delimiter)
            {
                var slice = _data.AsSpan(start, Position - start).ToArray();
                Position++; // consume delimiter
                return slice;
            }
            Position++;
        }
        throw new SaveFormatException($"delimiter 0x{delimiter:X2} not found", start);
    }

    /// <summary>Consumes a single expected delimiter byte, or throws describing what was expected.</summary>
    public void Expect(byte value, string context)
    {
        var at = Position;
        var actual = ReadByte();
        if (actual != value)
            throw new SaveFormatException(
                $"expected 0x{value:X2} ({context}) but found 0x{actual:X2}", at);
    }

    private void Require(int count)
    {
        if (Position + count > _data.Length)
            throw new SaveFormatException(
                $"unexpected end of file: needed {count} byte(s), {Remaining} remain", Position);
    }
}
