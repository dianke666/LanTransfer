using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>
/// 工程结构检查：领域层是平台无关的 net10.0 程序集，只允许依赖协议层（设计“依赖规则”：engine → domain → protocol）。
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly Subject = Assembly.Load("LanTransfer.Domain");

    [Fact]
    public void Targets_platform_neutral_net10()
    {
        Assert.Equal(".NETCoreApp,Version=v10.0", Subject.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Null(Subject.GetCustomAttribute<TargetPlatformAttribute>());
    }

    [Fact]
    public void References_only_lower_layers()
    {
        string[] allowed = ["LanTransfer.Protocol"];
        var disallowed = Subject.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("LanTransfer.", StringComparison.Ordinal) && !allowed.Contains(n));
        Assert.Empty(disallowed);
    }
}
