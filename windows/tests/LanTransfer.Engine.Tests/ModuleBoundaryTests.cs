using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// 工程结构检查：引擎层是平台无关的 net10.0 程序集，只允许依赖领域层与协议层，不得引用平台适配层或应用层。
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly Subject = Assembly.Load("LanTransfer.Engine");

    [Fact]
    public void Targets_platform_neutral_net10()
    {
        Assert.Equal(".NETCoreApp,Version=v10.0", Subject.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Null(Subject.GetCustomAttribute<TargetPlatformAttribute>());
    }

    [Fact]
    public void References_only_lower_layers()
    {
        string[] allowed = ["LanTransfer.Protocol", "LanTransfer.Domain"];
        var disallowed = Subject.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null && n.StartsWith("LanTransfer.", StringComparison.Ordinal) && !allowed.Contains(n));
        Assert.Empty(disallowed);
    }
}
