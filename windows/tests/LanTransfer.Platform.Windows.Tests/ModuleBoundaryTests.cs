using System.Reflection;
using System.Runtime.Versioning;
using Xunit;

namespace LanTransfer.Platform.Windows.Tests;

/// <summary>
/// 工程结构检查：平台适配层面向 Windows 10 SDK 19041 编译，最低支持 Windows 10 1809（需求 1.2），且不引用应用层。
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly Subject = Assembly.Load("LanTransfer.Platform.Windows");

    [Fact]
    public void Targets_windows_10_sdk_19041_with_1809_minimum()
    {
        Assert.Equal(".NETCoreApp,Version=v10.0", Subject.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Equal("Windows10.0.19041.0", Subject.GetCustomAttribute<TargetPlatformAttribute>()?.PlatformName);
        Assert.Equal("Windows10.0.17763.0", Subject.GetCustomAttribute<SupportedOSPlatformAttribute>()?.PlatformName);
    }

    [Fact]
    public void Does_not_reference_app_layer()
    {
        var names = Subject.GetReferencedAssemblies().Select(a => a.Name);
        Assert.DoesNotContain("LanTransfer", names);
        Assert.DoesNotContain("LanTransfer.App", names);
    }
}
