using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class ReferenceChangeFormTests
{
    [Fact]
    public void InventorySearchStart_skips_the_move_block_when_the_flag_is_set()
    {
        // 27-byte MOVE block + its 0x7C delimiter, then the rest. With CHANGE_REFR_MOVE set and the
        // delimiter where it's expected, the search starts just past the block (27 + 1 delimiter).
        var data = new byte[40];
        data[ReferenceChangeForm.MoveBlockLength] = ReferenceChangeForm.Delimiter; // delimiter at index 27

        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x1000, ReferenceChangeForm.ChangeRefrMove);

        Assert.Equal(0x1000 + ReferenceChangeForm.MoveBlockLength + 1, start);
    }

    [Fact]
    public void InventorySearchStart_does_not_skip_when_the_move_flag_is_clear()
    {
        var data = new byte[40];
        data[ReferenceChangeForm.MoveBlockLength] = ReferenceChangeForm.Delimiter;

        // INVENTORY set but MOVE clear -> no 27-byte block leads the data, so start at the data offset.
        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x1000, ReferenceChangeForm.ChangeRefrInventory);

        Assert.Equal(0x1000, start);
    }

    [Fact]
    public void InventorySearchStart_falls_back_when_the_move_delimiter_is_missing()
    {
        // MOVE flag set but no delimiter at index 27 (a 0x7C fell inside a float, say) -> fall back to the
        // data offset rather than mis-skipping; the forward scan still locates the list.
        var data = new byte[40]; // index 27 is 0x00, not the delimiter

        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x1000, ReferenceChangeForm.ChangeRefrMove);

        Assert.Equal(0x1000, start);
    }

    [Fact]
    public void DescribeFlags_labels_the_confirmed_reference_bits()
    {
        var text = ReferenceChangeForm.DescribeFlags(
            ReferenceChangeForm.ChangeRefrMove | ReferenceChangeForm.ChangeRefrInventory);

        Assert.Contains("MOVE", text);
        Assert.Contains("INVENTORY", text);
        Assert.Equal("MOVE", ReferenceChangeForm.FlagBitLabels[1]);
        Assert.Equal("INVENTORY", ReferenceChangeForm.FlagBitLabels[5]);
    }
}
