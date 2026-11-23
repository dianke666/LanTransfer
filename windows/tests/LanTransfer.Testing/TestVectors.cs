using System.Text.Json;

namespace LanTransfer.Testing;

/// <summary>
/// 测试向量加载器：读取输出目录 <c>test-vectors/</c> 下的 JSON（由 LanTransfer.Testing.csproj 从仓库唯一一份
/// <c>protocol/test-vectors</c> 链接复制）。文件格式见 <c>protocol/test-vectors/README.md</c>。
/// Kotlin 侧同名加载器：<c>com.lantransfer.testing.TestVectors</c>。
/// </summary>
public static class TestVectors
{
    public const int FormatVersion = 1;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "test-vectors");

    /// <summary>读取向量文件的原始字节。</summary>
    public static byte[] Bytes(string file)
    {
        var path = Path.Combine(Directory, file);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到测试向量文件 {path}（检查 LanTransfer.Testing 是否把 protocol/test-vectors 复制到输出目录）", path);
        }

        return File.ReadAllBytes(path);
    }

    /// <summary>读取并解析向量文件。</summary>
    public static JsonElement Load(string file)
    {
        using var doc = JsonDocument.Parse(Bytes(file));
        return doc.RootElement.Clone();
    }

    /// <summary>manifest.json：当前已提交的向量文件列表。</summary>
    public static JsonElement Manifest() => Load("manifest.json");

    /// <summary>manifest.json 中列出的文件名。</summary>
    public static IReadOnlyList<string> Files() =>
        Manifest().GetProperty("files").EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// <summary>向量文件中的 <c>vectors</c> 数组。</summary>
    public static IReadOnlyList<JsonElement> Vectors(string file) => Load(file).GetProperty("vectors").EnumerateArray().ToArray();

    /// <summary>小写十六进制字符串 → 字节（向量中 Bytes 字段的写法）。</summary>
    public static byte[] Hex(string text) => Convert.FromHexString(text);

    /// <summary>字节 → 小写十六进制字符串。</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>读取 JSON 对象中的字符串字段。</summary>
    public static string Str(this JsonElement obj, string key) => obj.GetProperty(key).GetString()!;

    /// <summary>读取 JSON 对象中的十六进制字段。</summary>
    public static byte[] HexBytes(this JsonElement obj, string key) => Hex(obj.Str(key));

    /// <summary>可选字段：缺省时为 null。</summary>
    public static JsonElement? Optional(this JsonElement obj, string key) => obj.TryGetProperty(key, out var v) ? v : null;
}
