namespace FnvSaveExplorer.Core;

/// <summary>
/// The embedded save thumbnail: raw 24-bit pixel data stored in the file as width*height*3 bytes.
/// The Core library stays UI-agnostic and only exposes the raw bytes plus a conversion to 32-bit
/// BGRA, which the WPF layer turns into a <c>BitmapSource</c>.
/// </summary>
public sealed class SaveScreenshot(int width, int height, byte[] rgb)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>Raw pixel bytes exactly as stored in the file (3 bytes per pixel, row-major).</summary>
    public byte[] Rgb { get; } = rgb;

    public int PixelCount => Width * Height;

    /// <summary>
    /// Converts the stored pixels to 32-bit BGRA (the order WPF's <c>PixelFormats.Bgra32</c> wants),
    /// assuming the file stores them in R,G,B order. Alpha is forced opaque.
    /// </summary>
    public byte[] ToBgra32()
    {
        var dst = new byte[Width * Height * 4];
        var src = Rgb;
        int s = 0, d = 0;
        var pixels = Math.Min(PixelCount, src.Length / 3);
        for (var i = 0; i < pixels; i++)
        {
            byte r = src[s++], g = src[s++], b = src[s++];
            dst[d++] = b;
            dst[d++] = g;
            dst[d++] = r;
            dst[d++] = 0xFF;
        }
        return dst;
    }

    /// <summary>
    /// Encodes the thumbnail as a 24-bit Windows BMP (bottom-up BGR, rows padded to 4 bytes) — a
    /// dependency-free format the UI-agnostic Core can write so the CLI can export the screenshot without a
    /// WPF imaging stack. The GUI prefers PNG via its own encoder.
    /// </summary>
    public byte[] ToBmp()
    {
        var rowStride = (Width * 3 + 3) & ~3;          // each row padded up to a 4-byte boundary
        var pixelDataSize = rowStride * Height;
        const int headerSize = 14 + 40;                // BITMAPFILEHEADER + BITMAPINFOHEADER
        var bmp = new byte[headerSize + pixelDataSize];

        // BITMAPFILEHEADER (14 bytes)
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteI32(bmp, 2, bmp.Length);                  // total file size
        WriteI32(bmp, 10, headerSize);                 // pixel-data offset
        // BITMAPINFOHEADER (40 bytes)
        WriteI32(bmp, 14, 40);                         // header size
        WriteI32(bmp, 18, Width);
        WriteI32(bmp, 22, Height);                     // positive => bottom-up
        bmp[26] = 1;                                   // planes
        bmp[28] = 24;                                  // bits per pixel
        WriteI32(bmp, 34, pixelDataSize);

        var src = Rgb;
        var pixels = Math.Min(PixelCount, src.Length / 3);
        for (var y = 0; y < Height; y++)
        {
            // BMP rows run bottom-to-top, so the file's row y holds image row (Height-1-y).
            var imageRow = Height - 1 - y;
            var dst = headerSize + y * rowStride;
            for (var x = 0; x < Width; x++)
            {
                var pix = imageRow * Width + x;
                if (pix >= pixels) break;
                var s = pix * 3;
                bmp[dst++] = src[s + 2];               // B
                bmp[dst++] = src[s + 1];               // G
                bmp[dst++] = src[s];                    // R
            }
        }
        return bmp;

        static void WriteI32(byte[] b, int off, int v) =>
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off, 4), v);
    }
}
