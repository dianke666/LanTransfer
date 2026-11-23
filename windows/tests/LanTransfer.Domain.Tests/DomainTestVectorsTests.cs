using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>领域层测试项目的输出目录中同样有测试向量（filename-sanitizer.json 等由本项目的测试使用）。</summary>
public sealed class DomainTestVectorsTests
{
    [Fact]
    public void Manifest_is_available_to_domain_tests()
    {
        Assert.Equal("1.0", TestVectors.Manifest().Str("protocol_version"));
    }
}
