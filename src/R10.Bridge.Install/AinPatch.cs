using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using R10.Core.Memory;

namespace R10.Bridge.Install;

internal sealed record PatchEdit(int Offset, int Remove, string ExpectedSha256, string Data);
internal sealed record PatchRecipe(int Schema, string SourceSha256, string SourcePlainSha256, string TargetPlainSha256,
    int SourcePlainLength, int TargetPlainLength, string ExeSha256, string DllSha256, PatchEdit[] Edits);

internal static class AinPatch
{
    private sealed record PatchBundle(int Schema, PatchRecipe[] Profiles);
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase, WriteIndented=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    internal static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
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
    internal static byte[] Encode(ReadOnlySpan<byte> plain)
    {
        using var packed=new MemoryStream();
        using (var encoder=new ZLibStream(packed,CompressionLevel.SmallestSize,true)) encoder.Write(plain);
        var compressed=packed.GetBuffer().AsSpan(0,checked((int)packed.Length));
        byte[] raw=new byte[16+compressed.Length];
        "AI2\0\0\0\0\0"u8.CopyTo(raw);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(8),plain.Length);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(12),compressed.Length);
        compressed.CopyTo(raw.AsSpan(16)); return raw;
    }
    internal static PatchRecipe Read(string path, GameVersion version)
    {
        var info=new FileInfo(path);
        if (!info.Exists || info.Length>512*1024) throw new InvalidDataException("组件补丁缺失或过大，请重新解压完整修改器。");
        using var document=JsonDocument.Parse(File.ReadAllBytes(path));
        var root=document.RootElement;
        if (root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("schema",out var schema)
            || schema.ValueKind!=JsonValueKind.Number || !schema.TryGetInt32(out int number))
            throw new InvalidDataException("组件补丁格式无效。");
        PatchRecipe[]? recipes=number switch
        {
            1 when root.Deserialize<PatchRecipe>(Json) is { } single => [single],
            2 => root.Deserialize<PatchBundle>(Json)?.Profiles,
            _ => null
        };
        if (recipes is null || recipes.Length<1 || recipes.Length>GameVersions.All.Count || recipes.Any(recipe=>recipe is null))
            throw new InvalidDataException("组件配方清单无效。");
        var matches=recipes.Where(recipe=>Same(recipe.SourceSha256,version.SourceSha256) && Same(recipe.ExeSha256,version.ExeSha256)).ToArray();
        if (matches.Length==0) throw new InvalidDataException($"此安装包缺少 {version.Name} 的组件配方。");
        if (matches.Length!=1) throw new InvalidDataException("组件配方重复。");
        var patch=matches[0];
        if (patch.Schema!=1 || !Same(patch.SourcePlainSha256,version.SourcePlainSha256)
            || !Same(patch.TargetPlainSha256,version.TargetPlainSha256) || patch.TargetPlainLength!=version.TargetPlainLength
            || patch.Edits is null || patch.Edits.Length is 0 or >256
            || patch.SourcePlainLength is <8 or >33554432 || patch.TargetPlainLength is <8 or >33554432
            || patch.Edits.Any(x=>x is null || x.Data is null || x.ExpectedSha256 is null))
            throw new InvalidDataException("组件配方与此修改器的适配版本不一致。");
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
            if (authorBytes>18*1024) throw new InvalidDataException("组件配方的变更字节超出上限。");
            if (!Same(Hash(original.AsSpan(edit.Offset,edit.Remove)),edit.ExpectedSha256)) throw new InvalidDataException("HP 补丁原始字节校验失败。");
            result.Write(original,cursor,edit.Offset-cursor); result.Write(replacement); cursor=edit.Offset+edit.Remove;
        }
        result.Write(original,cursor,original.Length-cursor);
        var plain=result.GetBuffer().AsSpan(0,checked((int)result.Length));
        if (plain.Length!=patch.TargetPlainLength || !Same(Hash(plain),patch.TargetPlainSha256)) throw new InvalidDataException("HP 补丁输出校验失败。");
        var encoded=Encode(plain);
        if (!Same(Hash(Decode(encoded)),patch.TargetPlainSha256)) throw new InvalidDataException("HP 组件压缩后校验失败。");
        return encoded;
    }
}
