using LanTransfer.Protocol;
using Ev = LanTransfer.Domain.TransferEvent;
using Fx = LanTransfer.Domain.TransferEffect;

namespace LanTransfer.Domain;

/// <summary>接收方对一个文件做完“flushSync → 校验 → 提交”后的结果（C4.5 FileReceiver）。</summary>
public abstract record VerifyOutcome
{
    public sealed record Committed(string FinalName, string FullPath, StoredFileRef StoredRef) : VerifyOutcome;

    public sealed record ChecksumMismatch : VerifyOutcome;

    public sealed record CommitFailed(string Detail) : VerifyOutcome;
}

/// <summary>传输任务事件，与 Kotlin 侧 <c>TransferEvent</c> 对应。LocalNow 为本地时间（Kind = Unspecified）。</summary>
public abstract record TransferEvent
{
    // ---- 发送方 ----

    /// <summary>收到 FILE_OFFER_REPLY（Offering）。</summary>
    public sealed record OfferReplied(OfferDecision Decision) : TransferEvent;

    /// <summary>源文件打开或读取失败（14.7）。</summary>
    public sealed record SourceReadFailed(int Index) : TransferEvent;

    /// <summary>已发出 FILE_END（附带整个文件的 SHA‑256）。</summary>
    public sealed record FileEndSent(int Index, Bytes Sha256) : TransferEvent;

    public sealed record AckReceived(int Index, long Persisted) : TransferEvent;

    public sealed record ResultReceived(int Index, FileResultStatus Status, string? Detail) : TransferEvent;

    // ---- 接收方 ----

    /// <summary>用户点击“接收”（15.6）。DirUsable 为 false 表示接收目录无法创建或不可写（16.15）。</summary>
    public sealed record UserAccepted(DirRef Dir, bool DirUsable) : TransferEvent;

    public sealed record UserRejected : TransferEvent;

    /// <summary>已写入 FILE_CHUNK；Persisted 为该文件临时文件的新长度。</summary>
    public sealed record ChunkWritten(int Index, long Persisted, DateTime LocalNow, long NowWall) : TransferEvent;

    public sealed record EndReceived(int Index, Bytes Sha256, DateTime LocalNow) : TransferEvent;

    public sealed record VerifyDone(int Index, VerifyOutcome Outcome) : TransferEvent;

    public sealed record WriteFailed(int Index, string Detail) : TransferEvent;

    /// <summary>收到 FILE_READ_FAILED。</summary>
    public sealed record PeerReadFailed(int Index) : TransferEvent;

    // ---- 双方 ----

    /// <summary>Offer 计时到期：接收方 60 s、发送方保底 65 s（15.4）。</summary>
    public sealed record OfferTimedOut : TransferEvent;

    public sealed record Disconnected(InterruptReason Reason, string? Detail = null) : TransferEvent;

    /// <summary>本机取消或放弃（18.2、18.9、20.5、20.10、Q7）。SessionActive 表示与对方当前有 Active Session。接收方在 AwaitingUser 时按拒绝处理（C4.5）。</summary>
    public sealed record LocalCancel(bool SessionActive, TransferCancelReason Reason = TransferCancelReason.User) : TransferEvent;

    /// <summary>收到 TRANSFER_CANCEL（含续传时对方回复 UNKNOWN_TRANSFER）。</summary>
    public sealed record PeerCancelled(TransferCancelReason Reason) : TransferEvent;

    /// <summary>用户点击“继续”，或收到对方的 RESUME_REQUEST / RESUME_STATE（17.4）。</summary>
    public sealed record ResumeRequested : TransferEvent;

    /// <summary>10 s 内未建立 Session（17.10）。</summary>
    public sealed record ResumeFailed(InterruptReason Reason) : TransferEvent;

    /// <summary>RESUME_PLAN 已确定：发送方由 ResumePlanner 生成，接收方从对方收到。</summary>
    public sealed record ResumePlanned(ValueList<ResumePlanEntry> Plan) : TransferEvent;

    /// <summary>启动恢复（17.9）。</summary>
    public sealed record Restored : TransferEvent;
}

/// <summary>传输任务 effects，与 Kotlin 侧 <c>TransferEffect</c> 对应。</summary>
public abstract record TransferEffect
{
    /// <summary>在与对方的 Session 上发送（取消类消息用 P0）。</summary>
    public sealed record Send(ProtocolMessage Msg) : TransferEffect;

    /// <summary>发送方：从 Offset 起读取并发送第 Index 个文件（Offset = size 时直接发 FILE_END）。</summary>
    public sealed record StartFile(int Index, long Offset) : TransferEffect;

    /// <summary>发送方：停止读取与发送。</summary>
    public sealed record StopSending : TransferEffect;

    /// <summary>接收方：flushSync → 校验长度与哈希 → 文件名处理 → 提交。</summary>
    public sealed record Verify(int Index, Bytes Sha256) : TransferEffect;

    /// <summary>接收方：删除第 Index 个文件的临时文件（18.3 要求 5 s 内完成）。</summary>
    public sealed record DeleteTemp(int Index) : TransferEffect;

    /// <summary>写入 pending_cancel，下次与对方建立 Session 后 2 s 内发出（18.9）。</summary>
    public sealed record AddPendingCancel(TransferCancelReason Reason) : TransferEffect;

    public sealed record StartOfferTimer(long Ms) : TransferEffect;

    public sealed record CancelOfferTimer : TransferEffect;

    /// <summary>重新建立 Session 并交换 RESUME_STATE / RESUME_PLAN（10 s 预算）。</summary>
    public sealed record StartResume : TransferEffect;

    public sealed record AbortResume : TransferEffect;

    /// <summary>对方的 FILE_* 消息违反顺序规则：关闭连接（22.6）。</summary>
    public sealed record ProtocolViolation : TransferEffect;
}

/// <summary>
/// 传输任务状态机（M3.2、M3.3，需求 14–18），与 Kotlin 侧 <c>TransferReducer</c> 对应。按 Direction 区分发送方与接收方。
/// 同一任务内按 index 顺序逐个文件传输：“当前文件”是第一个未进入终态的文件。终态任务不再迁移。
/// </summary>
public static class TransferReducer
{
    public const long ReceiverOfferTimeoutMs = 60_000;
    public const long SenderOfferTimeoutMs = 65_000;

    public static Step<TransferTask, TransferEffect> Reduce(TransferTask t, TransferEvent e)
    {
        if (t.State.IsTerminal())
        {
            return Of(t);
        }

        return e switch
        {
            Ev.OfferReplied x => OnOfferReplied(t, x),
            Ev.SourceReadFailed x => SenderOnly(t, () => OnSourceReadFailed(t, x)),
            Ev.FileEndSent x => SenderOnly(t, () => OnFileEndSent(t, x)),
            Ev.AckReceived x => SenderOnly(t, () => OnAck(t, x)),
            Ev.ResultReceived x => SenderOnly(t, () => OnResult(t, x)),
            Ev.UserAccepted x => OnUserAccepted(t, x),
            Ev.UserRejected => OnUserRejected(t),
            Ev.ChunkWritten x => ReceiverOnly(t, () => OnChunk(t, x)),
            Ev.EndReceived x => ReceiverOnly(t, () => OnEnd(t, x)),
            Ev.VerifyDone x => ReceiverOnly(t, () => OnVerifyDone(t, x)),
            Ev.WriteFailed x => ReceiverOnly(t, () => OnWriteFailed(t, x)),
            Ev.PeerReadFailed x => ReceiverOnly(t, () => OnPeerReadFailed(t, x)),
            Ev.OfferTimedOut => OnOfferTimedOut(t),
            Ev.Disconnected x => OnDisconnected(t, x),
            Ev.LocalCancel x => OnLocalCancel(t, x),
            Ev.PeerCancelled => OnPeerCancelled(t),
            Ev.ResumeRequested => t.State == TaskState.Interrupted ? Of(t with { State = TaskState.Resuming, Interrupt = null }, new Fx.StartResume()) : Of(t),
            Ev.ResumeFailed x => t.State == TaskState.Resuming ? Of(t with { State = TaskState.Interrupted, Interrupt = new InterruptInfo(x.Reason) }) : Of(t),
            Ev.ResumePlanned x => OnResumePlanned(t, x),
            Ev.Restored => OnRestored(t),
            _ => throw new ArgumentOutOfRangeException(nameof(e)),
        };
    }

    private static Step<TransferTask, TransferEffect> Of(TransferTask t, params TransferEffect[] effects) => new(t, new ValueList<TransferEffect>(effects));

    private static Step<TransferTask, TransferEffect> SenderOnly(TransferTask t, Func<Step<TransferTask, TransferEffect>> f) =>
        t.Direction == Direction.Send && t.State == TaskState.Running ? f() : Of(t);

    private static Step<TransferTask, TransferEffect> ReceiverOnly(TransferTask t, Func<Step<TransferTask, TransferEffect>> f) =>
        t.Direction == Direction.Receive && t.State == TaskState.Running ? f() : Of(t);

    /// <summary>当前文件在 Files 中的位置；全部为终态时为 -1。</summary>
    private static int CurrentPos(TransferTask t) => IndexOf(t, f => !f.State.IsTerminal());

    private static int PosOf(TransferTask t, int index) => IndexOf(t, f => f.Index == index);

    private static int IndexOf(TransferTask t, Func<TransferFile, bool> match)
    {
        for (var i = 0; i < t.Files.Count; i++)
        {
            if (match(t.Files[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static TransferTask WithFile(TransferTask t, int pos, TransferFile f) =>
        t with { Files = new ValueList<TransferFile>(t.Files.Select((old, i) => i == pos ? f : old)) };

    private static TransferTask Interrupted(TransferTask t, InterruptReason reason, string? detail = null) =>
        t with { State = TaskState.Interrupted, Interrupt = new InterruptInfo(reason, detail) };

    /// <summary>发送方：当前文件进入终态后开始下一个文件；没有剩余文件时任务完成。</summary>
    private static Step<TransferTask, TransferEffect> StartNextSender(TransferTask t, params TransferEffect[] effects)
    {
        var pos = CurrentPos(t);
        if (pos < 0)
        {
            return Of(t with { State = TaskState.Completed, Interrupt = null }, effects);
        }

        var f = t.Files[pos];
        return Of(WithFile(t, pos, f with { State = FileState.Transferring }), [.. effects, new Fx.StartFile(f.Index, f.Persisted)]);
    }

    /// <summary>接收方：当前文件进入终态后，没有剩余文件时任务完成；否则等待下一个文件的数据。</summary>
    private static Step<TransferTask, TransferEffect> AfterReceiverFile(TransferTask t, params TransferEffect[] effects) =>
        CurrentPos(t) < 0 ? Of(t with { State = TaskState.Completed, Interrupt = null }, effects) : Of(t, effects);

    // ---- Offer 阶段 ----

    private static Step<TransferTask, TransferEffect> OnOfferReplied(TransferTask t, Ev.OfferReplied e)
    {
        if (t.Direction != Direction.Send || t.State != TaskState.Offering)
        {
            return Of(t);
        }

        if (e.Decision == OfferDecision.Accept)
        {
            return StartNextSender(t with { State = TaskState.Running }, new Fx.CancelOfferTimer());
        }

        var end = e.Decision switch
        {
            OfferDecision.Reject => TaskState.Rejected,
            OfferDecision.Timeout => TaskState.NoResponse,
            OfferDecision.NoSpace => TaskState.NoSpace,
            OfferDecision.CannotSave => TaskState.CannotSave,
            _ => TaskState.OfferInvalid,
        };
        return Of(t with { State = end }, new Fx.CancelOfferTimer());
    }

    private static Fx.Send Reply(TransferTask t, OfferDecision d) => new(new FileOfferReply(t.TransferId, d));

    private static Step<TransferTask, TransferEffect> OnUserAccepted(TransferTask t, Ev.UserAccepted e)
    {
        if (t.Direction != Direction.Receive || t.State != TaskState.AwaitingUser)
        {
            return Of(t);
        }

        if (!e.DirUsable)
        {
            return Of(t with { State = TaskState.CannotSave }, new Fx.CancelOfferTimer(), Reply(t, OfferDecision.CannotSave));
        }

        return AfterReceiverFile(t with { State = TaskState.Running, ReceiveDir = e.Dir }, new Fx.CancelOfferTimer(), Reply(t, OfferDecision.Accept));
    }

    private static Step<TransferTask, TransferEffect> OnUserRejected(TransferTask t) =>
        t.Direction == Direction.Receive && t.State == TaskState.AwaitingUser
            ? Of(t with { State = TaskState.Rejected }, new Fx.CancelOfferTimer(), Reply(t, OfferDecision.Reject))
            : Of(t);

    private static Step<TransferTask, TransferEffect> OnOfferTimedOut(TransferTask t) =>
        t.Direction == Direction.Send && t.State == TaskState.Offering ? Of(t with { State = TaskState.NoResponse })
        : t.Direction == Direction.Receive && t.State == TaskState.AwaitingUser ? Of(t with { State = TaskState.NoResponse }, Reply(t, OfferDecision.Timeout))
        : Of(t);

    // ---- 发送方 ----

    private static Step<TransferTask, TransferEffect> OnSourceReadFailed(TransferTask t, Ev.SourceReadFailed e)
    {
        var pos = CurrentPos(t);
        if (pos < 0 || t.Files[pos].Index != e.Index)
        {
            return Of(t);
        }

        var next = WithFile(t, pos, t.Files[pos] with { State = FileState.ReadFailed });
        return StartNextSender(next, new Fx.Send(new FileReadFailed(t.TransferId, (uint)e.Index)));
    }

    private static Step<TransferTask, TransferEffect> OnFileEndSent(TransferTask t, Ev.FileEndSent e)
    {
        var pos = CurrentPos(t);
        if (pos < 0 || t.Files[pos].Index != e.Index || t.Files[pos].State != FileState.Transferring)
        {
            return Of(t);
        }

        return Of(WithFile(t, pos, t.Files[pos] with { State = FileState.Verifying, Sha256 = e.Sha256 }));
    }

    private static Step<TransferTask, TransferEffect> OnAck(TransferTask t, Ev.AckReceived e)
    {
        var pos = CurrentPos(t);
        if (pos < 0 || t.Files[pos].Index != e.Index)
        {
            return Of(t);
        }

        var f = t.Files[pos];
        var p = Math.Clamp(e.Persisted, f.Persisted, f.Size);
        return p == f.Persisted ? Of(t) : Of(WithFile(t, pos, f with { Persisted = p }));
    }

    private static Step<TransferTask, TransferEffect> OnResult(TransferTask t, Ev.ResultReceived e)
    {
        var pos = CurrentPos(t);
        if (pos < 0 || t.Files[pos].Index != e.Index)
        {
            return Of(t);
        }

        var f = t.Files[pos];
        return e.Status switch
        {
            FileResultStatus.Completed => StartNextSender(WithFile(t, pos, f with { State = FileState.Completed, Persisted = f.Size })),

            // 已接收字节数超过 size 时接收方可能在 FILE_END 之前就判定失败：停止当前文件，继续下一个
            FileResultStatus.ChecksumFailed => f.State == FileState.Transferring
                ? StartNextSender(WithFile(t, pos, f with { State = FileState.ChecksumFailed }), new Fx.StopSending())
                : StartNextSender(WithFile(t, pos, f with { State = FileState.ChecksumFailed })),
            _ => Of(Interrupted(WithFile(t, pos, f with { State = FileState.WriteFailed }), InterruptReason.WriteFailed, e.Detail), new Fx.StopSending()),
        };
    }

    // ---- 接收方 ----

    private static Step<TransferTask, TransferEffect> Violation(TransferTask t) => Of(Interrupted(t, InterruptReason.ProtocolError), new Fx.ProtocolViolation());

    /// <summary>
    /// 对方消息针对的文件：是当前文件 → 位置；是已经终态的文件（例如判定 ChecksumFailed 后仍在途的数据）→ null 表示忽略；
    /// 是更靠后的文件或不存在 → -1 表示违反顺序。
    /// </summary>
    private static int? TargetPos(TransferTask t, int index)
    {
        var pos = PosOf(t, index);
        if (pos < 0)
        {
            return -1;
        }

        if (t.Files[pos].State.IsTerminal())
        {
            return null;
        }

        return pos == CurrentPos(t) ? pos : -1;
    }

    private static Fx.Send Result(TransferTask t, int index, FileResultStatus status, string? detail = null) =>
        new(new FileResult(t.TransferId, (uint)index, status, detail));

    private static Step<TransferTask, TransferEffect> OnChunk(TransferTask t, Ev.ChunkWritten e)
    {
        if (TargetPos(t, e.Index) is not { } pos)
        {
            return Of(t);
        }

        if (pos < 0 || t.Files[pos].State == FileState.Verifying)
        {
            return Violation(t);
        }

        var f = t.Files[pos];
        if (e.Persisted > f.Size)
        {
            // 已接收字节数超过 size：立即判定校验失败（17.2）
            return AfterReceiverFile(WithFile(t, pos, f with { State = FileState.ChecksumFailed }), new Fx.DeleteTemp(f.Index), Result(t, f.Index, FileResultStatus.ChecksumFailed));
        }

        var updated = f with
        {
            State = FileState.Transferring,
            Persisted = Math.Max(f.Persisted, e.Persisted),
            StartedAt = f.StartedAt ?? e.LocalNow,
            LastWriteAtUtcMs = e.NowWall,
        };
        return Of(WithFile(t, pos, updated));
    }

    private static Step<TransferTask, TransferEffect> OnEnd(TransferTask t, Ev.EndReceived e)
    {
        if (TargetPos(t, e.Index) is not { } pos)
        {
            return Of(t);
        }

        if (pos < 0 || t.Files[pos].State == FileState.Verifying)
        {
            return Violation(t);
        }

        // 0 字节文件（以及续传时 start = size 的文件）从 Waiting 直接进入 Verifying
        var f = t.Files[pos];
        var updated = f with { State = FileState.Verifying, Sha256 = e.Sha256, StartedAt = f.StartedAt ?? e.LocalNow };
        return Of(WithFile(t, pos, updated), new Fx.Verify(f.Index, e.Sha256));
    }

    private static Step<TransferTask, TransferEffect> OnVerifyDone(TransferTask t, Ev.VerifyDone e)
    {
        var pos = CurrentPos(t);
        if (pos < 0 || t.Files[pos].Index != e.Index || t.Files[pos].State != FileState.Verifying)
        {
            return Of(t);
        }

        var f = t.Files[pos];
        return e.Outcome switch
        {
            VerifyOutcome.Committed o => AfterReceiverFile(
                WithFile(t, pos, f with { State = FileState.Completed, Persisted = f.Size, FinalName = o.FinalName, FullPath = o.FullPath, StoredRef = o.StoredRef }),
                Result(t, f.Index, FileResultStatus.Completed)),
            VerifyOutcome.CommitFailed o => Of(
                Interrupted(WithFile(t, pos, f with { State = FileState.WriteFailed }), InterruptReason.WriteFailed, o.Detail),
                Result(t, f.Index, FileResultStatus.WriteFailed, o.Detail)),
            _ => AfterReceiverFile(
                WithFile(t, pos, f with { State = FileState.ChecksumFailed }),
                new Fx.DeleteTemp(f.Index),
                Result(t, f.Index, FileResultStatus.ChecksumFailed)),
        };
    }

    private static Step<TransferTask, TransferEffect> OnWriteFailed(TransferTask t, Ev.WriteFailed e)
    {
        var pos = CurrentPos(t);
        if (pos < 0 || t.Files[pos].Index != e.Index)
        {
            return Of(t);
        }

        var f = t.Files[pos];
        return Of(
            Interrupted(WithFile(t, pos, f with { State = FileState.WriteFailed }), InterruptReason.WriteFailed, e.Detail),
            Result(t, f.Index, FileResultStatus.WriteFailed, e.Detail));
    }

    private static Step<TransferTask, TransferEffect> OnPeerReadFailed(TransferTask t, Ev.PeerReadFailed e)
    {
        if (TargetPos(t, e.Index) is not { } pos)
        {
            return Of(t);
        }

        if (pos < 0)
        {
            return Violation(t);
        }

        var f = t.Files[pos];
        return AfterReceiverFile(WithFile(t, pos, f with { State = FileState.ReadFailed }), new Fx.DeleteTemp(f.Index));
    }

    // ---- 双方 ----

    private static Step<TransferTask, TransferEffect> OnDisconnected(TransferTask t, Ev.Disconnected e) => t.State switch
    {
        TaskState.Offering or TaskState.AwaitingUser => Of(t with { State = TaskState.ConnectionLost }, new Fx.CancelOfferTimer()),
        TaskState.Running => t.Direction == Direction.Send
            ? Of(Interrupted(t, e.Reason, e.Detail), new Fx.StopSending())
            : Of(Interrupted(t, e.Reason, e.Detail)),
        TaskState.Resuming => Of(Interrupted(t, e.Reason, e.Detail), new Fx.AbortResume()),
        _ => Of(t),
    };

    /// <summary>接收方：删除全部未完成文件的临时文件，保留已完成的文件（18.3）。</summary>
    private static IEnumerable<TransferEffect> DeleteUnfinishedTemps(TransferTask t) =>
        t.Direction == Direction.Receive ? t.Files.Where(f => !f.State.IsTerminal()).Select(TransferEffect (f) => new Fx.DeleteTemp(f.Index)) : [];

    private static TransferTask Cancelled(TransferTask t, TaskState state) => t with { State = state, Interrupt = null };

    private static Step<TransferTask, TransferEffect> OnLocalCancel(TransferTask t, Ev.LocalCancel e)
    {
        var notify = new Fx.Send(new TransferCancel(t.TransferId, e.Reason));
        TransferEffect[] stop = t.Direction == Direction.Send ? [new Fx.StopSending()] : [];
        return t.State switch
        {
            TaskState.Offering => Of(Cancelled(t, TaskState.Cancelled), new Fx.CancelOfferTimer(), notify),

            // 接收方尚未响应：按拒绝处理（20.5、C4.5）
            TaskState.AwaitingUser => Of(Cancelled(t, TaskState.Rejected), new Fx.CancelOfferTimer(), Reply(t, OfferDecision.Reject)),
            TaskState.Running => Of(Cancelled(t, TaskState.Cancelled), [.. stop, notify, .. DeleteUnfinishedTemps(t)]),
            TaskState.Interrupted => Of(Cancelled(t, TaskState.Cancelled), [new Fx.AddPendingCancel(e.Reason), .. DeleteUnfinishedTemps(t)]),
            TaskState.Resuming => Of(
                Cancelled(t, TaskState.Cancelled),
                [new Fx.AbortResume(), .. stop, e.SessionActive ? notify : (TransferEffect)new Fx.AddPendingCancel(e.Reason), .. DeleteUnfinishedTemps(t)]),
            _ => Of(t),
        };
    }

    private static Step<TransferTask, TransferEffect> OnPeerCancelled(TransferTask t)
    {
        var effects = new List<TransferEffect>();
        if (t.State is TaskState.Offering or TaskState.AwaitingUser)
        {
            effects.Add(new Fx.CancelOfferTimer());
        }
        else if (t.State == TaskState.Resuming)
        {
            effects.Add(new Fx.AbortResume());
        }

        if (t.Direction == Direction.Send && t.State == TaskState.Running)
        {
            effects.Add(new Fx.StopSending());
        }

        // Offer 阶段还没有临时文件
        if (t.State is not (TaskState.Offering or TaskState.AwaitingUser))
        {
            effects.AddRange(DeleteUnfinishedTemps(t));
        }

        return Of(Cancelled(t, TaskState.PeerCancelled), [.. effects]);
    }

    private static Step<TransferTask, TransferEffect> OnResumePlanned(TransferTask t, Ev.ResumePlanned e)
    {
        if (t.State != TaskState.Resuming)
        {
            return Of(t);
        }

        var byIndex = new Dictionary<int, ResumePlanEntry>();
        foreach (var p in e.Plan)
        {
            byIndex[p.Index] = p;
        }

        var effects = new List<TransferEffect>();
        var files = new List<TransferFile>();
        foreach (var f in t.Files)
        {
            if (!byIndex.TryGetValue(f.Index, out var p))
            {
                files.Add(f);
                continue;
            }

            switch (p.Action)
            {
                case ResumeAction.Skip:
                    files.Add(f.State.IsTerminal() ? f : f with { State = FileState.Completed, Persisted = f.Size });
                    break;
                case ResumeAction.Continue:
                    files.Add(f.State.IsTerminal() ? f : f with { State = FileState.Waiting, Persisted = Math.Min(p.Start, f.Size) });
                    break;
                default:
                    if (t.Direction == Direction.Receive)
                    {
                        effects.Add(new Fx.DeleteTemp(f.Index));
                    }

                    files.Add(f.State.IsTerminal()
                        ? f
                        : f with { State = FileState.Waiting, Persisted = 0, Size = p.Size ?? f.Size, Mtime = p.Size is not null ? p.Mtime : f.Mtime, Sha256 = null });
                    break;
            }
        }

        var running = t with { State = TaskState.Running, Interrupt = null, Files = new ValueList<TransferFile>(files) };
        if (t.Direction == Direction.Send)
        {
            effects.Add(new Fx.Send(new ResumePlan(t.TransferId, new ValueList<ResumeFilePlan>(e.Plan.Select(p => p.ToWire())))));
            return StartNextSender(running, [.. effects]);
        }

        return AfterReceiverFile(running, [.. effects]);
    }

    private static Step<TransferTask, TransferEffect> OnRestored(TransferTask t) => t.State switch
    {
        TaskState.Running or TaskState.Resuming => Of(Interrupted(t, InterruptReason.AppKilled)),

        // 只有已被接受的任务才会进入“已中断”（C1.13）
        TaskState.Offering or TaskState.AwaitingUser => Of(t with { State = TaskState.ConnectionLost }),
        _ => Of(t),
    };
}
