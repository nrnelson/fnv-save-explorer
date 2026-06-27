using System.Buffers.Binary;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class SaveScreenshotTests
{
    [Fact]
    public void ToBmp_writes_a_valid_24bit_bottom_up_bitmap()
    {
        // 2x2 image, R,G,B per pixel (row-major, top-down as stored in the save).
        byte[] rgb =
        [
            10, 20, 30,   40, 50, 60,    // row 0: pixel(0,0), pixel(1,0)
            70, 80, 90,   100, 110, 120, // row 1: pixel(0,1), pixel(1,1)
        ];
        var bmp = new SaveScreenshot(2, 2, rgb).ToBmp();

        // Each 24-bit row is padded to a 4-byte boundary: 2*3 = 6 -> 8 bytes/row.
        const int rowStride = 8;
        Assert.Equal(54 + rowStride * 2, bmp.Length);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2, 4)));  // file size
        Assert.Equal(54, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10, 4)));         // pixel-data offset
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(14, 4)));         // DIB header size
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(18, 4)));          // width
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(22, 4)));          // height (positive => bottom-up)
        Assert.Equal(24, BinaryPrimitives.ReadInt16LittleEndian(bmp.AsSpan(28, 2)));         // bits/pixel

        // BMP rows are bottom-up, so file row 0 is image row 1; pixels are stored B,G,R.
        // File row 0, pixel x=0 == image pixel(0,1) = R70 G80 B90 -> B90 G80 R70.
        Assert.Equal(90, bmp[54]);
        Assert.Equal(80, bmp[55]);
        Assert.Equal(70, bmp[56]);
        // File row 1, pixel x=0 == image pixel(0,0) = R10 G20 B30 -> B30 G20 R10.
        Assert.Equal(30, bmp[54 + rowStride]);
        Assert.Equal(20, bmp[54 + rowStride + 1]);
        Assert.Equal(10, bmp[54 + rowStride + 2]);
    }
}
