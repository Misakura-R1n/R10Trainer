using System.Buffers.Binary;

namespace R10.Core.Patching;

// These are version-specific AIN bytecode facts, not native x86 instructions.
// Only four-byte operands are writable; the surrounding windows remain immutable.
internal sealed class PatchDefinition
{
    internal PatchDefinition(string name, FeatureId feature, int functionId, uint functionPc, uint windowPc,
        string window, uint instructionPc, string original, string patched, int operandOffset, uint returnPc = 0)
    {
        Name = name; Feature = feature; WindowPc = windowPc; InstructionPc = instructionPc;
        OriginalWindow = Convert.FromHexString(window); Original = Convert.FromHexString(original);
        Patched = Convert.FromHexString(patched); OperandOffset = operandOffset; ReturnPc = returnPc;
        var header = checked((int)(functionPc - windowPc));
        var instruction = checked((int)(instructionPc - windowPc));
        if (OriginalWindow[header] != 0x61 || OriginalWindow[header + 1] != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(OriginalWindow.AsSpan(header + 2, 4)) != functionId ||
            !OriginalWindow.AsSpan(instruction, Original.Length).SequenceEqual(Original) ||
            Original.Length != Patched.Length || operandOffset < 0 || operandOffset + 4 != Original.Length ||
            !Original.AsSpan(0, operandOffset).SequenceEqual(Patched.AsSpan(0, operandOffset)))
            throw new InvalidOperationException($"AIN 配置自检失败：{name}");
    }
    internal string Name { get; }
    internal FeatureId Feature { get; }
    internal uint WindowPc { get; }
    internal uint InstructionPc { get; }
    internal uint ReturnPc { get; }
    internal byte[] OriginalWindow { get; }
    internal byte[] Original { get; }
    internal byte[] Patched { get; }
    internal int OperandOffset { get; }
    internal byte[] Window(ReadOnlySpan<byte> instruction)
    {
        var result = (byte[])OriginalWindow.Clone();
        instruction.CopyTo(result.AsSpan(checked((int)(InstructionPc - WindowPc)), Original.Length));
        return result;
    }
}

internal static class BugFixProfile
{
    internal const string Sha256 = "7ED22B2854DCB93B6E447198E7CB682CD05B90CF6AB3226091D80003D52DD48A";
    internal const uint CodeLength = 0xDE5CEA;
    internal static readonly (uint Pc, byte[] Bytes)[] Anchors = [
        (0x3B0A1C, Convert.FromHexString("5B0000000300000002003000746D000004017700040000000F01000002000000AE6700005C000000000005000000000000000200770005006600000000000000")),
        (0x3E4A96, Convert.FromHexString("5B0000000100000002000000DA7300005C00000000002F007E00B99000005B00000002000000020000007671000041009E0B00005C0001000000050000000200")),
        (0x43E2C4, Convert.FromHexString("0500000001000000000000000000190001002C0042E3430061003B920000050000000000000002000000D56B00005C0000000000050003010000000000000200"))
    ];
    internal static readonly PatchDefinition[] Definitions = [
        new("FoodTicketA", FeatureId.Redraw, 0x901E, 0x3B0854, 0x3B084E,
            "2C0070083B0061001E9000005B000000806000005C00000000002F007E001E9000005B0000000A0000000200000005000000",
            0x3B085C, "000080600000", "000085600000", 2),
        new("FoodTicketB", FeatureId.Redraw, 0x6085, 0x3B1302, 0x3B1302,
            "6100856000005B0000000600000002003000306700000000D007000030007E7400005B000000866000005C0000000000",
            0x3B1318, "0000D0070000", "000000000000", 2),
        new("AutoBattleRepick", FeatureId.Redistribute, 0x90BA, 0x3E4B1E, 0x3E4B1E,
            "6100BA9000005B000000030000000200FF0035040000F40035040000444B3E002C00344B3E005B00000002000000020000007671000041008E0D00005C000100000005000000000000000200770005006600000000000000660019000000A37100000000000000005C000100000005000000000000002900020077000000FFFFFFFF190001002F007E00BA900000",
            0x3E4B34, "F40035040000444B3E00", "F40035040000A44B3E00", 6, 0x3E4BA4)
    ];
}
