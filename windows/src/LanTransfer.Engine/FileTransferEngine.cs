using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;

namespace LanTransfer.Engine;

// 文件传输引擎（设计 C4.5、C1.13），与 Kotlin 侧 FileTransferEngine.kt 同名镜像：执行 TransferReducer 的 effects。
//
// - 一个 actor 任务持有全部任务（TransferTask + 运行期状态）；收到的消息由分发器经 OnMessage 交进来。
// - FileSender：每个发送任务一个读盘任务，把 FILE_CHUNK / FILE_END 放进容量为 2 的 IChunkSource（P2），保证未确认数据 ≤ 4 MiB。
//   FILE_END 与数据块走同一个源，保证它排在该文件的全部数据块之后。
// - FileReceiver：每个接收任务一个写盘任务，按到达顺序处理该任务的 FILE_CHUNK / FILE_END / FILE_READ_FAILED 与校验、提交、删除。
//   actor 在入队前检查 offset 连续性与 4 MiB 窗口（违反时按 PROTOCOL_ERROR 关闭连接，E3）；写盘结果按顺序回到 actor 再交给归约器。
// - 读写盘在线程池上运行；计时一律用 IClock.Time。

public sealed class FileTransferEngine : IDisposable
{
    /// <summary>FileSender 的缓冲容量（C4.5）。</summary>
    private const int BufferCapacity = 2;

    private readonly SessionManager _sessions;
    private readonly IReceiveStorage _storage;
    private readonly ISourceFiles _sources;
    private readonly ITaskStore _store;
    private readonly ITrustStore _trust;
    private readonly INotifier _notifier;
    private readonly ISettingsStore _settings;
    private readonly IAppLifecycle _lifecycle;
    private readonly IOutgoingTextCache _textCache;
    private readonly IClock _clock;
    private readonly IRng _rng;
    private readonly FsProfile _profile;
    private readonly TransferTimings _timings;
    private readonly Channel<Cmd> _inbox = Channel.CreateUnbounded<Cmd>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ObservableState<ValueList<TransferTaskView>> _tasks = new(ValueList<TransferTaskView>.Empty);
    private readonly ObservableState<int> _unfinished = new(0);
    private readonly Subject<TransferNotice> _notices = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly IDisposable _sessionEvents;

    // ---- 只在 actor 中访问 ----

    /// <summary>按创建顺序。</summary>
    private readonly OrderedDictionary<TransferId, Entry> _entries = new();
    private bool _publishScheduled;
    private bool _janitorStarted;

    public FileTransferEngine(
        SessionManager sessions,
        IReceiveStorage storage,
        ISourceFiles sources,
        ITaskStore store,
        ITrustStore trust,
        INotifier notifier,
        ISettingsStore settings,
        IAppLifecycle lifecycle,
        IOutgoingTextCache textCache,
        IClock clock,
        IRng rng,
        FsProfile profile,
        TransferTimings? timings = null)
    {
        _sessions = sessions;
        _storage = storage;
        _sources = sources;
        _store = store;
        _trust = trust;
        _notifier = notifier;
        _settings = settings;
        _lifecycle = lifecycle;
        _textCache = textCache;
        _clock = clock;
        _rng = rng;
        _profile = profile;
        _timings = timings ?? TransferTimings.Default;
        var ct = _cts.Token;
        _ = Task.Run(() => ActorAsync(ct), CancellationToken.None);
        _sessionEvents = sessions.Events.Subscribe(e =>
        {
            switch (e)
            {
                case SessionEvent.Opened o:
                    Post(new Cmd.SessionOpened(o.Session));
                    break;
                case SessionEvent.Closed c:
                    Post(new Cmd.SessionClosed(c.Session, c.Reason));
                    break;
            }
        });
    }

    public IObservableState<ValueList<TransferTaskView>> Tasks => _tasks;

    public IObservable<TransferNotice> Notices => _notices;

    /// <summary>Offering、AwaitingUser、Running、Resuming、Interrupted 的任务数（20.11）。</summary>
    public IObservableState<int> UnfinishedCount => _unfinished;

    public void Dispose()
    {
        _sessionEvents.Dispose();
        _cts.Cancel();
    }

    /// <summary>点击“发送”（14.4、14.10）：读取元数据，10 s 内建立 Session，然后创建任务并发出 FILE_OFFER。无法建立 Session 时不发送 Offer。</summary>
    public async Task<SendOfferResult> SendAsync(DeviceId peer, IReadOnlyList<SourceRef> files)
    {
        if (files.Count == 0)
        {
            return new SendOfferResult.Empty();
        }

        if (files.Count > ProtocolConstants.MaxFilesPerOffer)
        {
            return new SendOfferResult.TooMany();
        }

        var metas = files.Select(f => (Ref: f, Meta: _sources.Describe(f))).ToList();
        var bad = metas.Where(m => m.Meta is null || m.Meta.IsDirectory || (ulong)m.Meta.Size > ProtocolConstants.MaxFileSize).Select(m => m.Ref).ToList();
        if (bad.Count > 0)
        {
            return new SendOfferResult.Unreadable(new ValueList<SourceRef>(bad));
        }

        Session session;
        switch (await _sessions.OpenAsync(new Target.Device(peer)).ConfigureAwait(false))
        {
            case OpenResult.Ok ok:
                session = ok.Session;
                break;
            case OpenResult.Failed f:
                return new SendOfferResult.ConnectFailed(f.Failure);
            case OpenResult.Pairing p:
                p.Connection.Close();
                return new SendOfferResult.ConnectFailed(new OpenFailure.NotTrusted());
            default:
                throw new InvalidOperationException();
        }

        var done = new TaskCompletionSource<TransferId>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new Cmd.StartSend(session, [.. metas.Select(m => (m.Ref, m.Meta!))], done));
        return new SendOfferResult.Sent(await done.Task.ConfigureAwait(false));
    }

    /// <summary>接收方点击“接收”（15.6）：固定当前接收目录（16.3），写第一个字节之前创建目录（16.14）。</summary>
    public void Accept(TransferId id) => Post(new Cmd.Accept(id));

    /// <summary>接收方点击“拒绝”（15.3）。</summary>
    public void Reject(TransferId id) => Post(new Cmd.Reject(id));

    /// <summary>取消（18.2、18.3、18.9）。</summary>
    public void Cancel(TransferId id) => Post(new Cmd.Cancel(id));

    /// <summary>“继续”（17.4、17.10）：10 s 内重新建立 Session 并交换 RESUME_STATE / RESUME_PLAN。</summary>
    public Task<ResumeResult> ResumeAsync(TransferId id)
    {
        var d = new TaskCompletionSource<ResumeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new Cmd.Resume(id, d));
        return d.Task;
    }

    /// <summary>启动恢复（17.9、M3.2）：未结束的任务 → “已中断” / “连接已断开”，随后运行一次 TempFileJanitor，之后每小时一次（17.7）。</summary>
    public Task RestoreOnStartupAsync() => RequestAsync(d => new Cmd.Restore(d));

    /// <summary>清空记录前放弃全部未结束任务（20.5）；返回后这些任务不再出现在 Tasks 中。</summary>
    public Task AbandonAllAsync() => RequestAsync(d => new Cmd.AbandonAll(d));

    /// <summary>删除“已中断”任务的文件记录前取消整个任务（20.10）。</summary>
    public Task AbandonAsync(TransferId id) => RequestAsync(d => new Cmd.Abandon(id, d));

    /// <summary>解除配对（Q7）：对该对端全部未结束的任务按本机取消处理，并发送 TRANSFER_CANCEL(UNPAIRED)。供 PairingService.UnpairAsync 使用。</summary>
    public Task CancelPeerAsync(DeviceId peer) => RequestAsync(d => new Cmd.CancelPeer(peer, d));

    /// <summary>立即运行一次 TempFileJanitor（测试与宿主使用）。</summary>
    public Task RunJanitorAsync() => RequestAsync(d => new Cmd.Janitor(d));

    /// <summary>分发器交来的应用消息；文件传输相关的消息返回 true。</summary>
    public bool OnMessage(Session session, ProtocolMessage msg)
    {
        if (msg is not (FileOffer or FileOfferReply or FileChunk or FileEnd or FileAck or FileResult or FileReadFailed
            or TransferCancel or ResumeRequest or ResumeState or ResumePlan))
        {
            return false;
        }

        Post(new Cmd.Inbound(session, msg));
        return true;
    }

    private async Task RequestAsync(Func<TaskCompletionSource, Cmd> make)
    {
        var d = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(make(d));
        await d.Task.ConfigureAwait(false);
    }

    private void Post(Cmd cmd) => _inbox.Writer.TryWrite(cmd);

    // ---- 命令 ----

    private abstract record Cmd
    {
        public sealed record StartSend(Session Session, IReadOnlyList<(SourceRef Ref, SourceMeta Meta)> Files, TaskCompletionSource<TransferId> Done) : Cmd;

        public sealed record Accept(TransferId Id) : Cmd;

        public sealed record Reject(TransferId Id) : Cmd;

        public sealed record Cancel(TransferId Id) : Cmd;

        public sealed record Resume(TransferId Id, TaskCompletionSource<ResumeResult> Done) : Cmd;

        public sealed record ResumeOpened(TransferId Id, object Attempt, OpenResult Result, TaskCompletionSource<ResumeResult>? Done) : Cmd;

        public sealed record ResumeStateReady(TransferId Id, object Attempt, Session Session, ValueList<ResumeFileState> States, IReadOnlyDictionary<int, (long Length, IncrementalHash Hash)> Digests) : Cmd;

        public sealed record ResumeTimeout(TransferId Id, object Attempt) : Cmd;

        public sealed record Restore(TaskCompletionSource Done) : Cmd;

        public sealed record AbandonAll(TaskCompletionSource Done) : Cmd;

        public sealed record Abandon(TransferId Id, TaskCompletionSource Done) : Cmd;

        public sealed record CancelPeer(DeviceId Peer, TaskCompletionSource Done) : Cmd;

        public sealed record Janitor(TaskCompletionSource? Done) : Cmd;

        public sealed record Inbound(Session Session, ProtocolMessage Msg) : Cmd;

        public sealed record SessionOpened(Session Session) : Cmd;

        public sealed record SessionClosed(Session Session, CloseReason Reason) : Cmd;

        public sealed record OfferTimeout(TransferId Id) : Cmd;

        /// <summary>发送方读盘任务的结果。</summary>
        public sealed record SenderEvent(Sender Sender, TransferEvent Event) : Cmd;

        public sealed record SenderChecking(Sender Sender, bool On) : Cmd;

        /// <summary>接收方写盘任务的结果。</summary>
        public sealed record ReceiverEvent(Receiver Receiver, TransferEvent Event) : Cmd;

        public sealed record TempReady(Receiver Receiver, int Index, TempRef Ref) : Cmd;

        public sealed record Publish : Cmd;
    }

    /// <summary>一个任务的运行期状态。</summary>
    private sealed class Entry(TransferTask task)
    {
        public TransferTask Task { get; set; } = task;

        public Session? Session { get; set; }

        public CancellationTokenSource? OfferTimer { get; set; }

        /// <summary>已显示文件接收请求通知（21.8）。</summary>
        public bool RequestShown { get; set; }

        /// <summary>Resuming → Running 时要先删除的临时文件（Restart），交给新的写盘任务按顺序处理。</summary>
        public List<(int Index, TempRef Ref)> PendingDeletes { get; } = [];

        public Sender? Sender { get; set; }

        public Receiver? Receiver { get; set; }

        public ProgressCalculator Progress { get; } = new();

        public bool CheckingPrefix { get; set; }

        public TransferTask? LastSaved { get; set; }

        public long LastSavedAt { get; set; } = long.MinValue / 2;

        /// <summary>当前这次续传尝试的标识（过期的结果按它丢弃）与续传前的中断原因（10 s 内未完成时恢复，17.10）。</summary>
        public object? ResumeAttempt { get; set; }

        public CancellationTokenSource? ResumeJob { get; set; }

        public CancellationTokenSource? ResumeTimer { get; set; }

        public InterruptReason? ResumeReason { get; set; }

        /// <summary>接收方续传前预先计算的临时文件哈希：index → (长度, 哈希状态)。</summary>
        public IReadOnlyDictionary<int, (long Length, IncrementalHash Hash)> PrefixDigests { get; set; } = new Dictionary<int, (long, IncrementalHash)>();

        /// <summary>后台进度通知（21.1）。</summary>
        public long ProgressNotifiedAt { get; set; } = long.MinValue / 2;

        public bool ProgressShown { get; set; }

        public TransferId Id => Task.TransferId;

        public DeviceId Peer => Task.Peer;
    }

    // ---- actor ----

    private async Task ActorAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var cmd in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    Dispatch(cmd);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // 兜底（设计“处理原则”）：未预期的异常不让 actor 退出
                    _notices.OnNext(new TransferNotice.StoreFailed($"internal error: {e.GetType().Name}"));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Entry? Find(TransferId id) => _entries.TryGetValue(id, out var e) ? e : null;

    private void Dispatch(Cmd cmd)
    {
        switch (cmd)
        {
            case Cmd.StartSend s:
                StartSend(s);
                break;
            case Cmd.Accept a:
                if (Find(a.Id) is { } ea && ea.Task.Direction == Direction.Receive && ea.Task.State == TaskState.AwaitingUser)
                {
                    AcceptNow(ea);
                }

                break;
            case Cmd.Reject r:
                if (Find(r.Id) is { } er)
                {
                    Step(er, new TransferEvent.UserRejected());
                }

                break;
            case Cmd.Cancel c:
                if (Find(c.Id) is { } ec)
                {
                    LocalCancel(ec, TransferCancelReason.User);
                }

                break;
            case Cmd.Resume rs:
                StartResumeByUser(rs);
                break;
            case Cmd.ResumeOpened ro:
                OnResumeOpened(ro);
                break;
            case Cmd.ResumeStateReady rr:
                OnResumeStateReady(rr);
                break;
            case Cmd.ResumeTimeout rt:
                if (Find(rt.Id) is { } et && ReferenceEquals(et.ResumeAttempt, rt.Attempt) && et.Task.State == TaskState.Resuming)
                {
                    Step(et, new TransferEvent.ResumeFailed(et.ResumeReason ?? InterruptReason.PeerUnresponsive));
                }

                break;
            case Cmd.Restore re:
                Restore();
                re.Done.TrySetResult();
                break;
            case Cmd.AbandonAll aa:
                foreach (var e in _entries.Values.ToList())
                {
                    if (!e.Task.State.IsTerminal())
                    {
                        LocalCancel(e, TransferCancelReason.User);
                    }
                }

                foreach (var e in _entries.Values.ToList())
                {
                    Dispose(e);
                }

                _entries.Clear();
                Publish();
                aa.Done.TrySetResult();
                break;
            case Cmd.Abandon ab:
                if (Find(ab.Id) is { } eb)
                {
                    LocalCancel(eb, TransferCancelReason.User);
                }

                ab.Done.TrySetResult();
                break;
            case Cmd.CancelPeer cp:
                foreach (var e in _entries.Values.Where(x => x.Peer == cp.Peer && !x.Task.State.IsTerminal()).ToList())
                {
                    LocalCancel(e, TransferCancelReason.Unpaired);
                }

                cp.Done.TrySetResult();
                break;
            case Cmd.Janitor j:
                RunJanitor();
                j.Done?.TrySetResult();
                break;
            case Cmd.Inbound ib:
                Inbound(ib.Session, ib.Msg);
                break;
            case Cmd.SessionOpened so:
                DeliverPendingCancels(so.Session);
                break;
            case Cmd.SessionClosed sc:
                if (sc.Reason.ToInterruptReason() is { } reason)
                {
                    foreach (var e in _entries.Values.ToList())
                    {
                        if (ReferenceEquals(e.Session, sc.Session) && !e.Task.State.IsTerminal())
                        {
                            Step(e, new TransferEvent.Disconnected(reason));
                        }
                    }
                }

                break;
            case Cmd.OfferTimeout ot:
                if (Find(ot.Id) is { } eo)
                {
                    Step(eo, new TransferEvent.OfferTimedOut());
                }

                break;
            case Cmd.SenderEvent se:
                if (EntryOf(se.Sender) is { } es)
                {
                    Step(es, se.Event);
                }

                break;
            case Cmd.SenderChecking sch:
                if (EntryOf(sch.Sender) is { } esc)
                {
                    esc.CheckingPrefix = sch.On;
                    Publish();
                }

                break;
            case Cmd.ReceiverEvent rev:
                OnReceiverEvent(rev);
                break;
            case Cmd.TempReady tr:
                if (EntryOf(tr.Receiver) is { } etr)
                {
                    var pos = IndexOfFile(etr.Task, tr.Index);
                    if (pos >= 0 && etr.Task.Files[pos].TempRef != tr.Ref)
                    {
                        etr.Task = WithFile(etr.Task, pos, etr.Task.Files[pos] with { TempRef = tr.Ref });
                        Save(etr, force: true);
                    }
                }

                break;
            case Cmd.Publish:
                _publishScheduled = false;
                Publish();
                break;
        }
    }

    private Entry? EntryOf(Sender s) => Find(s.Id) is { } e && ReferenceEquals(e.Sender, s) ? e : null;

    private Entry? EntryOf(Receiver r) => Find(r.Id) is { } e && ReferenceEquals(e.Receiver, r) ? e : null;

    private static int IndexOfFile(TransferTask t, int index)
    {
        for (var i = 0; i < t.Files.Count; i++)
        {
            if (t.Files[i].Index == index)
            {
                return i;
            }
        }

        return -1;
    }

    private static TransferTask WithFile(TransferTask t, int pos, TransferFile f) =>
        t with { Files = new ValueList<TransferFile>(t.Files.Select((x, i) => i == pos ? f : x)) };

    // ---- 发送方：创建任务与 Offer ----

    private void StartSend(Cmd.StartSend cmd)
    {
        TransferId id;
        do
        {
            id = new TransferId(Bytes16.Of(_rng.NextBytes(16)));
        }
        while (_entries.ContainsKey(id));

        var files = new ValueList<TransferFile>(cmd.Files.Select((f, i) =>
            new TransferFile(id, i, 0, f.Meta.Name, f.Meta.Size, f.Meta.Mime, f.Meta.Mtime, FileState.Waiting, SourceRef: f.Ref)));
        var peer = cmd.Session.Peer.DeviceId;
        var task = new TransferTask(id, peer, PeerName(peer, cmd.Session), Direction.Send, TaskState.Offering, null, null, _clock.RecordNow(), _clock.WallMs(), files);
        var e = new Entry(task) { Session = cmd.Session };
        _entries[id] = e;
        Save(e, force: true);
        var offer = new FileOffer(id, new ValueList<FileEntry>(files.Select(f => new FileEntry(f.OriginalName, (ulong)f.Size, f.Mime, f.Mtime))));
        e.OfferTimer = StartTimer(_timings.SenderOfferMs, () => new Cmd.OfferTimeout(id));
        Publish();
        if (!cmd.Session.Send(offer))
        {
            Step(e, new TransferEvent.Disconnected(InterruptReason.PeerClosed));
        }

        cmd.Done.TrySetResult(id);
    }

    // ---- 收到的消息 ----

    private void Inbound(Session session, ProtocolMessage msg)
    {
        if (msg is FileOffer offer)
        {
            OnOffer(session, offer);
            return;
        }

        TransferId id;
        switch (msg)
        {
            case FileOfferReply m: id = m.TransferId; break;
            case FileChunk m: id = m.TransferId; break;
            case FileEnd m: id = m.TransferId; break;
            case FileAck m: id = m.TransferId; break;
            case FileResult m: id = m.TransferId; break;
            case FileReadFailed m: id = m.TransferId; break;
            case TransferCancel m: id = m.TransferId; break;
            case ResumeRequest m: id = m.TransferId; break;
            case ResumeState m: id = m.TransferId; break;
            case ResumePlan m: id = m.TransferId; break;
            default: return;
        }

        // 只有该任务的对端才能操作它
        var e = Find(id);
        if (e is not null && e.Peer != session.Peer.DeviceId)
        {
            e = null;
        }

        if (e is null)
        {
            // E3：未知任务的 FILE_*、RESUME_* 回复 UNKNOWN_TRANSFER；TRANSFER_CANCEL、FILE_ACK、FILE_RESULT、FILE_OFFER_REPLY 忽略
            if (msg is FileChunk or FileEnd or FileReadFailed or ResumeRequest or ResumeState or ResumePlan)
            {
                session.Send(new TransferCancel(id, TransferCancelReason.UnknownTransfer));
            }

            return;
        }

        if (e.Task.State.IsTerminal())
        {
            // 迟到的消息忽略。已结束的任务被请求续传时：本机作为接收方已完成（对方没收到最后的 FILE_RESULT）→ 回复全部已结束的
            // RESUME_STATE，对方据此全部 SKIP 并完成；其他情况告知对方已取消。
            if (msg is ResumeRequest && e.Task.Direction == Direction.Receive && e.Task.State == TaskState.Completed)
            {
                session.Send(new ResumeState(id, new ValueList<ResumeFileState>(e.Task.Files.Select(f =>
                    new ResumeFileState((uint)f.Index, f.State == FileState.Completed ? ResumeFileStatus.Completed : ResumeFileStatus.Terminal, 0)))));
            }
            else if (msg is ResumeRequest or ResumeState)
            {
                session.Send(new TransferCancel(id, TransferCancelReason.User));
            }

            return;
        }

        var same = ReferenceEquals(e.Session, session);
        switch (msg)
        {
            case FileOfferReply m when same:
                Step(e, new TransferEvent.OfferReplied(m.Decision));
                break;
            case FileAck m when same:
                e.Sender?.OnAck((int)m.FileIndex, (long)m.Persisted);
                Step(e, new TransferEvent.AckReceived((int)m.FileIndex, (long)m.Persisted));
                break;
            case FileResult m when same:
                Step(e, new TransferEvent.ResultReceived((int)m.FileIndex, m.Status, m.Detail));
                break;
            case TransferCancel m:
                Step(e, new TransferEvent.PeerCancelled(m.Reason));
                break;
            case FileChunk or FileEnd or FileReadFailed when same:
                OnWireFileMessage(e, session, msg);
                break;
            case ResumeRequest:
                OnResumeRequest(e, session);
                break;
            case ResumeState m:
                OnResumeState(e, session, m);
                break;
            case ResumePlan m:
                OnResumePlan(e, session, m);
                break;
        }
    }

    private void OnOffer(Session session, FileOffer offer)
    {
        var peer = session.Peer.DeviceId;
        if (_entries.ContainsKey(offer.TransferId))
        {
            return; // 重复的 Offer
        }

        var dir = _settings.Current().ReceiveDirectory;
        var autoAccept = _trust.Get(peer)?.AutoAccept == true;
        var decision = OfferPrecheck.Decide(offer, _storage.Probe(dir), _storage.FreeSpace(dir), autoAccept);
        if (decision is PrecheckDecision.Reject { Reply: OfferDecision.InvalidOffer })
        {
            session.Send(new FileOfferReply(offer.TransferId, OfferDecision.InvalidOffer)); // 不创建任务（E3）
            return;
        }

        var files = new ValueList<TransferFile>(offer.Files.Select((f, i) =>
            new TransferFile(offer.TransferId, i, 0, f.Name, (long)f.Size, f.Mime, f.Mtime, FileState.Waiting)));
        var state = decision is PrecheckDecision.Reject rj
            ? (rj.Reply == OfferDecision.NoSpace ? TaskState.NoSpace : TaskState.CannotSave)
            : TaskState.AwaitingUser;
        var task = new TransferTask(offer.TransferId, peer, PeerName(peer, session), Direction.Receive, state, null, null, _clock.RecordNow(), _clock.WallMs(), files);
        var e = new Entry(task) { Session = session };
        _entries[offer.TransferId] = e;
        Save(e, force: true);
        switch (decision)
        {
            case PrecheckDecision.Reject r:
                session.Send(new FileOfferReply(offer.TransferId, r.Reply));
                break;
            case PrecheckDecision.AutoAccept:
                AcceptNow(e);
                return;
            default:
                e.OfferTimer = StartTimer(_timings.ReceiverOfferMs, () => new Cmd.OfferTimeout(offer.TransferId));
                if (_lifecycle.Background.Value)
                {
                    _notifier.Request(new NotifyKey.Transfer(offer.TransferId), task.PeerName, RequestKind.FileOffer, files.Count);
                    e.RequestShown = true;
                }

                break;
        }

        Publish();
    }

    private void AcceptNow(Entry e)
    {
        var dir = _settings.Current().ReceiveDirectory;
        Step(e, new TransferEvent.UserAccepted(dir, _storage.EnsureDirectory(dir)));
    }

    // ---- 接收方：数据 ----

    private void OnWireFileMessage(Entry e, Session session, ProtocolMessage msg)
    {
        var rx = e.Receiver;
        if (e.Task.Direction != Direction.Receive || e.Task.State != TaskState.Running || rx is null)
        {
            return;
        }

        var index = msg switch
        {
            FileChunk m => (int)m.FileIndex,
            FileEnd m => (int)m.FileIndex,
            FileReadFailed m => (int)m.FileIndex,
            _ => -1,
        };
        var pos = IndexOfFile(e.Task, index);
        if (pos < 0 || pos > rx.WirePos)
        {
            Violation(session, $"file index {index} out of order");
            return;
        }

        if (pos < rx.WirePos)
        {
            return; // 已结束的文件仍在途的数据（如判定校验失败之后）
        }

        switch (msg)
        {
            case FileChunk m:
            {
                var n = (long)m.Data.Length;
                if ((long)m.Offset != rx.Expected)
                {
                    Violation(session, $"offset {m.Offset} != {rx.Expected}");
                    return;
                }

                if (Interlocked.Read(ref rx.Queued) + n > _timings.WindowBytes)
                {
                    Violation(session, "window exceeded");
                    return;
                }

                rx.Expected += n;
                Interlocked.Add(ref rx.Queued, n);
                rx.Submit(new WriteCmd.Chunk(index, (long)m.Offset, m.Data));
                break;
            }

            case FileEnd m:
                rx.Submit(new WriteCmd.End(index, m.Sha256, rx.StartOf.GetValueOrDefault(index)));
                AdvanceWire(e, rx, pos + 1);
                break;
            case FileReadFailed:
                rx.Dropped[index] = true;
                rx.Submit(new WriteCmd.PeerReadFailed(index));
                AdvanceWire(e, rx, pos + 1);
                break;
        }
    }

    /// <summary>把接收方的“线上当前文件”移到 from 起第一个未结束的文件。</summary>
    private static void AdvanceWire(Entry e, Receiver rx, int from)
    {
        var p = from;
        while (p < e.Task.Files.Count && e.Task.Files[p].State.IsTerminal())
        {
            p++;
        }

        rx.WirePos = p;
        rx.Expected = p < e.Task.Files.Count ? rx.StartOf.GetValueOrDefault(e.Task.Files[p].Index) : 0;
    }

    private static void Violation(Session session, string detail) => session.Connection.Close(new CloseReason.ProtocolError(detail));

    private void OnReceiverEvent(Cmd.ReceiverEvent cmd)
    {
        if (Find(cmd.Receiver.Id) is not { } e)
        {
            return;
        }

        // 断开后写盘任务仍可能完成提交：文件已经以最终名称保存，按已完成记录（否则续传会重复接收）
        if (cmd.Event is TransferEvent.VerifyDone { Outcome: VerifyOutcome.Committed o } vd && e.Task.State == TaskState.Interrupted)
        {
            var pos = IndexOfFile(e.Task, vd.Index);
            if (pos >= 0 && e.Task.Files[pos].State == FileState.Verifying)
            {
                var f = e.Task.Files[pos];
                e.Task = WithFile(e.Task, pos, f with { State = FileState.Completed, Persisted = f.Size, FinalName = o.FinalName, FullPath = o.FullPath, StoredRef = o.StoredRef });
                Save(e, force: true);
                Publish();
            }

            return;
        }

        if (!ReferenceEquals(e.Receiver, cmd.Receiver))
        {
            return;
        }

        Step(e, cmd.Event);
    }

    // ---- 续传 ----

    private void StartResumeByUser(Cmd.Resume cmd)
    {
        var e = Find(cmd.Id);
        if (e is null || e.Task.State != TaskState.Interrupted)
        {
            cmd.Done.TrySetResult(new ResumeResult.NotResumable());
            return;
        }

        e.ResumeReason = e.Task.Interrupt?.Reason;
        StepWith(e, new TransferEvent.ResumeRequested(), f =>
        {
            if (f is TransferEffect.StartResume)
            {
                OpenForResume(e, cmd.Done);
            }
            else
            {
                Execute(e, f);
            }
        });
        if (e.Task.State != TaskState.Resuming)
        {
            cmd.Done.TrySetResult(new ResumeResult.NotResumable());
        }
    }

    /// <summary>用户点击“继续”：在 10 s 预算内重新建立 Session。</summary>
    private void OpenForResume(Entry e, TaskCompletionSource<ResumeResult>? done)
    {
        var attempt = new object();
        var id = e.Id;
        var peer = e.Peer;
        e.ResumeAttempt = attempt;
        CancelTimer(e.ResumeTimer);
        e.ResumeTimer = StartTimer(_timings.ResumeMs, () => new Cmd.ResumeTimeout(id, attempt));
        var job = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        e.ResumeJob = job;
        _ = Task.Run(
            async () =>
            {
                var r = await _sessions.OpenAsync(new Target.Device(peer)).ConfigureAwait(false);
                Post(new Cmd.ResumeOpened(id, attempt, r, done));
            },
            CancellationToken.None);
    }

    private void OnResumeOpened(Cmd.ResumeOpened cmd)
    {
        var e = Find(cmd.Id);
        if (e is null || !ReferenceEquals(e.ResumeAttempt, cmd.Attempt) || e.Task.State != TaskState.Resuming)
        {
            (cmd.Result as OpenResult.Pairing)?.Connection.Close();
            cmd.Done?.TrySetResult(new ResumeResult.NotResumable());
            return;
        }

        switch (cmd.Result)
        {
            case OpenResult.Ok ok:
                e.Session = ok.Session;
                if (e.Task.Direction == Direction.Send)
                {
                    if (!ok.Session.Send(new ResumeRequest(e.Id)))
                    {
                        Step(e, new TransferEvent.Disconnected(InterruptReason.PeerClosed));
                    }
                }
                else
                {
                    PrepareResumeState(e, ok.Session, cmd.Attempt);
                }

                cmd.Done?.TrySetResult(new ResumeResult.Started());
                break;
            case OpenResult.Failed f:
                Step(e, new TransferEvent.ResumeFailed(e.ResumeReason ?? InterruptReason.PeerUnresponsive));
                cmd.Done?.TrySetResult(new ResumeResult.ConnectFailed(f.Failure));
                break;
            case OpenResult.Pairing p:
                p.Connection.Close();
                Step(e, new TransferEvent.ResumeFailed(e.ResumeReason ?? InterruptReason.PeerUnresponsive));
                cmd.Done?.TrySetResult(new ResumeResult.ConnectFailed(new OpenFailure.NotTrusted()));
                break;
        }
    }

    /// <summary>对方发来 RESUME_REQUEST：本机是接收方，回复 RESUME_STATE（17.4，不需要再次确认）。</summary>
    private void OnResumeRequest(Entry e, Session session)
    {
        if (e.Task.Direction != Direction.Receive)
        {
            return;
        }

        if (e.Task.State == TaskState.Interrupted)
        {
            e.ResumeReason = e.Task.Interrupt?.Reason;
            var attempt = new object();
            var id = e.Id;
            e.ResumeAttempt = attempt;
            e.Session = session;
            StepWith(e, new TransferEvent.ResumeRequested(), f =>
            {
                if (f is not TransferEffect.StartResume)
                {
                    Execute(e, f);
                }
            });
            e.ResumeTimer = StartTimer(_timings.ResumeMs, () => new Cmd.ResumeTimeout(id, attempt));
            PrepareResumeState(e, session, attempt);
        }
        else if (e.Task.State == TaskState.Resuming)
        {
            // 双方同时点击“继续”：用对方的这条 Session 回复
            var attempt = new object();
            e.ResumeAttempt = attempt;
            e.Session = session;
            PrepareResumeState(e, session, attempt);
        }
    }

    /// <summary>接收方：在线程池上读取临时文件长度并预先计算其哈希（之后续传从这里接着计算），再回到 actor 发送 RESUME_STATE。</summary>
    private void PrepareResumeState(Entry e, Session session, object attempt)
    {
        e.CheckingPrefix = true;
        Publish();
        var files = e.Task.Files;
        var id = e.Id;
        var previous = e.Receiver?.Job;
        var job = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        e.ResumeJob = job;
        var ct = job.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    if (previous is not null)
                    {
                        await previous.WaitAsync(ct).ConfigureAwait(false); // 先等上一次的写盘任务写完已收到的数据
                    }

                    var states = new List<ResumeFileState>(files.Count);
                    var digests = new Dictionary<int, (long, IncrementalHash)>();
                    foreach (var f in files)
                    {
                        var u = (uint)f.Index;
                        if (f.State.IsTerminal())
                        {
                            states.Add(new ResumeFileState(u, f.State == FileState.Completed ? ResumeFileStatus.Completed : ResumeFileStatus.Terminal, 0));
                            continue;
                        }

                        var hashed = f.TempRef is { } temp ? HashTemp(temp, null) : null;
                        if (hashed is not { } h)
                        {
                            states.Add(new ResumeFileState(u, ResumeFileStatus.None, 0));
                        }
                        else
                        {
                            digests[f.Index] = h;
                            states.Add(new ResumeFileState(u, ResumeFileStatus.Partial, (ulong)h.Length));
                        }
                    }

                    Post(new Cmd.ResumeStateReady(id, attempt, session, new ValueList<ResumeFileState>(states), digests));
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
    }

    private void OnResumeStateReady(Cmd.ResumeStateReady cmd)
    {
        if (Find(cmd.Id) is not { } e || !ReferenceEquals(e.ResumeAttempt, cmd.Attempt) || e.Task.State != TaskState.Resuming)
        {
            return;
        }

        e.CheckingPrefix = false;
        e.PrefixDigests = cmd.Digests;
        Publish();
        if (!cmd.Session.Send(new ResumeState(e.Id, cmd.States)))
        {
            Step(e, new TransferEvent.Disconnected(InterruptReason.PeerClosed));
        }
    }

    /// <summary>本机是发送方：收到 RESUME_STATE，由 ResumePlanner 生成计划（17.4、17.5）。</summary>
    private void OnResumeState(Entry e, Session session, ResumeState msg)
    {
        if (e.Task.Direction != Direction.Send)
        {
            return;
        }

        if (e.Task.State == TaskState.Interrupted)
        {
            // 接收方点击了“继续”
            e.ResumeReason = e.Task.Interrupt?.Reason;
            StepWith(e, new TransferEvent.ResumeRequested(), f =>
            {
                if (f is not TransferEffect.StartResume)
                {
                    Execute(e, f);
                }
            });
        }

        if (e.Task.State != TaskState.Resuming)
        {
            return;
        }

        CancelTimer(e.ResumeTimer);
        e.ResumeAttempt = null;
        e.Session = session;
        var stats = e.Task.Files.Select(f => f.SourceRef is { } r ? _sources.Stat(r) : null).ToList();
        Step(e, new TransferEvent.ResumePlanned(new ValueList<ResumePlanEntry>(ResumePlanner.Plan(e.Task, msg.Files, stats))));
    }

    /// <summary>本机是接收方：收到 RESUME_PLAN。</summary>
    private void OnResumePlan(Entry e, Session session, ResumePlan msg)
    {
        if (e.Task.Direction != Direction.Receive || e.Task.State != TaskState.Resuming || !ReferenceEquals(e.Session, session))
        {
            return;
        }

        CancelTimer(e.ResumeTimer);
        e.ResumeAttempt = null;
        e.CheckingPrefix = false;
        var plan = msg.Files.Select(p => new ResumePlanEntry((int)p.FileIndex, p.Action, (long)p.Start, p.Size is { } s ? (long)s : null, p.Mtime)).ToList();
        Step(e, new TransferEvent.ResumePlanned(new ValueList<ResumePlanEntry>(plan)));
        if (e.Receiver is not { } rx)
        {
            return;
        }

        foreach (var p in plan.Where(p => p.Action == ResumeAction.Continue))
        {
            rx.StartOf[p.Index] = p.Start;
        }

        AdvanceWire(e, rx, 0);
    }

    // ---- 取消与放弃 ----

    private void LocalCancel(Entry e, TransferCancelReason reason)
    {
        var active = e.Session?.IsOpen == true && e.Task.State != TaskState.Interrupted;
        Step(e, new TransferEvent.LocalCancel(active, reason));
    }

    /// <summary>与该对端的 Session 建立后（以及写入 pending_cancel 时恰好有 Session），发出待送达的取消通知（18.9）。</summary>
    private void DeliverPendingCancels(Session session)
    {
        if (!session.IsOpen)
        {
            return;
        }

        var peer = session.Peer.DeviceId;
        foreach (var id in _store.TakePendingCancels(peer))
        {
            // 发不出去（连接正在关闭）时放回，下次 Session 建立后再发
            if (!session.Send(new TransferCancel(id, TransferCancelReason.User)))
            {
                _store.AddPendingCancel(peer, id);
            }
        }
    }

    // ---- 启动恢复与清理 ----

    private void Restore()
    {
        foreach (var t in _store.Unfinished())
        {
            if (_entries.ContainsKey(t.TransferId))
            {
                continue;
            }

            var e = new Entry(t);
            _entries[t.TransferId] = e;
            StepWith(e, new TransferEvent.Restored(), f => Execute(e, f));
            Save(e, force: true);
        }

        // 启动时删除没有被任何未结束发送任务引用的 .txt 缓存（M4）
        var referenced = _entries.Values.Where(x => x.Task.Direction == Direction.Send && !x.Task.State.IsTerminal())
            .SelectMany(x => x.Task.Files.Where(f => f.SourceRef is not null).Select(f => f.SourceRef!.Value)).ToHashSet();
        foreach (var r in _textCache.List())
        {
            if (!referenced.Contains(r))
            {
                _textCache.Delete(r);
            }
        }

        RunJanitor();
        if (!_janitorStarted)
        {
            _janitorStarted = true;
            var ct = _cts.Token;
            var next = _clock.MonotonicMs() + _timings.JanitorIntervalMs;
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        while (true)
                        {
                            long left;
                            while ((left = next - _clock.MonotonicMs()) > 0)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(left), _clock.Time, ct).ConfigureAwait(false);
                            }

                            Post(new Cmd.Janitor(null));
                            next += _timings.JanitorIntervalMs;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                },
                CancellationToken.None);
        }

        Publish();
    }

    /// <summary>TempFileJanitor（17.7）：删除不属于进行中任务、且超过 24 h 未写入的临时文件。</summary>
    private void RunJanitor()
    {
        var active = _entries.Values.Where(x => x.Task.State.IsActive()).Select(x => x.Id).ToHashSet();
        var lastWrite = new Dictionary<TempRef, long>();
        foreach (var f in _entries.Values.SelectMany(x => x.Task.Files))
        {
            if (f.TempRef is { } r && f.LastWriteAtUtcMs is { } w)
            {
                lastWrite[r] = w;
            }
        }

        var temps = _storage.ListTemps().Select(t => lastWrite.TryGetValue(t.Ref, out var w) && w > t.LastWriteUtcMs ? t with { LastWriteUtcMs = w } : t);
        foreach (var t in TempFileSelector.Select(temps, active, _clock.WallMs()))
        {
            _storage.Delete(t.Ref);
        }
    }

    // ---- 归约与 effects ----

    private void Step(Entry e, TransferEvent ev) => StepWith(e, ev, f => Execute(e, f));

    private void StepWith(Entry e, TransferEvent ev, Action<TransferEffect> run)
    {
        var before = e.Task;
        var s = TransferReducer.Reduce(before, ev);
        e.Task = s.State;
        foreach (var f in s.Effects)
        {
            run(f);
        }

        AfterStep(e, before, ev);
    }

    private void Execute(Entry e, TransferEffect f)
    {
        switch (f)
        {
            case TransferEffect.Send s:
            {
                var session = e.Session is { IsOpen: true } bound ? bound : _sessions.GetSession(e.Peer);
                var ok = session?.Send(s.Msg) == true;

                // 取消通知发不出去时改为待送达（18.9）
                if (!ok && s.Msg is TransferCancel)
                {
                    AddPendingCancel(e);
                }

                break;
            }

            case TransferEffect.StartFile sf:
                StartFile(e, sf.Index, sf.Offset);
                break;
            case TransferEffect.StopSending:
                e.Sender?.Stop();
                break;
            case TransferEffect.Verify v:
            {
                var pos = IndexOfFile(e.Task, v.Index);
                if (pos < 0)
                {
                    return;
                }

                var file = e.Task.Files[pos];
                var name = FileNameSanitizer.Sanitize(file.OriginalName, _profile, file.StartedAt ?? _clock.LocalNow());
                if (e.Receiver is { } rxv)
                {
                    rxv.Submit(new WriteCmd.Verify(v.Index, v.Sha256, name, file.Size, rxv.StartOf.GetValueOrDefault(v.Index)));
                }

                break;
            }

            case TransferEffect.DeleteTemp d:
            {
                var rx = e.Receiver;
                var pos = IndexOfFile(e.Task, d.Index);
                TempRef? r = pos >= 0 ? e.Task.Files[pos].TempRef : null;
                if (rx is { IsOpen: true })
                {
                    rx.Dropped[d.Index] = true;
                    rx.Submit(new WriteCmd.Delete(d.Index, r));
                }
                else if (e.Task.State == TaskState.Running)
                {
                    // Resuming → Running（Restart）：由即将创建的写盘任务在写入之前删除
                    if (r is { } pr)
                    {
                        e.PendingDeletes.Add((d.Index, pr));
                    }
                }
                else if (r is { } dr)
                {
                    var previous = rx?.Job;
                    _ = Task.Run(async () =>
                    {
                        if (previous is not null)
                        {
                            await previous.ConfigureAwait(false);
                        }

                        _storage.Delete(dr);
                    });
                }

                break;
            }

            case TransferEffect.AddPendingCancel:
                AddPendingCancel(e);
                break;
            case TransferEffect.StartOfferTimer st:
            {
                var id = e.Id;
                e.OfferTimer = StartTimer(st.Ms, () => new Cmd.OfferTimeout(id));
                break;
            }

            case TransferEffect.CancelOfferTimer:
                CancelTimer(e.OfferTimer);
                e.OfferTimer = null;
                break;
            case TransferEffect.StartResume:
                OpenForResume(e, null);
                break;
            case TransferEffect.AbortResume:
                e.ResumeAttempt = null;
                CancelTimer(e.ResumeJob);
                CancelTimer(e.ResumeTimer);
                e.CheckingPrefix = false;
                break;
            case TransferEffect.ProtocolViolation:
                e.Session?.Connection.Close(new CloseReason.ProtocolError($"transfer {e.Id}"));
                break;
        }
    }

    private void AddPendingCancel(Entry e)
    {
        if (_store.AddPendingCancel(e.Peer, e.Id) is StoreResult.Failed)
        {
            _notices.OnNext(new TransferNotice.PendingCancelNotSaved(e.Id));
        }

        if (_sessions.GetSession(e.Peer) is { } s)
        {
            DeliverPendingCancels(s);
        }
    }

    private void StartFile(Entry e, int index, long offset)
    {
        if (e.Session is not { } session)
        {
            return;
        }

        var sender = e.Sender;
        if (sender is null || !ReferenceEquals(sender.Session, session))
        {
            sender?.Close();
            sender = new Sender(this, e.Id, session);
            e.Sender = sender;
        }

        var pos = IndexOfFile(e.Task, index);
        if (pos < 0)
        {
            return;
        }

        if (e.Task.Files[pos].SourceRef is not { } r)
        {
            Post(new Cmd.SenderEvent(sender, new TransferEvent.SourceReadFailed(index)));
            return;
        }

        sender.Start(index, r, offset);
    }

    /// <summary>每次归约之后：维护运行期状态、通知、写库与发布。</summary>
    private void AfterStep(Entry e, TransferTask before, TransferEvent ev)
    {
        var t = e.Task;
        var now = _clock.MonotonicMs();
        if (before.State != t.State)
        {
            if (t.State == TaskState.Running)
            {
                e.Progress.Reset(now, Transferred(t));
                if (t.Direction == Direction.Receive && e.Session is { } session && t.ReceiveDir is { } dir)
                {
                    var previous = e.Receiver;
                    previous?.Close();
                    var rx = new Receiver(this, e.Id, session, dir, e.PrefixDigests, previous?.Job);
                    e.Receiver = rx;
                    foreach (var (index, r) in e.PendingDeletes)
                    {
                        rx.Submit(new WriteCmd.Delete(index, r));
                    }

                    e.PendingDeletes.Clear();
                    e.PrefixDigests = new Dictionary<int, (long, IncrementalHash)>();
                    AdvanceWire(e, rx, 0);
                }
            }

            if (before.State == TaskState.AwaitingUser && e.RequestShown)
            {
                _notifier.Cancel(new NotifyKey.Transfer(t.TransferId));
                e.RequestShown = false;
            }

            if (t.State.IsTerminal() || t.State == TaskState.Interrupted)
            {
                StopRuntime(e);
            }

            if (t.State.IsTerminal() && t.Direction == Direction.Send)
            {
                // 发送任务结束后删除“以 .txt 文件发送”的缓存（M4）
                foreach (var f in t.Files)
                {
                    if (f.SourceRef is { } r && _textCache.Owns(r))
                    {
                        _textCache.Delete(r);
                    }
                }
            }
        }
        else if (!ReferenceEquals(t, before))
        {
            e.Progress.Sample(now, Transferred(t));
        }

        NotifyChanges(e, before);
        var structural = Structural(before, t);
        if (t != before)
        {
            Save(e, force: structural);
        }

        if (before.State != t.State || structural)
        {
            Publish();
        }
        else if (!ReferenceEquals(t, before))
        {
            SchedulePublish();
        }

        // 进入 Running 后若 ChunkWritten 让当前文件进入终态（如超过 size），线上当前文件随之前移
        if (e.Receiver is { } wrx && t.State == TaskState.Running && wrx.WirePos < t.Files.Count && t.Files[wrx.WirePos].State.IsTerminal())
        {
            wrx.Dropped[t.Files[wrx.WirePos].Index] = true;
            AdvanceWire(e, wrx, wrx.WirePos + 1);
        }

        if (ev is TransferEvent.Disconnected)
        {
            e.CheckingPrefix = false;
        }
    }

    /// <summary>状态或任何文件的状态、临时文件引用发生变化时立即写库；只有进度变化时最多每 1 s 写一次（M2）。</summary>
    private static bool Structural(TransferTask a, TransferTask b)
    {
        if (a.State != b.State || a.Interrupt != b.Interrupt || a.Files.Count != b.Files.Count)
        {
            return true;
        }

        for (var i = 0; i < a.Files.Count; i++)
        {
            if (a.Files[i].State != b.Files[i].State || a.Files[i].TempRef != b.Files[i].TempRef)
            {
                return true;
            }
        }

        return false;
    }

    private void StopRuntime(Entry e)
    {
        CancelTimer(e.OfferTimer);
        e.OfferTimer = null;
        CancelTimer(e.ResumeTimer);
        if (e.Task.State.IsTerminal())
        {
            CancelTimer(e.ResumeJob);
        }

        e.Sender?.Close();
        e.Sender = null;
        e.Receiver?.Close();
        e.CheckingPrefix = false;
        if (e.ProgressShown)
        {
            _notifier.Cancel(new NotifyKey.Transfer(e.Id));
            e.ProgressShown = false;
        }
    }

    /// <summary>释放一个将被移出内存的任务。</summary>
    private void Dispose(Entry e)
    {
        StopRuntime(e);
        CancelTimer(e.ResumeJob);
    }

    private static long Transferred(TransferTask t) => t.Files.Sum(f => f.State.IsTerminal() ? f.Size : f.Persisted);

    private void Save(Entry e, bool force)
    {
        var now = _clock.MonotonicMs();
        if (!force && now - e.LastSavedAt < _timings.SaveIntervalMs)
        {
            return;
        }

        if (e.LastSaved == e.Task)
        {
            return;
        }

        var t = e.Task with { UpdatedAtUtcMs = _clock.WallMs() };
        e.Task = t;
        e.LastSaved = t;
        e.LastSavedAt = now;
        if (_store.Save(t) is StoreResult.Failed f)
        {
            _notices.OnNext(new TransferNotice.StoreFailed(f.Detail));
        }
    }

    // ---- 通知（21.1、21.7、21.10） ----

    private void NotifyChanges(Entry e, TransferTask before)
    {
        var t = e.Task;
        if (!_lifecycle.Background.Value)
        {
            return;
        }

        var name = t.PeerName;
        for (var i = 0; i < t.Files.Count; i++)
        {
            var f = t.Files[i];
            if (i < before.Files.Count && before.Files[i].State == f.State)
            {
                continue;
            }

            switch (f.State)
            {
                case FileState.Completed when t.Direction == Direction.Receive && f.StoredRef is { } stored:
                    _notifier.FileCompleted(new NotifyKey.File(t.TransferId, f.Index), name, f.FinalName ?? f.OriginalName, stored);
                    break;
                case FileState.ReadFailed or FileState.ChecksumFailed or FileState.WriteFailed:
                    _notifier.Problem(new NotifyKey.File(t.TransferId, f.Index), name, f.OriginalName, StateName(f.State));
                    break;
            }
        }

        if (before.State != t.State && t.State == TaskState.Interrupted)
        {
            var file = t.Files.FirstOrDefault(f => !f.State.IsTerminal())?.OriginalName;
            _notifier.Problem(new NotifyKey.Transfer(t.TransferId), name, file, $"INTERRUPTED:{ReasonName(t.Interrupt?.Reason)}");
        }

        if (t.State == TaskState.Running)
        {
            var now = _clock.MonotonicMs();
            if (now - e.ProgressNotifiedAt >= _timings.ProgressNotifyMs)
            {
                e.ProgressNotifiedAt = now;
                e.ProgressShown = true;
                var cur = t.Files.FirstOrDefault(f => !f.State.IsTerminal());
                var p = e.Progress.Snapshot(now, t.Files.Sum(f => f.Size), false);
                _notifier.Progress(t.TransferId, name, cur?.OriginalName ?? string.Empty, p.Percent);
            }
        }
    }

    /// <summary>与 Kotlin 侧枚举名一致的状态键（UI / 平台层映射为文案）。</summary>
    private static string StateName(FileState s) => s switch
    {
        FileState.ReadFailed => "READ_FAILED",
        FileState.ChecksumFailed => "CHECKSUM_FAILED",
        FileState.WriteFailed => "WRITE_FAILED",
        _ => s.ToString().ToUpperInvariant(),
    };

    private static string ReasonName(InterruptReason? r) => r switch
    {
        null => "null",
        InterruptReason.PeerUnresponsive => "PEER_UNRESPONSIVE",
        InterruptReason.PeerClosed => "PEER_CLOSED",
        InterruptReason.LocalNetworkLost => "LOCAL_NETWORK_LOST",
        InterruptReason.IntegrityFailed => "INTEGRITY_FAILED",
        InterruptReason.ProtocolError => "PROTOCOL_ERROR",
        InterruptReason.WriteFailed => "WRITE_FAILED",
        InterruptReason.AppExit => "APP_EXIT",
        InterruptReason.AppKilled => "APP_KILLED",
        _ => r.Value.ToString(),
    };

    // ---- 发布 ----

    private void SchedulePublish()
    {
        if (_publishScheduled)
        {
            return;
        }

        _publishScheduled = true;
        StartTimer(_timings.PublishIntervalMs, () => new Cmd.Publish());
    }

    private void Publish()
    {
        var now = _clock.MonotonicMs();
        _tasks.Value = new ValueList<TransferTaskView>(_entries.Values.Select(e => View(e, now)));
        _unfinished.Value = _entries.Values.Count(e => !e.Task.State.IsTerminal());
    }

    private static TransferTaskView View(Entry e, long now)
    {
        var t = e.Task;
        var total = t.Files.Sum(f => f.Size);
        var done = Transferred(t);
        var running = t.State == TaskState.Running;
        var snap = e.Progress.Snapshot(now, total, t.State == TaskState.Completed);
        var percent = total == 0 ? (t.State == TaskState.Completed ? 100 : 0) : (int)(Math.Min(done, total) * 100 / total);
        var p = new Progress(percent, done, total, running ? snap.BytesPerSec : 0, running ? snap.EtaSec : null);
        int? current = running ? t.Files.FirstOrDefault(f => !f.State.IsTerminal())?.Index : null;
        var files = t.Files.Select(f =>
        {
            var fileDone = f.State == FileState.Completed ? f.Size : f.Persisted;
            var filePercent = f.Size == 0 ? (f.State == FileState.Completed ? 100 : 0) : (int)(Math.Min(fileDone, f.Size) * 100 / f.Size);
            return f.Index == current && p.BytesPerSec > 0
                ? new Progress(filePercent, fileDone, f.Size, p.BytesPerSec, Math.Max(0, f.Size - fileDone) / p.BytesPerSec)
                : new Progress(filePercent, fileDone, f.Size, 0, null);
        });
        return new TransferTaskView(t, p, new ValueList<Progress>(files), e.CheckingPrefix);
    }

    /// <summary>在单调时钟到达“现在 + ms”时投递 make()；到期时刻在调用时确定，与任务何时开始运行无关。</summary>
    private CancellationTokenSource StartTimer(long ms, Func<Cmd> make)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var ct = cts.Token;
        var due = _clock.MonotonicMs() + Math.Max(0, ms);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    long left;
                    while ((left = due - _clock.MonotonicMs()) > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(left), _clock.Time, ct).ConfigureAwait(false);
                    }

                    Post(make());
                }
                catch (OperationCanceledException)
                {
                }
            },
            CancellationToken.None);
        return cts;
    }

    private static void CancelTimer(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string PeerName(DeviceId peer, Session? session) =>
        _trust.Get(peer)?.DisplayName ?? session?.Peer.DisplayName ?? string.Empty;

    /// <summary>读出临时文件 [0, upTo)（null 表示全部）的哈希；读不出或长度不足时返回 null（E4：按 None 处理）。</summary>
    private (long Length, IncrementalHash Hash)? HashTemp(TempRef r, long? upTo)
    {
        if (_storage.OpenRead(r) is not ReadResult<IChunkReader>.Ok ok)
        {
            return null;
        }

        var reader = ok.Value;
        try
        {
            var md = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[_timings.ChunkBytes];
            long total = 0;
            while (upTo is null || total < upTo)
            {
                var want = upTo is { } u ? (int)Math.Min(buf.Length, u - total) : buf.Length;
                if (reader.Read(buf.AsSpan(0, want)) is not ReadResult<int>.Ok n)
                {
                    md.Dispose();
                    return null;
                }

                if (n.Value < 0)
                {
                    break;
                }

                md.AppendData(buf, 0, n.Value);
                total += n.Value;
            }

            if (upTo is { } need && total < need)
            {
                md.Dispose();
                return null;
            }

            return (total, md);
        }
        finally
        {
            reader.Close();
        }
    }

    // ---- 发送方读盘任务 ----

    /// <summary>一个发送任务在一个 Session 上的数据源：容量为 2 的缓冲 + 读盘任务（C4.5 FileSender）。</summary>
    private sealed class Sender : IChunkSource
    {
        private readonly FileTransferEngine _owner;
        private readonly Queue<ProtocolMessage> _buffer = new();
        private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        private readonly ChunkSourceHandle _handle;
        private CancellationTokenSource? _job;

        /// <summary>每次 Start / Stop 加 1；被取消的读盘任务不能再往缓冲里放数据（在 _buffer 锁内检查）。</summary>
        private int _gen;
        private int _index = -1;
        private long _acked;

        public Sender(FileTransferEngine owner, TransferId id, Session session)
        {
            _owner = owner;
            Id = id;
            Session = session;
            _handle = session.AttachChunkSource(this);
        }

        public TransferId Id { get; }

        public Session Session { get; }

        public ProtocolMessage? Poll()
        {
            ProtocolMessage? m;
            lock (_buffer)
            {
                _buffer.TryDequeue(out m);
            }

            if (m is not null)
            {
                _wake.Writer.TryWrite(true);
            }

            return m;
        }

        public void OnAck(int index, long persisted)
        {
            if (index == Volatile.Read(ref _index) && persisted > Interlocked.Read(ref _acked))
            {
                Interlocked.Exchange(ref _acked, persisted);
                _wake.Writer.TryWrite(true);
            }
        }

        public void Start(int index, SourceRef r, long offset)
        {
            Stop();
            Volatile.Write(ref _index, index);
            Interlocked.Exchange(ref _acked, offset);
            int g;
            lock (_buffer)
            {
                g = _gen;
            }

            var job = CancellationTokenSource.CreateLinkedTokenSource(_owner._cts.Token);
            _job = job;
            var ct = job.Token;
            _ = Task.Run(() => RunAsync(g, index, r, offset, ct), CancellationToken.None);
        }

        public void Stop()
        {
            CancelTimer(_job);
            _job = null;
            lock (_buffer)
            {
                _gen++;
                _buffer.Clear();
            }
        }

        public void Close()
        {
            Stop();
            _handle.Detach();
        }

        /// <summary>放入缓冲；本任务已被 Stop 取代时返回 false。</summary>
        private async Task<bool> PushAsync(int g, ProtocolMessage m, CancellationToken ct)
        {
            while (true)
            {
                int r;
                lock (_buffer)
                {
                    if (_gen != g)
                    {
                        r = 0;
                    }
                    else if (_buffer.Count < BufferCapacity)
                    {
                        _buffer.Enqueue(m);
                        r = 1;
                    }
                    else
                    {
                        r = 2;
                    }
                }

                if (r == 0)
                {
                    return false;
                }

                if (r == 1)
                {
                    _handle.Signal();
                    return true;
                }

                await _wake.Reader.ReadAsync(ct).ConfigureAwait(false);
            }
        }

        private void Post(TransferEvent ev) => _owner.Post(new Cmd.SenderEvent(this, ev));

        private async Task RunAsync(int g, int index, SourceRef r, long offset, CancellationToken ct)
        {
            if (_owner._sources.Open(r, 0) is not ReadResult<IChunkReader>.Ok opened)
            {
                Post(new TransferEvent.SourceReadFailed(index));
                return;
            }

            var reader = opened.Value;
            try
            {
                using var md = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[_owner._timings.ChunkBytes];
                long pos = 0;
                if (offset > 0)
                {
                    // 续传：先从磁盘重新计算 [0, offset) 的哈希
                    _owner.Post(new Cmd.SenderChecking(this, true));
                    while (pos < offset)
                    {
                        var want = (int)Math.Min(buf.Length, offset - pos);
                        if (reader.Read(buf.AsSpan(0, want)) is not ReadResult<int>.Ok n || n.Value < 0)
                        {
                            Post(new TransferEvent.SourceReadFailed(index));
                            return;
                        }

                        md.AppendData(buf, 0, n.Value);
                        pos += n.Value;
                    }

                    _owner.Post(new Cmd.SenderChecking(this, false));
                }

                while (true)
                {
                    // 未确认数据 ≤ 4 MiB（C1.13）
                    while (pos + buf.Length - Interlocked.Read(ref _acked) > _owner._timings.WindowBytes)
                    {
                        await _wake.Reader.ReadAsync(ct).ConfigureAwait(false);
                    }

                    if (reader.Read(buf) is not ReadResult<int>.Ok n)
                    {
                        Post(new TransferEvent.SourceReadFailed(index));
                        return;
                    }

                    if (n.Value < 0)
                    {
                        break;
                    }

                    md.AppendData(buf, 0, n.Value);
                    if (!await PushAsync(g, new FileChunk(Id, (uint)index, (ulong)pos, Bytes.Of(buf.AsSpan(0, n.Value))), ct).ConfigureAwait(false))
                    {
                        return;
                    }

                    pos += n.Value;
                }

                var sha = Bytes.Of(md.GetHashAndReset());
                if (!await PushAsync(g, new FileEnd(Id, (uint)index, sha), ct).ConfigureAwait(false))
                {
                    return;
                }

                Post(new TransferEvent.FileEndSent(index, sha));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                reader.Close();
            }
        }
    }

    // ---- 接收方写盘任务 ----

    private abstract record WriteCmd
    {
        public sealed record Chunk(int Index, long Offset, Bytes Data) : WriteCmd;

        /// <summary>Start 为该文件本次的起点（没有收到数据块时，从这里打开临时文件）。</summary>
        public sealed record End(int Index, Bytes Sha256, long Start) : WriteCmd;

        public sealed record PeerReadFailed(int Index) : WriteCmd;

        public sealed record Verify(int Index, Bytes Sha256, string Name, long Size, long Start) : WriteCmd;

        public sealed record Delete(int Index, TempRef? Ref) : WriteCmd;
    }

    /// <summary>一个接收任务在一个 Session 上的写盘任务（C4.5 FileReceiver）。</summary>
    private sealed class Receiver
    {
        private readonly FileTransferEngine _owner;
        private readonly DirRef _dir;
        private readonly IReadOnlyDictionary<int, (long Length, IncrementalHash Hash)> _prefix;
        private readonly Channel<WriteCmd> _queue = Channel.CreateUnbounded<WriteCmd>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>已入队、尚未写盘的数据字节数（窗口检查用）。</summary>
        public long Queued;

        // ---- 写盘任务内部状态 ----
        private int _cur = -1;
        private ITempWriter? _writer;
        private TempRef? _temp;
        private IncrementalHash? _md;
        private long _persisted;
        private long _ackedBytes;
        private long _ackedAt;
        private bool _failed;
        private volatile bool _isOpen = true;

        public Receiver(FileTransferEngine owner, TransferId id, Session session, DirRef dir, IReadOnlyDictionary<int, (long Length, IncrementalHash Hash)> prefix, Task? previous)
        {
            _owner = owner;
            Id = id;
            Session = session;
            _dir = dir;
            _prefix = prefix;

            // 放在全部字段初始化之后启动：任务可能立即在另一个线程上运行
            Job = Task.Run(() => LoopAsync(previous), CancellationToken.None);
        }

        public TransferId Id { get; }

        public Session Session { get; }

        public Task Job { get; }

        /// <summary>已判定结束的文件：队列中它的数据直接丢弃。</summary>
        public ConcurrentDictionary<int, bool> Dropped { get; } = new();

        // ---- 以下三项只在 actor 中访问 ----

        /// <summary>线上当前文件在 Files 中的位置；之前的文件不再接受数据。</summary>
        public int WirePos { get; set; }

        /// <summary>当前文件期望的下一个 offset。</summary>
        public long Expected { get; set; }

        /// <summary>续传计划中 Continue 的起点。</summary>
        public Dictionary<int, long> StartOf { get; } = new();

        public bool IsOpen => _isOpen;

        public void Submit(WriteCmd c)
        {
            if (!_queue.Writer.TryWrite(c) && c is WriteCmd.Chunk ch)
            {
                Interlocked.Add(ref Queued, -ch.Data.Length);
            }
        }

        /// <summary>不再接收新数据；已入队的命令处理完后结束。</summary>
        public void Close()
        {
            _isOpen = false;
            _queue.Writer.TryComplete();
        }

        private void Post(TransferEvent ev) => _owner.Post(new Cmd.ReceiverEvent(this, ev));

        private async Task LoopAsync(Task? previous)
        {
            var ct = _owner._cts.Token;
            try
            {
                if (previous is not null)
                {
                    await previous.ConfigureAwait(false);
                }

                while (true)
                {
                    if (_writer is not null && _persisted > _ackedBytes)
                    {
                        var wait = Math.Max(1, _ackedAt + _owner._timings.AckIntervalMs - _owner._clock.MonotonicMs());
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var read = _queue.Reader.WaitToReadAsync(cts.Token).AsTask();
                        var delay = Task.Delay(TimeSpan.FromMilliseconds(wait), _owner._clock.Time, cts.Token);
                        var first = await Task.WhenAny(read, delay).ConfigureAwait(false);
                        await cts.CancelAsync().ConfigureAwait(false);
                        if (first == delay)
                        {
                            SendAck(); // 200 ms 内没有新数据：确认已写入的部分
                            continue;
                        }

                        if (!await read.ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    else if (!await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        break;
                    }

                    while (_queue.Reader.TryRead(out var cmd))
                    {
                        Process(cmd);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                CloseWriter();
            }
        }

        private void Process(WriteCmd c)
        {
            switch (c)
            {
                case WriteCmd.Chunk ch:
                {
                    Interlocked.Add(ref Queued, -ch.Data.Length);
                    if (_failed || Dropped.ContainsKey(ch.Index) || !EnsureOpen(ch.Index, ch.Offset) || _writer is not { } w)
                    {
                        return;
                    }

                    if (w.Write(ch.Data.Span) is IoResult.Failed f)
                    {
                        _failed = true;
                        Post(new TransferEvent.WriteFailed(ch.Index, f.Detail));
                        return;
                    }

                    _md?.AppendData(ch.Data.Span);
                    _persisted = ch.Offset + ch.Data.Length;
                    Post(new TransferEvent.ChunkWritten(ch.Index, _persisted, _owner._clock.LocalNow(), _owner._clock.WallMs()));
                    var now = _owner._clock.MonotonicMs();
                    if (_persisted - _ackedBytes >= _owner._timings.AckBytes || now - _ackedAt >= _owner._timings.AckIntervalMs)
                    {
                        SendAck();
                    }

                    break;
                }

                case WriteCmd.End end:
                    if (_failed || Dropped.ContainsKey(end.Index) || !EnsureOpen(end.Index, end.Start))
                    {
                        return;
                    }

                    SendAck(force: true); // 文件结束时也确认一次
                    Post(new TransferEvent.EndReceived(end.Index, end.Sha256, _owner._clock.LocalNow()));
                    break;
                case WriteCmd.PeerReadFailed prf:
                    if (_cur == prf.Index)
                    {
                        CloseWriter();
                    }

                    Post(new TransferEvent.PeerReadFailed(prf.Index));
                    break;
                case WriteCmd.Verify v:
                    Verify(v);
                    break;
                case WriteCmd.Delete d:
                {
                    var r = d.Ref ?? (_cur == d.Index ? _temp : null);
                    if (_cur == d.Index)
                    {
                        CloseWriter();
                    }

                    if (r is { } dr)
                    {
                        _owner._storage.Delete(dr);
                    }

                    break;
                }
            }
        }

        /// <summary>打开第 index 个文件的临时文件，从 offset 处追加；续传时接着之前的哈希状态。</summary>
        private bool EnsureOpen(int index, long offset)
        {
            if (_cur == index && _writer is not null)
            {
                return true;
            }

            CloseWriter();
            var r = _owner._storage.CreateTemp(_dir, Id, index);
            _owner.Post(new Cmd.TempReady(this, index, r));
            IncrementalHash? digest;
            if (offset == 0)
            {
                digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            }
            else if (_prefix.TryGetValue(index, out var p) && p.Length == offset)
            {
                digest = p.Hash.Clone();
            }
            else
            {
                digest = _owner.HashTemp(r, offset)?.Hash;
            }

            if (digest is null)
            {
                _failed = true;
                Post(new TransferEvent.WriteFailed(index, "无法读取临时文件"));
                return false;
            }

            switch (_owner._storage.OpenAppend(r, offset))
            {
                case WriteResult<ITempWriter>.Ok ok:
                    _writer = ok.Value;
                    break;
                case WriteResult<ITempWriter>.Failed f:
                    digest.Dispose();
                    _failed = true;
                    Post(new TransferEvent.WriteFailed(index, f.Detail));
                    return false;
            }

            _cur = index;
            _temp = r;
            _md = digest;
            _persisted = offset;
            _ackedBytes = offset;
            _ackedAt = _owner._clock.MonotonicMs();
            return true;
        }

        private void SendAck(bool force = false)
        {
            if (_cur < 0 || (!force && _persisted <= _ackedBytes))
            {
                return;
            }

            Session.Send(new FileAck(Id, (uint)_cur, (ulong)_persisted));
            _ackedBytes = _persisted;
            _ackedAt = _owner._clock.MonotonicMs();
        }

        private void Verify(WriteCmd.Verify v)
        {
            if (_failed || Dropped.ContainsKey(v.Index))
            {
                return;
            }

            if ((_cur != v.Index || _writer is null) && !EnsureOpen(v.Index, v.Start))
            {
                return;
            }

            if (_writer!.FlushSync() is IoResult.Failed f)
            {
                _failed = true;
                Post(new TransferEvent.WriteFailed(v.Index, f.Detail));
                return;
            }

            var ok = _persisted == v.Size && Bytes.Of(_md!.GetHashAndReset()) == v.Sha256;
            var r = _temp!.Value;
            CloseWriter();
            VerifyOutcome outcome = !ok
                ? new VerifyOutcome.ChecksumMismatch()
                : _owner._storage.Commit(r, v.Name) switch
                {
                    CommitResult.Ok c => new VerifyOutcome.Committed(c.FinalName, c.FullPath, c.StoredRef),
                    CommitResult.Failed c => new VerifyOutcome.CommitFailed(c.Detail),
                    _ => new VerifyOutcome.CommitFailed("unknown"),
                };
            Post(new TransferEvent.VerifyDone(v.Index, outcome));
        }

        private void CloseWriter()
        {
            _writer?.Close();
            _writer = null;
            _cur = -1;
            _md?.Dispose();
            _md = null;
        }
    }
}
