using System.Text;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>测试向量接入检查：protocol/test-vectors 已复制到输出目录，manifest 列出的文件都存在且结构正确。</summary>
public sealed class TestVectorsTests
{
    [Fact]
    public void Manifest_is_readable_and_versioned()
    {
        var manifest = TestVectors.Manifest();
        Assert.Equal(TestVectors.FormatVersion, manifest.GetProperty("format_version").GetInt32());
        Assert.Equal("1.0", manifest.Str("protocol_version"));
    }

    [Fact]
    public void Every_listed_file_exists_and_has_vectors()
    {
        foreach (var file in TestVectors.Files())
        {
            var doc = TestVectors.Load(file);
            Assert.Equal("1.0", doc.Str("protocol_version"));
            Assert.Equal(System.Text.Json.JsonValueKind.Array, doc.GetProperty("vectors").ValueKind);
        }
    }

    [Fact]
    public void Readme_is_copied_with_the_vectors()
    {
        Assert.Contains("frames.json", Encoding.UTF8.GetString(TestVectors.Bytes("README.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void Hex_round_trips()
    {
        Assert.Equal("00ff4c54", TestVectors.ToHex(TestVectors.Hex("00ff4c54")));
        Assert.Empty(TestVectors.Hex(""));
    }
}
