using System.Buffers.Binary;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

/// <summary>Pins the CHANGE_QUEST_SCRIPT local-variable block decode (ROADMAP §6 #16 Stage B): the payload is
/// <c>[vsval count] count×(u32 SLSD index, f64 value)</c>. The shape is the real NVDLC03TeleportEffectTimer
/// block (count 2 → var 2 = a timer, var 3 = 1.0).</summary>
public class QuestScriptVarsTests
{
    private static RefField U8(byte b) => new(0, [b]);
    private static RefField U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return new RefField(0, b); }
    private static RefField F64(double v) { var b = new byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(b, v); return new RefField(0, b); }

    [Fact]
    public void Decodes_the_two_var_block()
    {
        // vsval count 2 (0x08 = 2<<2), then (idx 2, 10.78), (idx 3, 1.0), then trailing section fields.
        var fields = new List<RefField> { U8(0x08), U32(2), F64(10.78), U32(3), F64(1.0), U8(0), U8(0) };

        var vars = QuestScriptVars.DecodeFields(fields);

        Assert.Equal(2, vars.Count);
        Assert.Equal(2, vars[0].Index);
        Assert.Equal(10.78, vars[0].Value, 6);
        Assert.Equal(3, vars[1].Index);
        Assert.Equal(1.0, vars[1].Value, 6);
    }

    [Fact]
    public void Empty_block_count_zero_yields_no_vars()
    {
        // NVDLC03Intro: count 0 then the trailing two zero fields.
        Assert.Empty(QuestScriptVars.DecodeFields([U8(0), U8(0), U8(0)]));
    }

    [Fact]
    public void Rejects_a_non_var_block_layout_rather_than_misparsing()
    {
        // Count says 1 pair, but the "value" field is 4 bytes (not an f64) — not the var-block shape.
        Assert.Empty(QuestScriptVars.DecodeFields([U8(0x04), U32(2), U32(99)]));
        // Count overruns the available fields.
        Assert.Empty(QuestScriptVars.DecodeFields([U8(0x08), U32(2), F64(1.0)]));
        // Leading field isn't a 1-byte vsval-aligned count.
        Assert.Empty(QuestScriptVars.DecodeFields([U32(2), F64(1.0)]));
    }
}
