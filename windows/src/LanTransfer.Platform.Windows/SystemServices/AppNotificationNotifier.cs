using System.Security;
using LanTransfer.Domain;
using LanTransfer.Engine;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using Microsoft.Windows.AppNotifications;

namespace LanTransfer.Platform.Windows.SystemServices;

/// <summary>通知按钮或主体被点击时的激活回调载荷（设计 C7.2）。</summary>
public sealed record NotificationAction(string Kind, string? PeerHex, string? IdHex, int? FileIndex);

/// <summary>
/// Windows App SDK 系统通知适配（设计 C7.2、需求 21.6–21.11）：
/// 非打包自包含运行，在注册前订阅 NotificationInvoked，退出时仅调用 Unregister() 保留通知中心交互；
/// 在无桌面/测试环境下注册失败时自动安全降级。
/// </summary>
public sealed class AppNotificationNotifier : INotifier, IDisposable
{
    private readonly ObservableState<bool> _enabled = new(true);
    private readonly Action<NotificationAction> _onAction;
    private bool _registered;

    public AppNotificationNotifier(Action<NotificationAction> onAction)
    {
        _onAction = onAction;
        TryRegister();
    }

    public IObservableState<bool> Enabled => _enabled;

    public void RefreshEnabled()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            _enabled.Value = AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;
        }
        catch
        {
        }
    }

    public void Text(NotifyKey key, string peerName, string preview)
    {
        if (key is not NotifyKey.Text tk)
        {
            return;
        }

        var xml = $"""
            <toast launch="action=open_peer&amp;peer={tk.Peer.Value.ToHex()}">
              <visual>
                <binding template="ToastGeneric">
                  <text>来自“{Esc(peerName)}”的新文字</text>
                  <text>{Esc(preview)}</text>
                </binding>
              </visual>
              <actions>
                <action content="复制" arguments="action=copy_text&amp;peer={tk.Peer.Value.ToHex()}&amp;id={tk.MessageId.Value.ToHex()}" activationType="foreground"/>
              </actions>
            </toast>
            """;
        ShowToast(xml, key);
    }

    public void FileCompleted(NotifyKey key, string peerName, string fileName, StoredFileRef file)
    {
        var (tidHex, idx) = key is NotifyKey.File fk ? (fk.TransferId.Value.ToHex(), fk.Index) : (string.Empty, 0);
        var xml = $"""
            <toast launch="action=open_history">
              <visual>
                <binding template="ToastGeneric">
                  <text>已接收来自“{Esc(peerName)}”的文件</text>
                  <text>{Esc(fileName)}</text>
                </binding>
              </visual>
              <actions>
                <action content="打开文件" arguments="action=open_file&amp;id={tidHex}&amp;idx={idx}" activationType="foreground"/>
                <action content="打开所在位置" arguments="action=reveal_file&amp;id={tidHex}&amp;idx={idx}" activationType="foreground"/>
              </actions>
            </toast>
            """;
        ShowToast(xml, key);
    }

    public void Request(NotifyKey key, string peerName, RequestKind kind, int? fileCount)
    {
        var (title, body, launch) = kind == RequestKind.Pairing
            ? ("收到配对请求", $"“{peerName}”请求与本机配对，点击查看", $"action=pair_req&amp;peer={(key is NotifyKey.PairingRequest pr ? pr.Peer.Value.ToHex() : string.Empty)}")
            : ("收到文件传输请求", $"“{peerName}”想发送 {fileCount ?? 1} 个文件，点击确认", $"action=file_req&amp;id={(key is NotifyKey.Transfer tr ? tr.TransferId.Value.ToHex() : string.Empty)}");

        var xml = $"""
            <toast launch="{launch}">
              <visual>
                <binding template="ToastGeneric">
                  <text>{Esc(title)}</text>
                  <text>{Esc(body)}</text>
                </binding>
              </visual>
            </toast>
            """;
        ShowToast(xml, key);
    }

    public void Problem(NotifyKey key, string peerName, string? fileName, string status)
    {
        var body = string.IsNullOrEmpty(fileName) ? status : $"{fileName}：{status}";
        var xml = $"""
            <toast launch="action=open_history">
              <visual>
                <binding template="ToastGeneric">
                  <text>传输异常（{Esc(peerName)}）</text>
                  <text>{Esc(body)}</text>
                </binding>
              </visual>
            </toast>
            """;
        ShowToast(xml, key);
    }

    public void Progress(TransferId task, string peerName, string fileName, int percent)
    {
        // Android 前台服务专用；Windows 端不在通知中心刷屏（C5.4）
    }

    public void Cancel(NotifyKey key)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var (tag, group) = TagGroupOf(key);
            _ = AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
        }

        _registered = false;
    }

    private void TryRegister()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register("局域网传输", null);
            _registered = true;
            RefreshEnabled();
        }
        catch
        {
            _registered = false;
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            var map = ParseArgs(args.Argument);
            if (!map.TryGetValue("action", out var kind))
            {
                return;
            }

            map.TryGetValue("peer", out var peer);
            map.TryGetValue("id", out var id);
            int? idx = map.TryGetValue("idx", out var idxStr) && int.TryParse(idxStr, out var i) ? i : null;
            _onAction(new NotificationAction(kind, peer, id, idx));
        }
        catch
        {
        }
    }

    private void ShowToast(string xml, NotifyKey key)
    {
        if (!_registered)
        {
            return;
        }

        try
        {
            var (tag, group) = TagGroupOf(key);
            var n = new AppNotification(xml) { Tag = tag, Group = group };
            AppNotificationManager.Default.Show(n);
        }
        catch
        {
        }
    }

    private static (string Tag, string Group) TagGroupOf(NotifyKey key) => key switch
    {
        NotifyKey.Text t => (t.MessageId.Value.ToHex()[..16], "text"),
        NotifyKey.PairingRequest p => (p.Peer.Value.ToHex()[..16], "pair"),
        NotifyKey.Transfer tr => (tr.TransferId.Value.ToHex()[..16], "xfer"),
        NotifyKey.File f => ($"{f.TransferId.Value.ToHex()[..12]}_{f.Index}", "file"),
        _ => ("default", "general"),
    };

    private static Dictionary<string, string> ParseArgs(string raw)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                d[part[..eq]] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        return d;
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? string.Empty;
}
