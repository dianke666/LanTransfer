using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using LanTransfer.App.Dialogs;
using LanTransfer.App.Services;
using LanTransfer.App.Themes;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Platform.Windows.Storage;
using LanTransfer.Platform.Windows.SystemServices;
using LanTransfer.Protocol;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LanTransfer.App;

/// <summary>
/// 应用入口（设计 C7.5、C8.3）：
/// 1. 强制简体中文区域文化；
/// 2. 处理 --repair-firewall / --remove-firewall 提权子进程参数；
/// 3. 单实例互斥与管道唤醒；
/// 4. 系统托盘图标与右键菜单（打开主窗口 / 退出 + 进行中传输退出确认）；
/// 5. Windows 通知点击回调与注销/关机快速关闭。
/// </summary>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private AppController? _controller;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconHandle;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 需求 1.5 / 设计 C8.3：无论系统语言如何，全部界面固定简体中文
        var zhCn = CultureInfo.GetCultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = zhCn;
        CultureInfo.DefaultThreadCurrentUICulture = zhCn;
        Thread.CurrentThread.CurrentCulture = zhCn;
        Thread.CurrentThread.CurrentUICulture = zhCn;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(zhCn.IetfLanguageTag)));

        // 防火墙维护子进程参数（设计 C7.3）
        if (e.Args.Any(a => string.Equals(a, "--repair-firewall", StringComparison.OrdinalIgnoreCase)))
        {
            var ok = new FirewallManager().RepairRules();
            Shutdown(ok ? 0 : 1);
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--remove-firewall", StringComparison.OrdinalIgnoreCase)))
        {
            var ok = new FirewallManager().RemoveRules();
            Shutdown(ok ? 0 : 1);
            return;
        }

        // 单实例检查
        _singleInstance = new SingleInstance();
        if (!_singleInstance.TryAcquire(OnSecondInstanceMessage))
        {
            SingleInstance.SignalExistingInstance("SHOW");
            Shutdown(0);
            return;
        }

        // 构建核心服务容器；若数据库来自更高版本则以中文对话框提示后退出（M2）
        _controller = new AppController(OnNotificationAction);
        if (!_controller.Database.IsHealthy && _controller.Database.InitError is { } dbErr && dbErr.Contains("高于", StringComparison.Ordinal))
        {
            AppDialog.ShowDialog(
                null,
                UiText.AppName,
                $"{dbErr}，请先更新应用后再启动。",
                UiText.BtnConfirm,
                tone: DialogTone.Danger);
            _controller.Dispose();
            Shutdown(1);
            return;
        }

        // 应用已保存的主题偏好（浅色 / 深色 / 跟随系统）
        ThemeManager.Apply(_controller.SettingsStore.Theme.Value);

        _mainWindow = new MainWindow(_controller);
        MainWindow = _mainWindow;
        SetupTrayIcon();

        _mainWindow.Show();

        var startResult = await _controller.StartAsync();
        if (startResult is StartResult.Running)
        {
            _mainWindow.ViewModel.BindEngineServices();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // 设计 C7.5：系统注销或关机时不弹确认框，直接快速关闭网络层
        base.OnSessionEnding(e);
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            _controller?.ShutdownAsync(SessionCloseReason.AppExit).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        CleanupResources();
    }

    private void OnSecondInstanceMessage(string msg)
    {
        Dispatcher.BeginInvoke(() => _mainWindow?.BringToFront());
    }

    private void SetupTrayIcon()
    {
        _trayIconHandle = CreateTrayIcon();
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem(UiText.BtnOpenMainWindow);
        openItem.Click += (_, _) => Dispatcher.BeginInvoke(() => _mainWindow?.BringToFront());
        var exitItem = new Forms.ToolStripMenuItem(UiText.BtnExit);
        exitItem.Click += (_, _) => Dispatcher.BeginInvoke(() => _ = RequestExitAsync());

        menu.Items.Add(openItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayIconHandle,
            Text = UiText.AppName,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                Dispatcher.BeginInvoke(() => _mainWindow?.BringToFront());
            }
        };
    }

    private async Task RequestExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        var hasActiveTransfer = _controller?.Services?.Files.Tasks.Value.Any(t =>
            t.Task.State is TaskState.Running or TaskState.Resuming or TaskState.Offering or TaskState.AwaitingUser) ?? false;

        if (hasActiveTransfer)
        {
            _mainWindow?.BringToFront();
            var outcome = AppDialog.ShowDialog(
                _mainWindow,
                UiText.DialogExitTransferTitle,
                UiText.DialogExitTransferDesc,
                UiText.BtnExit,
                cancelText: UiText.BtnCancel,
                tone: DialogTone.Warning);
            if (outcome != DialogOutcome.Primary)
            {
                return;
            }
        }

        _exiting = true;
        if (_controller is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await _controller.ShutdownAsync(SessionCloseReason.AppExit).WaitAsync(cts.Token);
            }
            catch
            {
            }
        }

        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        CleanupResources();
        Shutdown(0);
    }

    private void OnNotificationAction(NotificationAction action)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (_controller is null || _mainWindow is null)
            {
                return;
            }

            switch (action.Kind)
            {
                case "copy_text":
                {
                    if (TryParseBytes16(action.PeerHex, out var pid) && TryParseBytes16(action.IdHex, out var mid))
                    {
                        var orig = _controller.Services?.Text.OriginalText(new DeviceId(pid), new MessageId(mid));
                        if (orig is not null)
                        {
                            var r = await _controller.Clipboard.WriteTextAsync(orig);
                            var toast = r is ClipWrite.Ok
                                ? (orig.Contains('\0') ? UiText.ToastCopiedWithNul : UiText.ToastCopied)
                                : UiText.ToastClipboardFailed;
                            _mainWindow.ViewModel.ShowToast(toast);
                        }
                    }

                    break;
                }

                case "open_file":
                case "reveal_file":
                {
                    _mainWindow.BringToFront();
                    if (TryParseBytes16(action.IdHex, out var tidBytes) && action.FileIndex is { } idx)
                    {
                        var tid = new TransferId(tidBytes);
                        var fileRec = _controller.TaskStore.Get(tid)?.Files.ElementAtOrDefault(idx);
                        if (fileRec?.StoredRef is { } fileRef)
                        {
                            var res = action.Kind == "open_file"
                                ? _controller.FileLocator.OpenFile(fileRef, fileRec.Mime)
                                : _controller.FileLocator.RevealInFolder(fileRef);
                            _mainWindow.HandleLocateResult(res, fileRef, null);
                        }
                        else
                        {
                            _mainWindow.ViewModel.OpenHistoryCommand.Execute(null);
                        }
                    }

                    break;
                }

                case "open_peer":
                {
                    _mainWindow.BringToFront();
                    if (TryParseBytes16(action.PeerHex, out var pidBytes))
                    {
                        _mainWindow.ViewModel.OpenConversation(new DeviceId(pidBytes));
                    }

                    break;
                }

                case "pair_req":
                case "file_req":
                    _mainWindow.BringToFront();
                    break;

                case "open_history":
                    _mainWindow.BringToFront();
                    _mainWindow.ViewModel.OpenHistoryCommand.Execute(null);
                    break;
            }
        });
    }

    private static bool TryParseBytes16(string? hex, out Bytes16 value)
    {
        value = default;
        if (string.IsNullOrEmpty(hex) || hex.Length != 32)
        {
            return false;
        }

        try
        {
            value = Bytes16.Of(Convert.FromHexString(hex));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>在内存中绘制品牌矢量风格的 32x32 靛蓝圆角托盘图标。</summary>
    private static Drawing.Icon CreateTrayIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(37, 99, 235));
            using var path = new Drawing.Drawing2D.GraphicsPath();
            const int r = 8;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            g.FillPath(bg, path);

            using var pen = new Drawing.Pen(Drawing.Color.White, 2.4f)
            {
                StartCap = Drawing.Drawing2D.LineCap.Round,
                EndCap = Drawing.Drawing2D.LineCap.Round,
                LineJoin = Drawing.Drawing2D.LineJoin.Round,
            };
            // 双向传输箭头
            g.DrawLine(pen, 9, 13, 23, 13);
            g.DrawLines(pen, [new Drawing.Point(19, 9), new Drawing.Point(23, 13), new Drawing.Point(19, 17)]);
            g.DrawLine(pen, 23, 20, 9, 20);
            g.DrawLines(pen, [new Drawing.Point(13, 16), new Drawing.Point(9, 20), new Drawing.Point(13, 24)]);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    private void CleanupResources()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayIconHandle?.Dispose();
        _trayIconHandle = null;
        _controller?.Dispose();
        _controller = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
