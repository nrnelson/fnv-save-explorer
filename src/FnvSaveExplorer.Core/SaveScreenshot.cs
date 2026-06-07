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
}
