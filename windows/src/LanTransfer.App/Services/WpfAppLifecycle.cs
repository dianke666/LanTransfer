using System.Windows;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;

namespace LanTransfer.App.Services;

/// <summary>
/// WPF 前后台状态判定（设计 C7.1）：主窗口或其拥有的对话框处于活动且未隐藏、未最小化时视为前台。
/// </summary>
public sealed class WpfAppLifecycle : IAppLifecycle
{
    private readonly ObservableState<bool> _background = new(false);
    private Window? _mainWindow;

    public IObservableState<bool> Background => _background;

    public void Attach(Window mainWindow)
    {
        _mainWindow = mainWindow;
        mainWindow.Activated += (_, _) => Recompute();
        mainWindow.Deactivated += (_, _) => Recompute();
        mainWindow.StateChanged += (_, _) => Recompute();
        mainWindow.IsVisibleChanged += (_, _) => Recompute();
        Recompute();
    }

    public void Recompute()
    {
        if (_mainWindow is null)
        {
            _background.Value = true;
            return;
        }

        var visible = _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized;
        var active = _mainWindow.IsActive || _mainWindow.OwnedWindows.Cast<Window>().Any(w => w.IsActive);
        _background.Value = !(visible && active);
    }
}
