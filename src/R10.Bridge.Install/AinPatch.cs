using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace R10.Bridge.Install;

internal sealed record PatchEdit(int Offset, int Remove, string ExpectedSha256, string Data);
internal sealed record PatchRecipe(int Schema, string SourceSha256, string SourcePlainSha256, string TargetPlainSha256,
    int SourcePlainLength, int TargetPlainLength, string ExeSha256, string DllSha256, PatchEdit[] Edits);
internal sealed record InstallProfile(string ExeHash, string SourceHash, string TargetPlainHash)
{
    internal static readonly InstallProfile Supported = new(
        "39A508C05B13AFC5427F0B722FCE5C4EDECED126587E037D585CF0E70D279297",
        "7ED22B2854DCB93B6E447198E7CB682CD05B90CF6AB3226091D80003D52DD48A",
        "6016DF12F4F8C47C596AF8A7951C6149201768CF3996CA0C49D1E13D2FE12D4A");
}

internal static class AinPatch
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, WriteIndented=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    internal static bool Same(string? left, string? right) => string.Equals(left,right,StringComparison.OrdinalIgnoreCase);
    internal static byte[] Decode(byte[] raw)
    {
        if (raw.Length<16 || !raw.AsSpan(0,8).SequenceEqual("AI2\0\0\0\0\0"u8)) throw new InvalidDataException("不支持的 AIN 文件格式。");
        int plainLength=BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(8)), compressedLength=BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(12));
        if (plainLength<8 || plainLength>32*1024*1024 || compressedLength!=raw.Length-16) throw new InvalidDataException("AIN 长度信息无效。");
        using var input=new MemoryStream(raw,16,compressedLength,false);
        using var decoder=new ZLibStream(input,CompressionMode.Decompress);
        byte[] plain=new byte[plainLength];
        decoder.ReadExactly(plain);
        if (decoder.ReadByte()!=-1) throw new InvalidDataException("AIN 解压长度不匹配。");
        return plain;
    }
    internal static byte[] Encode(byte[] plain)
    {
        using var packed=new MemoryStream();
        using (var encoder=new ZLibStream(packed,CompressionLevel.SmallestSize,true)) encoder.Write(plain);
        byte[] compressed=packed.ToArray();
        byte[] raw=new byte[16+compressed.Length];
        "AI2\0\0\0\0\0"u8.CopyTo(raw);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8),plain.Length);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(12),compressed.Length);
        compressed.CopyTo(raw,16); return raw;
    }
    internal static PatchRecipe Read(string path, InstallProfile profile)
    {
        var info=new FileInfo(path);
        if (!info.Exists || info.Length>64*1024) throw new InvalidDataException("HP 组件补丁缺失或过大，请重新解压完整修改器。");
        var patch=JsonSerializer.Deserialize<PatchRecipe>(File.ReadAllBytes(path),Json) ?? throw new InvalidDataException("HP 组件补丁无效。");
        if (patch.Schema!=1 || !Same(patch.SourceSha256,profile.SourceHash) || !Same(patch.ExeSha256,profile.ExeHash)
            || !Same(patch.TargetPlainSha256,profile.TargetPlainHash) || patch.Edits is null || patch.Edits.Length is 0 or >256
            || patch.SourcePlainLength is <8 or >33554432 || patch.TargetPlainLength is <8 or >33554432
            || patch.Edits.Any(x=>x is null || x.Data is null || x.ExpectedSha256 is null))
            throw new InvalidDataException("HP 组件与此修改器的适配版本不一致。");
        return patch;
    }
    internal static byte[] Apply(byte[] raw, PatchRecipe patch)
    {
        if (!Same(Hash(raw),patch.SourceSha256)) throw new InvalidDataException("游戏脚本不是已适配的原版文件。");
        var original=Decode(raw);
        if (original.Length!=patch.SourcePlainLength || !Same(Hash(original),patch.SourcePlainSha256)) throw new InvalidDataException("原版脚本内容校验失败。");
        using var result=new MemoryStream(patch.TargetPlainLength);
        int cursor=0,authorBytes=0;
        foreach (var edit in patch.Edits)
        {
            if (edit.Offset<cursor || edit.Remove<0 || edit.Offset>original.Length || edit.Remove>original.Length-edit.Offset)
                throw new InvalidDataException("HP 补丁范围无效。");
            var replacement=Convert.FromBase64String(edit.Data);
            authorBytes=checked(authorBytes+replacement.Length);
            if (authorBytes>12*1024 || !Same(Hash(original.AsSpan(edit.Offset,edit.Remove).ToArray()),edit.ExpectedSha256)) throw new InvalidDataException("HP 补丁原始字节校验失败。");
            result.Write(original,cursor,edit.Offset-cursor); result.Write(replacement); cursor=edit.Offset+edit.Remove;
        }
        result.Write(original,cursor,original.Length-cursor);
        var plain=result.ToArray();
        if (plain.Length!=patch.TargetPlainLength || !Same(Hash(plain),patch.TargetPlainSha256)) throw new InvalidDataException("HP 补丁输出校验失败。");
        var encoded=Encode(plain);
        if (!Same(Hash(Decode(encoded)),patch.TargetPlainSha256)) throw new InvalidDataException("HP 组件压缩后校验失败。");
        return encoded;
    }
}
