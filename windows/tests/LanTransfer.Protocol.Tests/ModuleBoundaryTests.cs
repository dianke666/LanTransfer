using System.Reflection;
using System.Runtime.Versioning;
using FsCheck.Xunit;
using Xunit;

namespace LanTransfer.Protocol.Tests;

/// <summary>
/// 工程结构检查：协议层是平台无关的 net10.0 程序集，且处于最底层，不引用其他 LanTransfer 程序集（设计“依赖规则”）。
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly Subject = Assembly.Load("LanTransfer.Protocol");

    [Fact]
    public void Targets_platform_neutral_net10()
    {
        Assert.Equal(".NETCoreApp,Version=v10.0", Subject.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Null(Subject.GetCustomAttribute<TargetPlatformAttribute>());
    }

    [Fact]
    public void Does_not_reference_other_LanTransfer_assemblies()
    {
        var references = Subject.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("LanTransfer.", StringComparison.Ordinal));
        Assert.Empty(references);
    }

    /// <summary>工具链检查：FsCheck.Xunit.v3 的 [Property] 能被 xUnit v3 发现并执行。</summary>
    [Property]
    public bool FsCheck_property_is_discovered_and_run(int a, int b) => a + b == b + a;
}
