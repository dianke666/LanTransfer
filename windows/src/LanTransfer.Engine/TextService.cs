using System.Globalization;
using System.Threading.Channels;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// 文字服务（设计 C4.4、C1.12），与 Kotlin 侧 TextService.kt 同名镜像：执行 TextReducer 的 effects，按 C1.12 的顺序处理收到的文字。
// 全部状态只在一个 actor 任务里修改；打开 Session、计时与写剪贴板在 actor 之外进行，结果作为命令送回 actor。
// 收到的消息由分发器（EngineHost）经 OnMessage 交进来。

/// <summary>会话界面中的一条文字。与 Kotlin 侧的差异：Kotlin 的 <c>text</c> 在这里是 <see cref="Body"/>（与 <c>Text.Body</c> 一致）。</summary>
/// <param name="Body">完整原文（复制时写入剪贴板的就是它，13.2）。</param>
/// <param name="Status">发出的：Sending / Delivered / Failed；收到的：Received。</param>
public sealed record TextItem(TextKey Key, Direction Direction, string Body, long SentAtUtcMs, RecordTime RecordedAt, TextStatus Status)
{
    /// <summary>发送失败时显示“重试”（11.6、12.12）。</summary>
    public bool ShowRetry => Direction == Direction.Send && Status == TextStatus.Failed;
}

/// <summary><see cref="TextService.SendAsync"/> 的结果（发送前检查，C1.12）。</summary>
public abstract record SendCheck
{
    private SendCheck()
    {
    }

    /// <summary>已以“发送中”显示在会话末尾（11.11）。</summary>
    public sealed record Sent(MessageId Id) : SendCheck;

    /// <summary>文字为空（界面已禁用“发送”，11.2；这里兜底，不发送）。</summary>
    public sealed record Empty : SendCheck;

    /// <summary>含 Unpaired_Surrogate：未发送；界面弹出“替换并发送 / 取消”（12.6），选择替换时用 TextRules.ReplaceUnpaired 的结果再次调用（12.10）。</summary>
    public sealed record HasUnpairedSurrogate : SendCheck;

    /// <summary>UTF‑8 编码超过 1 MiB：未发送；界面提示并提供“以 .txt 文件发送”（11.9，见 <see cref="TextService.WriteTextFile"/>）。</summary>
    public sealed record TooLong(long Utf8Bytes) : SendCheck;
}

/// <summary>需要界面提示的事件。</summary>
public abstract record TextNotice
{
    private TextNotice()
    {
    }

    /// <summary>History_Store 读取或写入失败（20.8）；文字收发照常进行。</summary>
    public sealed record StoreFailed(string Detail) : TextNotice;

    /// <summary>
    /// 发送文字时无法建立 Session。消息本身在点击后满 10 s 时标为“发送失败”（11.6）；KeyMismatch、NotTrusted 等需要界面按 9.4、9.5 另行提示。
    /// </summary>
    public sealed record ConnectFailed(DeviceId Peer, OpenFailure Failure) : TextNotice;

    /// <summary>自动复制写入剪贴板失败（13.9）。</summary>
    public sealed record AutoCopyFailed : TextNotice;
}

public sealed class TextService : IDisposable
{
    private readonly SessionManager _sessions;
    private readonly IHistoryStore _history;
    private readonly ITrustStore _trust;
    private readonly IClipboard _clipboard;
    private readonly INotifier _notifier;
    private readonly ISettingsStore _settings;
    private readonly IAppLifecycle _lifecycle;
    private readonly IOutgoingTextCache _textCache;
    private readonly IClock _clock;
    private readonly IRng _rng;
    private readonly Channel<Cmd> _inbox = Channel.CreateUnbounded<Cmd>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>自动复制的原文：按收到的顺序串行写入剪贴板，最后收到的一条留在剪贴板中（13.8）。</summary>
    private readonly Channel<string> _copies = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Subject<TextNotice> _notices = new();

    /// <summary>每个对端的会话条目；访问字典本身要加锁，条目列表只在 actor 中修改。</summary>
    private readonly Lock _conversationsGate = new();
    private readonly Dictionary<DeviceId, ObservableState<ValueList<TextItem>>> _conversations = new();
    private readonly CancellationTokenSource _cts = new();

    // ---- 只在 actor 中访问 ----

    /// <summary>
    /// 本次运行中已显示过的收到的文字 → 是否已写入 History_Store（C1.12 的“已显示集合”）。写库失败时仍然显示，
    /// 发送方因收不到 ACK 而重试时不再重复显示（20.8）；已保存的消息此后只回复 TEXT_ACK，即使记录已被删除也不重新保存。
    /// </summary>
    private readonly Dictionary<TextKey, bool> _shown = new();

    /// <summary>每个对端一个发送队列，保证同一对端的文字按点击顺序入队。</summary>
    private readonly Dictionary<DeviceId, Channel<Outgoing>> _outboxes = new();
    private TextState _state = new();

    public TextService(
        SessionManager sessions,
        IHistoryStore history,
        ITrustStore trust,
        IClipboard clipboard,
        INotifier notifier,
        ISettingsStore settings,
        IAppLifecycle lifecycle,
        IOutgoingTextCache textCache,
        IClock clock,
        IRng rng)
    {
        _sessions = sessions;
        _history = history;
        _trust = trust;
        _clipboard = clipboard;
        _notifier = notifier;
        _settings = settings;
        _lifecycle = lifecycle;
        _textCache = textCache;
        _clock = clock;
        _rng = rng;
        var ct = _cts.Token;
        _ = Task.Run(() => ActorAsync(ct), CancellationToken.None);
        _ = Task.Run(() => CopierAsync(ct), CancellationToken.None);
    }

    private abstract record Cmd
    {
        public sealed record Send(DeviceId Peer, Bytes Utf8, string Body, long Now, TaskCompletionSource<MessageId> Done) : Cmd;

        public sealed record Retry(MessageId Id, long Now) : Cmd;

        public sealed record Inbound(Session Session, ProtocolMessage Msg) : Cmd;

        public sealed record Tick : Cmd;

        public sealed record Restore(TaskCompletionSource Done) : Cmd;

        /// <summary>Keys 为 null 表示全部。</summary>
        public sealed record Forget(IReadOnlySet<TextKey>? Keys, TaskCompletionSource Done) : Cmd;
    }

    /// <summary>待发出的 TEXT 与它的 10 s 截止时刻（单调时钟）。</summary>
    private sealed record Outgoing(Text Msg, long Deadline);

    public IObservable<TextNotice> Notices => _notices;

    public void Dispose() => _cts.Cancel();

    /// <summary>与 <paramref name="peer"/> 的会话中的文字，按显示顺序（11.4、11.11）。</summary>
    public IObservableState<ValueList<TextItem>> Conversation(DeviceId peer) => ConversationState(peer);

    /// <summary>
    /// 用户点击“发送”或“发送剪贴板”（13.6）：先执行 TextRules.CheckOutgoing；通过时生成新的消息 ID，写入 History_Store（失败时仍然发送，E5），
    /// 立即以“发送中”显示（11.11），然后在需要时打开 Session（9.2）并以 P1 优先级入队。从调用时起计时 10 s，建立 Session 的时间也算在内（11.6）。
    /// </summary>
    public async Task<SendCheck> SendAsync(DeviceId peer, string text)
    {
        var now = _clock.MonotonicMs();
        var check = TextRules.CheckOutgoing(text);
        if (check is not OutgoingCheck.Ok ok)
        {
            return check switch
            {
                OutgoingCheck.TooLong t => new SendCheck.TooLong(t.Utf8Bytes),
                OutgoingCheck.HasUnpairedSurrogate => new SendCheck.HasUnpairedSurrogate(),
                _ => new SendCheck.Empty(),
            };
        }

        var done = new TaskCompletionSource<MessageId>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new Cmd.Send(peer, ok.Utf8, text, now, done));
        return new SendCheck.Sent(await done.Task.ConfigureAwait(false));
    }

    /// <summary>“重试”：相同的消息 ID 与文本，状态回到“发送中”，重新计时 10 s（11.7）；只对“发送失败”的消息有效。</summary>
    public void Retry(MessageId id) => Post(new Cmd.Retry(id, _clock.MonotonicMs()));

    /// <summary>分发器交来的应用消息；TEXT、TEXT_ACK、TEXT_CORRUPT 由本服务处理并返回 true，其他消息返回 false。</summary>
    public bool OnMessage(Session session, ProtocolMessage msg)
    {
        if (msg is not (Text or TextAck or TextCorrupt))
        {
            return false;
        }

        Post(new Cmd.Inbound(session, msg));
        return true;
    }

    /// <summary>启动时载入 History_Store 中的文字；“发送中”的计时已丢失，改为“发送失败”（M3.1、17.9）。</summary>
    public Task RestoreOnStartupAsync() => RequestAsync(d => new Cmd.Restore(d));

    /// <summary>删除记录（20.4）之后调用：会话中不再显示这些消息，之后迟到的应答也不再影响它们。</summary>
    public Task ForgetAsync(IReadOnlySet<TextKey> keys) => RequestAsync(d => new Cmd.Forget(keys, d));

    /// <summary>清空记录（20.5）：在 IHistoryStore.ClearAll 之后调用，清掉内存中的全部会话条目。已显示集合保留。</summary>
    public Task ForgetAllAsync() => RequestAsync(d => new Cmd.Forget(null, d));

    /// <summary>
    /// 一条文字的完整原文（通知中的“复制”，13.2、21.6）：先取会话中的条目（包括写库失败、只在内存中的消息，20.8），再查 History_Store。
    /// </summary>
    public string? OriginalText(DeviceId peer, MessageId id)
    {
        if (ConversationState(peer).Value.LastOrDefault(i => i.Key.MessageId == id) is { } item)
        {
            return item.Body;
        }

        return _history.FindText(peer, id) is { } r ? StrictUtf8.Decode(r.TextUtf8.Span) : null;
    }

    /// <summary>
    /// “以 .txt 文件发送”（11.13）：把 <paramref name="text"/> 的 UTF‑8 字节（不加 BOM 或其他任何字节）写入缓存目录下随机命名的子目录中的
    /// <c>文字_YYYYMMDD_HHMMSS.txt</c>；返回的 SourceRef 由调用方交给文件发送流程。前置条件：文字不含 Unpaired_Surrogate（调用方先按 12.6、12.10 处理）。
    /// </summary>
    public WriteResult<SourceRef> WriteTextFile(string text)
    {
        if (StrictUtf8.HasUnpairedSurrogate(text))
        {
            throw new ArgumentException("text must not contain unpaired surrogates", nameof(text));
        }

        var dir = Bytes.Of(_rng.NextBytes(16)).ToHex();
        return _textCache.Write(dir, TextFileName(_clock.LocalNow()), StrictUtf8.Encode(text));
    }

    /// <summary><c>文字_YYYYMMDD_HHMMSS.txt</c>（C1.12）。</summary>
    public static string TextFileName(DateTime now) => string.Create(CultureInfo.InvariantCulture, $"文字_{now:yyyyMMdd_HHmmss}.txt");

    private async Task RequestAsync(Func<TaskCompletionSource, Cmd> make)
    {
        var d = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(make(d));
        await d.Task.ConfigureAwait(false);
    }

    private void Post(Cmd cmd) => _inbox.Writer.TryWrite(cmd);

    private ObservableState<ValueList<TextItem>> ConversationState(DeviceId peer)
    {
        lock (_conversationsGate)
        {
            if (!_conversations.TryGetValue(peer, out var s))
            {
                s = new ObservableState<ValueList<TextItem>>(ValueList<TextItem>.Empty);
                _conversations[peer] = s;
            }

            return s;
        }
    }

    private async Task CopierAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var s in _copies.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (await _clipboard.WriteTextAsync(s, ct).ConfigureAwait(false) is ClipWrite.Failed)
                {
                    _notices.OnNext(new TextNotice.AutoCopyFailed());
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- actor ----

    private async Task ActorAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var cmd in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Handle(cmd);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Handle(Cmd cmd)
    {
        switch (cmd)
        {
            case Cmd.Send s:
            {
                var id = new MessageId(Bytes16.Of(_rng.NextBytes(16)));
                var key = new TextKey(s.Peer, id);
                var sentAt = _clock.WallMs();
                var at = _clock.RecordNow();
                Stored(_history.AppendText(new TextRecord(0, s.Peer, PeerName(s.Peer), id, Direction.Send, s.Utf8, sentAt, at, TextStatus.Sending)));
                Append(new TextItem(key, Direction.Send, s.Body, sentAt, at, TextStatus.Sending));
                Step(new TextEvent.Send(key, s.Utf8, sentAt, s.Now));
                s.Done.TrySetResult(id);
                break;
            }

            case Cmd.Retry r:
            {
                foreach (var key in _state.Outgoing.Keys)
                {
                    if (key.MessageId == r.Id)
                    {
                        Step(new TextEvent.Retry(key, r.Now));
                        break;
                    }
                }

                break;
            }

            case Cmd.Inbound ib:
            {
                var peer = ib.Session.Peer.DeviceId;
                switch (ib.Msg)
                {
                    case TextAck a:
                        Step(new TextEvent.AckReceived(new TextKey(peer, a.MessageId)));
                        break;
                    case TextCorrupt c:
                        Step(new TextEvent.CorruptReceived(new TextKey(peer, c.MessageId)));
                        break;
                    case Text t:
                        Receive(ib.Session, t);
                        break;
                }

                break;
            }

            case Cmd.Tick:
                Step(new TextEvent.Tick(_clock.MonotonicMs()));
                break;

            case Cmd.Restore rs:
                Restore();
                rs.Done.TrySetResult();
                break;

            case Cmd.Forget f:
                ForgetKeys(f.Keys);
                f.Done.TrySetResult();
                break;
        }
    }

    /// <summary>C1.12 接收方的处理顺序。</summary>
    private void Receive(Session session, Text m)
    {
        var peer = session.Peer.DeviceId;

        // 1. 严格 UTF‑8 解码；非法时只回复 TEXT_CORRUPT：不保存、不显示，也不把消息 ID 记为已接收（12.7）
        if (StrictUtf8.Decode(m.Body.Span) is not { } body)
        {
            session.Send(new TextCorrupt(m.MessageId));
            return;
        }

        var key = new TextKey(peer, m.MessageId);
        bool? seen = _shown.TryGetValue(key, out var saved) ? saved : null;
        if (seen == true)
        {
            // 本次运行中已保存过：只回复 TEXT_ACK（11.8）
            session.Send(new TextAck(m.MessageId));
            return;
        }

        var name = session.Peer.DisplayName;
        var at = _clock.RecordNow();

        // 2–3. 去重并保存：直接插入，命中 UNIQUE (peer, message_id) 即为重复消息（11.8）
        var r = _history.AppendText(new TextRecord(0, peer, name, m.MessageId, Direction.Receive, m.Body, m.SentAt, at, TextStatus.Received));
        switch (r)
        {
            case StoreResult.Ok or StoreResult.Duplicate:
                // 4. 已保存后才回复 TEXT_ACK（11.12），然后显示、通知、自动复制；重复消息只回复 ACK
                session.Send(new TextAck(m.MessageId));
                _shown[key] = true;
                if (r is StoreResult.Ok && seen is null)
                {
                    Show(key, name, body, m.SentAt, at);
                }

                break;

            case StoreResult.Failed f:
                _notices.OnNext(new TextNotice.StoreFailed(f.Detail));
                if (_history.FindText(peer, m.MessageId) is not null)
                {
                    // 写入失败但记录已存在（此前已保存）：按重复消息处理（11.8）
                    session.Send(new TextAck(m.MessageId));
                    _shown[key] = true;
                    return;
                }

                // 写入失败：不回复 ACK，但仍然显示；发送方重试时由已显示集合挡住重复显示（20.8）
                if (seen is null)
                {
                    _shown[key] = false;
                    Show(key, name, body, m.SentAt, at);
                }

                break;
        }
    }

    /// <summary>显示一条新收到的文字；处于 Background_State 时发通知（21.6），开启“自动复制”时排队写剪贴板（13.8）。</summary>
    private void Show(TextKey key, string peerName, string body, long sentAt, RecordTime at)
    {
        Append(new TextItem(key, Direction.Receive, body, sentAt, at, TextStatus.Received));
        if (_lifecycle.Background.Value)
        {
            _notifier.Text(new NotifyKey.Text(key.Peer, key.MessageId), peerName, TextRules.NotificationPreview(body));
        }

        if (_settings.Current().AutoCopy)
        {
            _copies.Writer.TryWrite(body);
        }
    }

    private void Append(TextItem item) => ConversationState(item.Key.Peer).Update(items => new ValueList<TextItem>([.. items, item]));

    private void Restore()
    {
        switch (_history.List())
        {
            case ListResult<HistoryRecord>.Failed f:
                _notices.OnNext(new TextNotice.StoreFailed(f.Detail));
                return;

            case ListResult<HistoryRecord>.Ok ok:
            {
                var byPeer = new Dictionary<DeviceId, List<TextItem>>();
                var order = new List<DeviceId>();
                var usable = new List<TextRecord>();
                foreach (var r in ok.Items.OfType<HistoryRecord.Text>().Select(t => t.Record))
                {
                    if (StrictUtf8.Decode(r.TextUtf8.Span) is not { } body)
                    {
                        continue;
                    }

                    usable.Add(r);
                    if (!byPeer.TryGetValue(r.Peer, out var list))
                    {
                        list = [];
                        byPeer[r.Peer] = list;
                        order.Add(r.Peer);
                    }

                    list.Add(new TextItem(new TextKey(r.Peer, r.MessageId), r.Direction, body, r.SentAtUtcMs, r.RecordedAt, r.Status));
                }

                foreach (var peer in order)
                {
                    var restored = byPeer[peer];
                    ConversationState(peer).Update(items => new ValueList<TextItem>([.. restored, .. items]));
                }

                Step(new TextEvent.Restored(new ValueList<TextRecord>(usable)));
                break;
            }
        }
    }

    private void ForgetKeys(IReadOnlySet<TextKey>? keys)
    {
        if (keys is null)
        {
            Step(new TextEvent.Forget(new ValueSet<TextKey>(_state.Outgoing.Keys)));
            List<ObservableState<ValueList<TextItem>>> all;
            lock (_conversationsGate)
            {
                all = [.. _conversations.Values];
            }

            foreach (var s in all)
            {
                s.Value = ValueList<TextItem>.Empty;
            }

            return;
        }

        Step(new TextEvent.Forget(new ValueSet<TextKey>(keys)));
        foreach (var peer in keys.Select(k => k.Peer).Distinct())
        {
            ConversationState(peer).Update(items => new ValueList<TextItem>(items.Where(i => !keys.Contains(i.Key))));
        }
    }

    private void Step(TextEvent e)
    {
        var s = TextReducer.Reduce(_state, e);
        _state = s.State;
        foreach (var f in s.Effects)
        {
            Execute(f);
        }
    }

    private void Execute(TextEffect f)
    {
        switch (f)
        {
            case TextEffect.Transmit t:
                if (_state.Outgoing.TryGetValue(new TextKey(t.Peer, t.Msg.MessageId), out var o) && o.Deadline is { } deadline)
                {
                    Outbox(t.Peer).Writer.TryWrite(new Outgoing(t.Msg, deadline));
                }

                break;

            case TextEffect.SaveStatus ss:
                Stored(_history.UpdateTextStatus(ss.Key.MessageId, ss.Key.Peer, ss.Status));
                ConversationState(ss.Key.Peer).Update(items =>
                    new ValueList<TextItem>(items.Select(i => i.Key == ss.Key && i.Direction == Direction.Send ? i with { Status = ss.Status } : i)));
                break;

            case TextEffect.ArmTimer at:
                ArmTimer(at.AtMono);
                break;
        }
    }

    private void ArmTimer(long atMono)
    {
        var ct = _cts.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // 等到单调时钟确实到达截止时刻再 Tick
                    long left;
                    while ((left = atMono - _clock.MonotonicMs()) > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(left), _clock.Time, ct).ConfigureAwait(false);
                    }

                    Post(new Cmd.Tick());
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
    }

    private Channel<Outgoing> Outbox(DeviceId peer)
    {
        if (_outboxes.TryGetValue(peer, out var ch))
        {
            return ch;
        }

        ch = Channel.CreateUnbounded<Outgoing>(new UnboundedChannelOptions { SingleReader = true });
        _outboxes[peer] = ch;
        var ct = _cts.Token;
        var reader = ch.Reader;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await foreach (var o in reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        await TransmitAsync(peer, o).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
        return ch;
    }

    private async Task TransmitAsync(DeviceId peer, Outgoing o)
    {
        // 已超过 10 s（已标为“发送失败”）的不再发出，由用户重试（11.6、11.7）
        if (_clock.MonotonicMs() >= o.Deadline)
        {
            return;
        }

        switch (await _sessions.OpenAsync(new Target.Device(peer)).ConfigureAwait(false))
        {
            case OpenResult.Ok ok:
                ok.Session.Send(o.Msg, Priority.Text);
                break;
            case OpenResult.Failed f:
                _notices.OnNext(new TextNotice.ConnectFailed(peer, f.Failure));
                break;
            case OpenResult.Pairing p:
                // 本机已不信任对方（例如刚解除配对）：不在这里配对，直接关闭
                p.Connection.Close();
                break;
        }
    }

    private void Stored(StoreResult r)
    {
        if (r is StoreResult.Failed f)
        {
            _notices.OnNext(new TextNotice.StoreFailed(f.Detail));
        }
    }

    /// <summary>记录中的对方名称快照（20.1）：可信设备名称，其次是当前 Session 中的名称。</summary>
    private string PeerName(DeviceId peer) =>
        _trust.Get(peer)?.DisplayName ?? (_sessions.Sessions.Value.TryGetValue(peer, out var info) ? info.Peer.DisplayName : string.Empty);
}

/// <summary>引擎内部的时钟小工具。</summary>
internal static class ClockExtensions
{
    /// <summary>当前时刻的记录时间：UTC 毫秒与本机当时的 UTC 偏移（分钟，20.1）。</summary>
    public static RecordTime RecordNow(this IClock clock)
    {
        var utc = clock.WallMs();
        var local = new DateTimeOffset(DateTime.SpecifyKind(clock.LocalNow(), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        return new RecordTime(utc, (int)Math.Floor((local - utc + 30_000) / 60_000.0));
    }
}
