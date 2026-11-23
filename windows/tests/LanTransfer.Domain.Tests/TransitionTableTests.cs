using System.Text;
using LanTransfer.Protocol;
using Xunit;
using static LanTransfer.Domain.Tests.ReducerFixtures;
using Fx = LanTransfer.Domain.PairingEffect;
using Pe = LanTransfer.Domain.PairingEvent;
using Ps = LanTransfer.Domain.PairingSession;
using Te = LanTransfer.Domain.TransferEvent;
using Tf = LanTransfer.Domain.TransferEffect;
using Xe = LanTransfer.Domain.TextEvent;
using Xf = LanTransfer.Domain.TextEffect;

namespace LanTransfer.Domain.Tests;

/// <summary>状态迁移表驱动测试（任务 12.7）：M3.1–M3.4 中每条迁移至少 1 个示例，与 Kotlin 侧 TransitionTableTest 对应。</summary>
public sealed class TransitionTableTests
{
    // ---- 配对流程的固定数据 ----

    private static readonly PeerInfo PfI = PeerInfoOf(1);
    private static readonly PeerInfo PfR = PeerInfoOf(2);
    private static readonly ConnId PfConn = new(7);
    private static readonly Bytes PfNI = Bytes32(40);
    private static readonly Bytes PfNR = Bytes32(80);
    private static readonly Bytes PfCommit = Bytes.Of(VerificationCode.Commitment(PfI.PublicKey.ToArray(), PfR.PublicKey.ToArray(), PfNI.ToArray()));
    private static readonly string PfSas = VerificationCode.Sas(PfI.PublicKey.ToArray(), PfR.PublicKey.ToArray(), PfNI.ToArray(), PfNR.ToArray());

    private static Step<PairingState, PairingEffect> Pr(PairingState s, PairingEvent e) => PairingReducer.Reduce(s, e);

    private static Fx.Finished Fin(PeerInfo peer, PairingRole role, PairingResult result) => new(new PairingDone(peer.DeviceId, role, result));

    private static Pe.MessageReceived Msg(ProtocolMessage m, long now) => new(PfConn, m, now);

    private static void Effects<TS, TE>(Step<TS, TE> step, params TE[] expected) =>
        Assert.True(expected.SequenceEqual(step.Effects), $"effects=[{string.Join(", ", step.Effects)}] expected=[{string.Join(", ", expected)}]");

    private static void Unchanged<TS, TE>(TS before, Step<TS, TE> step)
    {
        Assert.Equal(before, step.State);
        Assert.Empty(step.Effects);
    }

    /// <summary>发起方：t = 1 000 ms 时已发出 PAIR_REQUEST，截止 61 000 ms。</summary>
    private static PairingState InitiatorAwaiting()
    {
        var connecting = Pr(new PairingState(), new Pe.StartPair(PfR.DeviceId, 0)).State;
        return Pr(connecting, new Pe.Connected(PfConn, PfR, PfI.PublicKey, PfNI, 1_000)).State;
    }

    private static PairingState InitiatorSas() => Pr(InitiatorAwaiting(), Msg(new PairNonce(PfNR), 2_000)).State;

    private static PairingState InitiatorSaving() => Pr(InitiatorSas(), Msg(new PairDecision(PairDecisionCode.Accepted), 3_000)).State;

    /// <summary>接收方：t = 1 000 ms 时收到 PAIR_REQUEST，截止 61 000 ms。</summary>
    private static PairingState ReceiverShowing() => Pr(new PairingState(), new Pe.PairRequestReceived(PfConn, PfI, PfR.PublicKey, PfCommit, PfNR, 1_000)).State;

    private static PairingState ReceiverRevealed() => Pr(ReceiverShowing(), Msg(new PairReveal(PfNI), 2_000)).State;

    /// <summary>接收方：t = 5 000 ms 时点击“接受”，等待 PAIR_COMPLETE 至 20 000 ms。</summary>
    private static PairingState ReceiverCompleting() => Pr(ReceiverRevealed(), new Pe.UserDecision(true, 5_000)).State;

    private static PairingState ReceiverSaving() => Pr(ReceiverCompleting(), Msg(PairComplete.Instance, 6_000)).State;

    // ---- 传输任务的辅助函数 ----

    private static readonly TransferId Tid = FixtureTransferId;
    private static readonly Bytes Sha = Bytes32(3);

    private static Step<TransferTask, TransferEffect> Tr(TransferTask t, TransferEvent e) => TransferReducer.Reduce(t, e);

    private static TransferTask RunningSender(params long[] sizes) => Tr(MakeTask(Direction.Send, TaskState.Offering, sizes), new Te.OfferReplied(OfferDecision.Accept)).State;

    private static TransferTask RunningReceiver(params long[] sizes) => MakeTask(Direction.Receive, TaskState.Running, sizes);

    private static Tf.Send FileResultOf(int i, FileResultStatus s, string? detail = null) => new(new FileResult(Tid, (uint)i, s, detail));

    private static Tf.Send OfferReply(OfferDecision d) => new(new FileOfferReply(Tid, d));

    private static Tf.Send CancelMsg(TransferCancelReason reason = TransferCancelReason.User) => new(new TransferCancel(Tid, reason));

    private static Te.ChunkWritten Chunk(int i, long persisted) => new(i, persisted, FixtureLocalNow, 1_700_000_000_000);

    private static Te.EndReceived End(int i) => new(i, Sha, FixtureLocalNow);

    private static VerifyOutcome.Committed CommittedAs(string name) => new(name, $"D:\\接收\\{name}", new StoredFileRef($"D:\\接收\\{name}"));

    private static FileState[] StatesOf(TransferTask t) => t.Files.Select(f => f.State).ToArray();

    // ==== M3.1 TextStatus ====

    private static readonly TextKey Key = new(DeviceIdOf(1), new MessageId(Bytes16.Of(Enumerable.Repeat((byte)3, 16).ToArray())));
    private static readonly Bytes Body = Bytes.Of(Encoding.UTF8.GetBytes("你好"));
    private const long TextSentAt = 1_700_000_000_000;
    private static readonly Xf.Transmit TextTransmit = new(Key.Peer, new Text(Key.MessageId, TextSentAt, Body));
    private static readonly TextRecord SendingRecord = new(1, Key.Peer, "对方", Key.MessageId, Direction.Send, Body, TextSentAt, new RecordTime(TextSentAt, 480), TextStatus.Sending);

    private static TextState Sending() => TextReducer.Reduce(new TextState(), new Xe.Send(Key, Body, TextSentAt, 0)).State;

    private static TextState FailedText() => TextReducer.Reduce(Sending(), new Xe.Tick(10_000)).State;

    private static TextState DeliveredText() => TextReducer.Reduce(Sending(), new Xe.AckReceived(Key)).State;

    private static OutgoingText Item(TextState s) => s.Outgoing[Key];

    [Fact]
    public void Text_Start_Sending_Timer10s()
    {
        var st = TextReducer.Reduce(new TextState(), new Xe.Send(Key, Body, TextSentAt, 0));
        Effects(st, TextTransmit, new Xf.ArmTimer(10_000));
        Assert.Equal(TextStatus.Sending, Item(st.State).Status);
        Assert.Equal(10_000L, Item(st.State).Deadline);
        Assert.False(Item(st.State).ShowRetry);
    }

    [Fact]
    public void Text_Sending_Ack_Delivered()
    {
        var st = TextReducer.Reduce(Sending(), new Xe.AckReceived(Key));
        Effects(st, new Xf.SaveStatus(Key, TextStatus.Delivered));
        Assert.Equal(TextStatus.Delivered, Item(st.State).Status);
    }

    [Fact]
    public void Text_Sending_Timeout_Failed_ShowsRetry()
    {
        Unchanged(Sending(), TextReducer.Reduce(Sending(), new Xe.Tick(9_999)));
        Effects(TextReducer.Reduce(Sending(), new Xe.Tick(10_000)), new Xf.SaveStatus(Key, TextStatus.Failed));
        Assert.Equal(TextStatus.Failed, Item(FailedText()).Status);
        Assert.Null(Item(FailedText()).Deadline);
        Assert.True(Item(FailedText()).ShowRetry);
    }

    [Fact]
    public void Text_Sending_Corrupt_Failed()
    {
        var st = TextReducer.Reduce(Sending(), new Xe.CorruptReceived(Key));
        Effects(st, new Xf.SaveStatus(Key, TextStatus.Failed));
        Assert.True(Item(st.State).ShowRetry);
    }

    [Fact]
    public void Text_Failed_Retry_Sending_SameIdAndText()
    {
        var st = TextReducer.Reduce(FailedText(), new Xe.Retry(Key, 20_000));
        Effects(st, new Xf.SaveStatus(Key, TextStatus.Sending), TextTransmit, new Xf.ArmTimer(30_000));
        Assert.Equal(TextStatus.Sending, Item(st.State).Status);
        Assert.Equal(30_000L, Item(st.State).Deadline);
    }

    [Fact]
    public void Text_Failed_LateAck_Delivered()
    {
        var st = TextReducer.Reduce(FailedText(), new Xe.AckReceived(Key));
        Effects(st, new Xf.SaveStatus(Key, TextStatus.Delivered));
        Assert.Equal(TextStatus.Delivered, Item(st.State).Status);
    }

    [Fact]
    public void Text_Sending_Restart_Failed()
    {
        var st = TextReducer.Reduce(new TextState(), new Xe.Restored(new ValueList<TextRecord>([SendingRecord])));
        Effects(st, new Xf.SaveStatus(Key, TextStatus.Failed));
        Assert.Equal(TextStatus.Failed, Item(st.State).Status);
        Assert.True(Item(st.State).ShowRetry);
        var kept = TextReducer.Reduce(new TextState(), new Xe.Restored(new ValueList<TextRecord>([SendingRecord with { Status = TextStatus.Delivered }])));
        Assert.Empty(kept.Effects);
        Assert.Equal(TextStatus.Delivered, Item(kept.State).Status);
    }

    [Fact]
    public void Text_DeliveredAndReceived_NeverChange()
    {
        var delivered = DeliveredText();
        TextEvent[] events = [new Xe.AckReceived(Key), new Xe.CorruptReceived(Key), new Xe.Retry(Key, 50_000), new Xe.Tick(1_000_000), new Xe.Send(Key, Body, TextSentAt, 60_000)];
        foreach (var e in events)
        {
            Unchanged(delivered, TextReducer.Reduce(delivered, e));
        }

        // 接收方记录（Received）不进入发送状态机，收到新消息时的 Received 由 TextService 写入（11.4）
        var received = SendingRecord with { Direction = Direction.Receive, Status = TextStatus.Received };
        Unchanged(new TextState(), TextReducer.Reduce(new TextState(), new Xe.Restored(new ValueList<TextRecord>([received]))));
    }

    [Fact]
    public void Text_Forget_LateAnswersIgnored()
    {
        var forgotten = TextReducer.Reduce(Sending(), new Xe.Forget(new ValueSet<TextKey>([Key]))).State;
        Assert.Empty(forgotten.Outgoing);
        Unchanged(forgotten, TextReducer.Reduce(forgotten, new Xe.AckReceived(Key)));
        Unchanged(forgotten, TextReducer.Reduce(forgotten, new Xe.Retry(Key, 1)));
    }

    // ==== M3.2 TaskState ====

    private static TransferTask Offering() => MakeTask(Direction.Send, TaskState.Offering, 100, 200);

    private static TransferTask Awaiting() => MakeTask(Direction.Receive, TaskState.AwaitingUser, 100, 200);

    [Fact]
    public void Task_Offering_Accept_Running_StartsFirstFile()
    {
        var st = Tr(Offering(), new Te.OfferReplied(OfferDecision.Accept));
        Assert.Equal(TaskState.Running, st.State.State);
        Assert.Equal(new[] { FileState.Transferring, FileState.Waiting }, StatesOf(st.State));
        Effects(st, new Tf.CancelOfferTimer(), new Tf.StartFile(0, 0));
    }

    [Theory]
    [InlineData(OfferDecision.Reject, TaskState.Rejected)]
    [InlineData(OfferDecision.Timeout, TaskState.NoResponse)]
    [InlineData(OfferDecision.NoSpace, TaskState.NoSpace)]
    [InlineData(OfferDecision.CannotSave, TaskState.CannotSave)]
    [InlineData(OfferDecision.InvalidOffer, TaskState.OfferInvalid)]
    public void Task_Offering_RejectingReplies_EndOfferPhase(OfferDecision decision, TaskState end)
    {
        var st = Tr(Offering(), new Te.OfferReplied(decision));
        Assert.Equal(end, st.State.State);
        Effects(st, new Tf.CancelOfferTimer());
    }

    [Fact]
    public void Task_Offering_SenderTimeout_NoResponse()
    {
        var st = Tr(Offering(), new Te.OfferTimedOut());
        Assert.Equal(Offering() with { State = TaskState.NoResponse }, st.State);
        Assert.Empty(st.Effects);
    }

    [Fact]
    public void Task_Offering_Disconnected_ConnectionLost()
    {
        var st = Tr(Offering(), new Te.Disconnected(InterruptReason.PeerClosed));
        Assert.Equal(TaskState.ConnectionLost, st.State.State);
        Assert.Null(st.State.Interrupt);
        Effects(st, new Tf.CancelOfferTimer());
    }

    [Fact]
    public void Task_Offering_LocalCancel_Cancelled()
    {
        var st = Tr(Offering(), new Te.LocalCancel(true));
        Assert.Equal(TaskState.Cancelled, st.State.State);
        Effects(st, new Tf.CancelOfferTimer(), CancelMsg());
    }

    [Fact]
    public void Task_AwaitingUser_Accept_Running_FixesReceiveDirectory()
    {
        var dir = new DirRef.Path("D:\\接收");
        var st = Tr(Awaiting(), new Te.UserAccepted(dir, true));
        Assert.Equal(TaskState.Running, st.State.State);
        Assert.Equal(dir, st.State.ReceiveDir);
        Effects(st, new Tf.CancelOfferTimer(), OfferReply(OfferDecision.Accept));
    }

    [Fact]
    public void Task_AwaitingUser_AcceptWithUnusableDirectory_CannotSave()
    {
        var st = Tr(Awaiting(), new Te.UserAccepted(DirRef.Default.Instance, false));
        Assert.Equal(TaskState.CannotSave, st.State.State);
        Effects(st, new Tf.CancelOfferTimer(), OfferReply(OfferDecision.CannotSave));
    }

    [Fact]
    public void Task_AwaitingUser_Reject_Rejected()
    {
        var st = Tr(Awaiting(), new Te.UserRejected());
        Assert.Equal(TaskState.Rejected, st.State.State);
        Effects(st, new Tf.CancelOfferTimer(), OfferReply(OfferDecision.Reject));
    }

    [Fact]
    public void Task_AwaitingUser_Timeout_NoResponse()
    {
        var st = Tr(Awaiting(), new Te.OfferTimedOut());
        Assert.Equal(TaskState.NoResponse, st.State.State);
        Effects(st, OfferReply(OfferDecision.Timeout));
    }

    [Fact]
    public void Task_AwaitingUser_Disconnected_ConnectionLost()
    {
        var st = Tr(Awaiting(), new Te.Disconnected(InterruptReason.LocalNetworkLost));
        Assert.Equal(TaskState.ConnectionLost, st.State.State);
        Effects(st, new Tf.CancelOfferTimer());
    }

    [Fact]
    public void Task_AwaitingUser_SenderCancels_PeerCancelled()
    {
        var st = Tr(Awaiting(), new Te.PeerCancelled(TransferCancelReason.User));
        Assert.Equal(TaskState.PeerCancelled, st.State.State);
        Effects(st, new Tf.CancelOfferTimer());
    }

    [Fact]
    public void Task_AwaitingUser_Abandon_Rejected()
    {
        var st = Tr(Awaiting(), new Te.LocalCancel(true));
        Assert.Equal(TaskState.Rejected, st.State.State);
        Effects(st, new Tf.CancelOfferTimer(), OfferReply(OfferDecision.Reject));
    }

    [Fact]
    public void Task_Running_AllFilesTerminal_Completed()
    {
        var sender = RunningSender(100);
        sender = Tr(sender, new Te.FileEndSent(0, Sha)).State;
        sender = Tr(sender, new Te.AckReceived(0, 100)).State;
        var st = Tr(sender, new Te.ResultReceived(0, FileResultStatus.Completed, null));
        Assert.Equal(TaskState.Completed, st.State.State);
        Assert.Equal(FileState.Completed, st.State.Files.Single().State);
        Assert.Empty(st.Effects);

        var receiver = RunningReceiver(10);
        receiver = Tr(receiver, Chunk(0, 10)).State;
        receiver = Tr(receiver, End(0)).State;
        var done = Tr(receiver, new Te.VerifyDone(0, CommittedAs("a.txt")));
        Assert.Equal(TaskState.Completed, done.State.State);
        Effects(done, FileResultOf(0, FileResultStatus.Completed));
    }

    [Fact]
    public void Task_Running_Disconnected_Interrupted()
    {
        var st = Tr(RunningSender(100), new Te.Disconnected(InterruptReason.PeerUnresponsive));
        Assert.Equal(TaskState.Interrupted, st.State.State);
        Assert.Equal(new InterruptInfo(InterruptReason.PeerUnresponsive), st.State.Interrupt);
        Effects(st, new Tf.StopSending());
        Assert.Equal(new InterruptInfo(InterruptReason.ProtocolError), Tr(RunningReceiver(100), new Te.Disconnected(InterruptReason.ProtocolError)).State.Interrupt);
    }

    [Fact]
    public void Task_Running_WriteFailed_InterruptedWithDetail()
    {
        var sender = Tr(RunningSender(100), new Te.ResultReceived(0, FileResultStatus.WriteFailed, "存储空间不足"));
        Assert.Equal(TaskState.Interrupted, sender.State.State);
        Assert.Equal(new InterruptInfo(InterruptReason.WriteFailed, "存储空间不足"), sender.State.Interrupt);
        Assert.Equal(FileState.WriteFailed, sender.State.Files.Single().State);
        Effects(sender, new Tf.StopSending());

        var receiving = Tr(RunningReceiver(100), Chunk(0, 40)).State;
        var receiver = Tr(receiving, new Te.WriteFailed(0, "存储空间不足"));
        Assert.Equal(new InterruptInfo(InterruptReason.WriteFailed, "存储空间不足"), receiver.State.Interrupt);
        Effects(receiver, FileResultOf(0, FileResultStatus.WriteFailed, "存储空间不足"));
    }

    [Fact]
    public void Task_Interrupted_Resume_Resuming_ThenResumeFailed_Interrupted()
    {
        var resuming = Tr(MakeTask(Direction.Send, TaskState.Interrupted, 100), new Te.ResumeRequested());
        Assert.Equal(TaskState.Resuming, resuming.State.State);
        Assert.Null(resuming.State.Interrupt);
        Effects(resuming, new Tf.StartResume());
        var failed = Tr(resuming.State, new Te.ResumeFailed(InterruptReason.PeerUnresponsive));
        Assert.Equal(TaskState.Interrupted, failed.State.State);
        Assert.Equal(new InterruptInfo(InterruptReason.PeerUnresponsive), failed.State.Interrupt);
        Assert.Empty(failed.Effects);
    }

    [Fact]
    public void Task_Resuming_PlanExchanged_Running()
    {
        var resuming = Tr(MakeTask(Direction.Send, TaskState.Interrupted, 100), new Te.ResumeRequested()).State;
        var st = Tr(resuming, new Te.ResumePlanned(new ValueList<ResumePlanEntry>([new ResumePlanEntry(0, ResumeAction.Continue, 40)])));
        Assert.Equal(TaskState.Running, st.State.State);
        Assert.Equal(FileState.Transferring, st.State.Files.Single().State);
        Assert.Equal(40L, st.State.Files.Single().Persisted);
        Effects(
            st,
            new Tf.Send(new ResumePlan(Tid, new ValueList<ResumeFilePlan>([new ResumeFilePlan(0, ResumeAction.Continue, 40)]))),
            new Tf.StartFile(0, 40));
    }

    [Fact]
    public void Task_Running_LocalCancel_Cancelled_DeletesUnfinishedTempsKeepsCompleted()
    {
        Effects(Tr(RunningSender(100), new Te.LocalCancel(true)), new Tf.StopSending(), CancelMsg());

        var receiver = RunningReceiver(10, 20, 30);
        receiver = Tr(receiver, Chunk(0, 10)).State;
        receiver = Tr(receiver, End(0)).State;
        receiver = Tr(receiver, new Te.VerifyDone(0, CommittedAs("a.txt"))).State;
        receiver = Tr(receiver, Chunk(1, 5)).State;
        var st = Tr(receiver, new Te.LocalCancel(true));
        Assert.Equal(TaskState.Cancelled, st.State.State);
        Assert.Equal(receiver.Files, st.State.Files);
        Assert.Equal(FileState.Completed, st.State.Files[0].State);
        Effects(st, CancelMsg(), new Tf.DeleteTemp(1), new Tf.DeleteTemp(2));
    }

    [Fact]
    public void Task_Interrupted_LocalCancel_Cancelled_WritesPendingCancel()
    {
        var st = Tr(MakeTask(Direction.Receive, TaskState.Interrupted, 100, 200), new Te.LocalCancel(false));
        Assert.Equal(TaskState.Cancelled, st.State.State);
        Assert.Null(st.State.Interrupt);
        Effects(st, new Tf.AddPendingCancel(TransferCancelReason.User), new Tf.DeleteTemp(0), new Tf.DeleteTemp(1));
    }

    [Fact]
    public void Task_Running_PeerCancel_PeerCancelled()
    {
        var sender = Tr(RunningSender(100), new Te.PeerCancelled(TransferCancelReason.User));
        Assert.Equal(TaskState.PeerCancelled, sender.State.State);
        Effects(sender, new Tf.StopSending());
        Effects(Tr(RunningReceiver(100), new Te.PeerCancelled(TransferCancelReason.User)), new Tf.DeleteTemp(0));
    }

    [Fact]
    public void Task_Resuming_UnknownTransfer_PeerCancelled()
    {
        var resuming = Tr(MakeTask(Direction.Send, TaskState.Interrupted, 100), new Te.ResumeRequested()).State;
        var st = Tr(resuming, new Te.PeerCancelled(TransferCancelReason.UnknownTransfer));
        Assert.Equal(TaskState.PeerCancelled, st.State.State);
        Effects(st, new Tf.AbortResume());
    }

    [Fact]
    public void Task_Resuming_LocalCancel_NotifiesNowOrWritesPendingCancel()
    {
        var resuming = Tr(MakeTask(Direction.Send, TaskState.Interrupted, 100), new Te.ResumeRequested()).State;
        Effects(Tr(resuming, new Te.LocalCancel(true)), new Tf.AbortResume(), new Tf.StopSending(), CancelMsg());
        Effects(Tr(resuming, new Te.LocalCancel(false)), new Tf.AbortResume(), new Tf.StopSending(), new Tf.AddPendingCancel(TransferCancelReason.User));
    }

    [Fact]
    public void Task_Unpair_CancelsWithUnpairedReason()
    {
        var st = Tr(RunningSender(100), new Te.LocalCancel(true, TransferCancelReason.Unpaired));
        Assert.Equal(TaskState.Cancelled, st.State.State);
        Effects(st, new Tf.StopSending(), CancelMsg(TransferCancelReason.Unpaired));
    }

    [Fact]
    public void Task_StartupRecovery()
    {
        var running = Tr(RunningSender(100), new Te.Restored()).State;
        Assert.Equal(TaskState.Interrupted, running.State);
        Assert.Equal(new InterruptInfo(InterruptReason.AppKilled), running.Interrupt);
        var resuming = Tr(MakeTask(Direction.Receive, TaskState.Interrupted, 100), new Te.ResumeRequested()).State;
        Assert.Equal(new InterruptInfo(InterruptReason.AppKilled), Tr(resuming, new Te.Restored()).State.Interrupt);
        Assert.Equal(TaskState.ConnectionLost, Tr(Offering(), new Te.Restored()).State.State);
        Assert.Equal(TaskState.ConnectionLost, Tr(Awaiting(), new Te.Restored()).State.State);
        var interrupted = MakeTask(Direction.Send, TaskState.Interrupted, 100);
        Unchanged(interrupted, Tr(interrupted, new Te.Restored())); // 保留原来的中断原因
        var cancelled = MakeTask(Direction.Send, TaskState.Cancelled, 100);
        Unchanged(cancelled, Tr(cancelled, new Te.Restored())); // 被放弃的任务不会恢复为“已中断”
    }

    [Fact]
    public void Task_TerminalTasks_NeverTransition()
    {
        TransferEvent[] events =
        [
            new Te.LocalCancel(true), new Te.PeerCancelled(TransferCancelReason.User), new Te.ResumeRequested(),
            new Te.Restored(), new Te.Disconnected(InterruptReason.PeerClosed), new Te.OfferReplied(OfferDecision.Accept),
        ];
        foreach (var state in Enum.GetValues<TaskState>().Where(s => s.IsTerminal()))
        {
            foreach (var direction in Enum.GetValues<Direction>())
            {
                var task = MakeTask(direction, state, 100);
                foreach (var e in events)
                {
                    Unchanged(task, Tr(task, e));
                }
            }
        }
    }

    // ==== M3.3 FileState ====

    private static TransferTask Receiving() => RunningReceiver(100, 50);

    [Fact]
    public void File_Waiting_Transferring_RecordsFirstStartTime()
    {
        var st = Tr(Receiving(), new Te.ChunkWritten(0, 40, FixtureLocalNow, 111));
        var f = st.State.Files[0];
        Assert.Equal(FileState.Transferring, f.State);
        Assert.Equal(40L, f.Persisted);
        Assert.Equal(FixtureLocalNow, f.StartedAt);
        Assert.Equal(111L, f.LastWriteAtUtcMs);
        Assert.Empty(st.Effects);
        var later = Tr(st.State, new Te.ChunkWritten(0, 80, FixtureLocalNow.AddMinutes(5), 222)).State.Files[0];
        Assert.Equal(FixtureLocalNow, later.StartedAt);
        Assert.Equal(222L, later.LastWriteAtUtcMs);
    }

    [Fact]
    public void File_Waiting_ReadFailed_PeerReported()
    {
        var st = Tr(Receiving(), new Te.PeerReadFailed(0));
        Assert.Equal(TaskState.Running, st.State.State);
        Assert.Equal(new[] { FileState.ReadFailed, FileState.Waiting }, StatesOf(st.State));
        Effects(st, new Tf.DeleteTemp(0));
    }

    [Fact]
    public void File_Transferring_ReadFailed_SenderContinuesWithNext()
    {
        var st = Tr(RunningSender(100, 50), new Te.SourceReadFailed(0));
        Assert.Equal(new[] { FileState.ReadFailed, FileState.Transferring }, StatesOf(st.State));
        Effects(st, new Tf.Send(new FileReadFailed(Tid, 0)), new Tf.StartFile(1, 0));
    }

    [Fact]
    public void File_Transferring_Verifying_OnFileEnd()
    {
        var full = Tr(Receiving(), Chunk(0, 100)).State;
        var st = Tr(full, End(0));
        Assert.Equal(FileState.Verifying, st.State.Files[0].State);
        Assert.Equal(Sha, st.State.Files[0].Sha256);
        Effects(st, new Tf.Verify(0, Sha));

        var sent = Tr(RunningSender(100), new Te.FileEndSent(0, Sha));
        Assert.Equal(FileState.Verifying, sent.State.Files.Single().State);
        Assert.Empty(sent.Effects);
    }

    [Fact]
    public void File_ZeroBytes_WaitingToVerifying()
    {
        var st = Tr(RunningReceiver(0), End(0));
        Assert.Equal(FileState.Verifying, st.State.Files.Single().State);
        Effects(st, new Tf.Verify(0, Sha));
    }

    [Fact]
    public void File_Transferring_ChecksumFailed_WhenBeyondSize()
    {
        var partial = Tr(Receiving(), Chunk(0, 40)).State;
        var st = Tr(partial, Chunk(0, 101));
        Assert.Equal(TaskState.Running, st.State.State);
        Assert.Equal(new[] { FileState.ChecksumFailed, FileState.Waiting }, StatesOf(st.State));
        Effects(st, new Tf.DeleteTemp(0), FileResultOf(0, FileResultStatus.ChecksumFailed));

        // 校验失败之后仍在途的数据被忽略
        Unchanged(st.State, Tr(st.State, Chunk(0, 50)));
    }

    [Fact]
    public void File_Verifying_Completed_WhenCommitted()
    {
        var verifying = Tr(Tr(Receiving(), Chunk(0, 100)).State, End(0)).State;
        var st = Tr(verifying, new Te.VerifyDone(0, CommittedAs("a.txt")));
        var f = st.State.Files[0];
        Assert.Equal(FileState.Completed, f.State);
        Assert.Equal("a.txt", f.FinalName);
        Assert.Equal("D:\\接收\\a.txt", f.FullPath);
        Assert.Equal(new StoredFileRef("D:\\接收\\a.txt"), f.StoredRef);
        Assert.Equal(TaskState.Running, st.State.State);
        Effects(st, FileResultOf(0, FileResultStatus.Completed));
    }

    [Fact]
    public void File_Verifying_ChecksumFailed_ContinuesWithRest()
    {
        var verifying = Tr(Tr(Receiving(), Chunk(0, 100)).State, End(0)).State;
        var st = Tr(verifying, new Te.VerifyDone(0, new VerifyOutcome.ChecksumMismatch()));
        Assert.Equal(new[] { FileState.ChecksumFailed, FileState.Waiting }, StatesOf(st.State));
        Assert.Equal(TaskState.Running, st.State.State);
        Effects(st, new Tf.DeleteTemp(0), FileResultOf(0, FileResultStatus.ChecksumFailed));

        var sender = Tr(Tr(RunningSender(100, 50), new Te.FileEndSent(0, Sha)).State, new Te.ResultReceived(0, FileResultStatus.ChecksumFailed, null));
        Assert.Equal(new[] { FileState.ChecksumFailed, FileState.Transferring }, StatesOf(sender.State));
        Effects(sender, new Tf.StartFile(1, 0));
    }

    [Fact]
    public void File_Verifying_WriteFailed_WhenCommitFails()
    {
        var verifying = Tr(Tr(Receiving(), Chunk(0, 100)).State, End(0)).State;
        var st = Tr(verifying, new Te.VerifyDone(0, new VerifyOutcome.CommitFailed("重命名失败")));
        Assert.Equal(FileState.WriteFailed, st.State.Files[0].State);
        Assert.Equal(TaskState.Interrupted, st.State.State);
        Assert.Equal(new InterruptInfo(InterruptReason.WriteFailed, "重命名失败"), st.State.Interrupt);
        Effects(st, FileResultOf(0, FileResultStatus.WriteFailed, "重命名失败"));
    }

    [Fact]
    public void File_WriteFailed_Transferring_OnResumeWithStartEqualsSize()
    {
        var t = RunningSender(100);
        t = Tr(t, new Te.FileEndSent(0, Sha)).State;
        t = Tr(t, new Te.ResultReceived(0, FileResultStatus.WriteFailed, "重命名失败")).State;
        t = Tr(t, new Te.ResumeRequested()).State;
        var plan = ResumePlanner.Plan(t, [new ResumeFileState(0, ResumeFileStatus.Partial, 100)], [new SourceStat(100, t.Files[0].Mtime)]);
        Assert.Equal(new[] { new ResumePlanEntry(0, ResumeAction.Continue, 100) }, plan);
        var st = Tr(t, new Te.ResumePlanned(new ValueList<ResumePlanEntry>(plan)));
        Assert.Equal(FileState.Transferring, st.State.Files.Single().State);
        Assert.Equal(100L, st.State.Files.Single().Persisted);
        Assert.Contains((TransferEffect)new Tf.StartFile(0, 100), st.Effects);
    }

    [Fact]
    public void File_Restart_WhenSourceChanged()
    {
        var t = Tr(Receiving(), Chunk(0, 40)).State;
        t = Tr(t, new Te.Disconnected(InterruptReason.PeerClosed)).State;
        t = Tr(t, new Te.ResumeRequested()).State;
        var st = Tr(t, new Te.ResumePlanned(new ValueList<ResumePlanEntry>([new ResumePlanEntry(0, ResumeAction.Restart, 0, 200, 9), new ResumePlanEntry(1, ResumeAction.Continue, 0)])));
        var f = st.State.Files[0];
        Assert.Equal(FileState.Waiting, f.State);
        Assert.Equal(0L, f.Persisted);
        Assert.Equal(200L, f.Size);
        Assert.Equal(9L, f.Mtime);
        Assert.Equal(TaskState.Running, st.State.State);
        Effects(st, new Tf.DeleteTemp(0));
    }

    [Fact]
    public void File_SenderAckProgress_MonotonicAndCapped()
    {
        var t = RunningSender(100);
        var acked = Tr(t, new Te.AckReceived(0, 50)).State;
        Assert.Equal(50L, acked.Files[0].Persisted);
        Unchanged(acked, Tr(acked, new Te.AckReceived(0, 30)));
        Assert.Equal(100L, Tr(acked, new Te.AckReceived(0, 500)).State.Files[0].Persisted);
    }

    [Fact]
    public void File_OutOfOrderData_ProtocolError()
    {
        var three = RunningReceiver(100, 50, 70);
        TransferEvent[] events = [Chunk(1, 10), End(2), new Te.PeerReadFailed(5)];
        foreach (var e in events)
        {
            var st = Tr(three, e);
            Assert.Equal(TaskState.Interrupted, st.State.State);
            Assert.Equal(new InterruptInfo(InterruptReason.ProtocolError), st.State.Interrupt);
            Assert.Equal(three.Files, st.State.Files);
            Effects(st, new Tf.ProtocolViolation());
        }
    }

    // ==== M3.4 PairingState ====

    [Fact]
    public void Pairing_Initiator_SuccessPath()
    {
        var st = Pr(new PairingState(), new Pe.StartPair(PfR.DeviceId, 0));
        Assert.Equal(new Ps.Connecting(PfR.DeviceId, null, 10_000), st.State.Session);
        Effects(st, new Fx.ArmTimer(10_000));
        st = Pr(st.State, new Pe.Connected(PfConn, PfR, PfI.PublicKey, PfNI, 1_000));
        Assert.Equal(new Ps.AwaitingPeer(PfR.DeviceId, PfConn, PfR, PfI.PublicKey, PfNI, 61_000), st.State.Session);
        Effects(st, new Fx.Send(PfConn, new PairRequest(PfCommit)), new Fx.ArmTimer(61_000));
        st = Pr(st.State, Msg(new PairNonce(PfNR), 2_000));
        Assert.Equal(new Ps.ShowingSas(PfR.DeviceId, PfConn, PfR, PfSas, 61_000), st.State.Session);
        Effects(st, new Fx.Send(PfConn, new PairReveal(PfNI)), new Fx.ShowSas(PfSas));
        st = Pr(st.State, Msg(new PairDecision(PairDecisionCode.Accepted), 3_000));
        Assert.Equal(new Ps.Saving(PfR.DeviceId, PfConn, PfR, PairingRole.Initiator, null), st.State.Session);
        Effects(st, new Fx.SaveTrust(PfR));
        st = Pr(st.State, new Pe.TrustStored(PfR.DeviceId, true));
        Assert.Null(st.State.Session);
        Effects(st, new Fx.Send(PfConn, PairComplete.Instance), new Fx.PromoteSession(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Success));
    }

    [Fact]
    public void Pairing_Receiver_SuccessPath_StagesThenCommitsAfterPairComplete()
    {
        var st = Pr(new PairingState(), new Pe.PairRequestReceived(PfConn, PfI, PfR.PublicKey, PfCommit, PfNR, 1_000));
        Assert.Equal(new Ps.AwaitingUser(PfI.DeviceId, PfConn, PfI, PfR.PublicKey, PfCommit, PfNR, null, 61_000), st.State.Session);
        Effects(st, new Fx.Send(PfConn, new PairNonce(PfNR)), new Fx.ShowRequest(PfI), new Fx.ArmTimer(61_000));

        // 显示验证码之前不能接受
        Unchanged(st.State, Pr(st.State, new Pe.UserDecision(true, 1_500)));
        st = Pr(st.State, Msg(new PairReveal(PfNI), 2_000));
        Effects(st, new Fx.ShowSas(PfSas)); // 与发起方显示的验证码相同
        st = Pr(st.State, new Pe.UserDecision(true, 5_000));
        Assert.Equal(new Ps.AwaitingComplete(PfI.DeviceId, PfConn, PfI, PairingRole.Receiver, null, 20_000), st.State.Session);
        Effects(st, new Fx.StageTrust(PfI), new Fx.Send(PfConn, new PairDecision(PairDecisionCode.Accepted)), new Fx.ArmTimer(20_000));
        st = Pr(st.State, Msg(PairComplete.Instance, 6_000));
        Assert.Equal(new Ps.Saving(PfI.DeviceId, PfConn, PfI, PairingRole.Receiver, null), st.State.Session);
        Effects(st, new Fx.CommitTrust(PfI.DeviceId));
        st = Pr(st.State, new Pe.TrustStored(PfI.DeviceId, true));
        Assert.Null(st.State.Session);
        Effects(st, new Fx.PromoteSession(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.Success));
    }

    [Fact]
    public void Pairing_QrScanner_SuccessAndTokenInvalid()
    {
        var token = Bytes16.Of(Enumerable.Repeat((byte)9, 16).ToArray());
        var st = Pr(new PairingState(), new Pe.StartQrPair(PfR.DeviceId, token, 0));
        Assert.Equal(new Ps.Connecting(PfR.DeviceId, token, 20_000), st.State.Session);
        Effects(st, new Fx.ArmTimer(20_000));
        st = Pr(st.State, new Pe.Connected(PfConn, PfR, PfI.PublicKey, PfNI, 4_000));
        Assert.Equal(new Ps.AwaitingQrResult(PfR.DeviceId, PfConn, PfR, 19_000), st.State.Session);
        Effects(st, new Fx.Send(PfConn, new QrPairRequest(token)), new Fx.ArmTimer(19_000));
        var awaitingResult = st.State;
        Effects(Pr(awaitingResult, Msg(new QrPairResult(QrPairResultCode.TokenInvalid), 5_000)), new Fx.Close(PfConn), Fin(PfR, PairingRole.QrScanner, PairingResult.TokenInvalid));
        st = Pr(awaitingResult, Msg(new QrPairResult(QrPairResultCode.Ok), 5_000));
        Assert.Equal(new Ps.Saving(PfR.DeviceId, PfConn, PfR, PairingRole.QrScanner, null), st.State.Session);
        Effects(st, new Fx.SaveTrust(PfR));
        st = Pr(st.State, new Pe.TrustStored(PfR.DeviceId, true));
        Effects(st, new Fx.Send(PfConn, PairComplete.Instance), new Fx.PromoteSession(PfConn), Fin(PfR, PairingRole.QrScanner, PairingResult.Success));
    }

    private sealed class OnesRng : IRng
    {
        public byte[] NextBytes(int n) => Enumerable.Repeat((byte)1, n).ToArray();
    }

    [Fact]
    public void Pairing_QrDisplay_StagesCommitsAndMarksTokenUsed_ReleasesOnDisconnect()
    {
        var registry = new PairingTokenRegistry(new OnesRng());
        var token = registry.Issue(0);
        var lease = Assert.IsType<TokenCheck.Ok>(registry.Check(token.Value.ToArray(), 1)).Lease;
        var st = Pr(new PairingState(), new Pe.QrRequestChecked(PfConn, PfI, lease, 1_000));
        Assert.Equal(new Ps.AwaitingComplete(PfI.DeviceId, PfConn, PfI, PairingRole.QrDisplay, lease, 16_000), st.State.Session);
        Effects(st, new Fx.Send(PfConn, new QrPairResult(QrPairResultCode.Ok)), new Fx.StageTrust(PfI), new Fx.ArmTimer(16_000));
        var staged = st.State;
        Effects(
            Pr(staged, new Pe.Disconnected(PfConn, 2_000)),
            new Fx.DiscardStagedTrust(PfI.DeviceId), new Fx.ReleaseToken(lease), Fin(PfI, PairingRole.QrDisplay, PairingResult.Disconnected));
        st = Pr(staged, Msg(PairComplete.Instance, 2_000));
        Effects(st, new Fx.CommitTrust(PfI.DeviceId));
        st = Pr(st.State, new Pe.TrustStored(PfI.DeviceId, true));
        Effects(st, new Fx.MarkTokenUsed(lease), new Fx.PromoteSession(PfConn), Fin(PfI, PairingRole.QrDisplay, PairingResult.Success));

        // 令牌无效：回复 TOKEN_INVALID 并关闭，不进入配对（8.9）
        var invalid = Pr(new PairingState(), new Pe.QrRequestChecked(PfConn, PfI, null, 1_000));
        Assert.Equal(new PairingState(), invalid.State);
        Effects(invalid, new Fx.Send(PfConn, new QrPairResult(QrPairResultCode.TokenInvalid)), new Fx.Close(PfConn));
    }

    [Fact]
    public void Pairing_Reject_ThenCooldownAutoRejectsSameDevice()
    {
        var st = Pr(ReceiverRevealed(), new Pe.UserDecision(false, 10_000));
        Assert.Null(st.State.Session);
        Assert.Equal(new ValueMap<DeviceId, long>([new(PfI.DeviceId, 10_000L)]), st.State.RejectedAt);
        Effects(st, new Fx.Send(PfConn, new PairDecision(PairDecisionCode.Rejected)), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.Rejected));
        var conn2 = new ConnId(8);
        Effects(
            Pr(st.State, new Pe.PairRequestReceived(conn2, PfI, PfR.PublicKey, PfCommit, PfNR, 69_999)),
            new Fx.Send(conn2, new PairDecision(PairDecisionCode.Rejected)), new Fx.Close(conn2), new Fx.AutoRejected(PfI.DeviceId, PairingResult.AutoRejected));
        Assert.IsType<Ps.AwaitingUser>(Pr(st.State, new Pe.PairRequestReceived(conn2, PfI, PfR.PublicKey, PfCommit, PfNR, 70_000)).State.Session);

        // 发起方：拒绝与自动拒绝都提示“对方拒绝了配对请求”
        Effects(Pr(InitiatorSas(), Msg(new PairDecision(PairDecisionCode.Rejected), 4_000)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Rejected));
        Effects(Pr(InitiatorAwaiting(), Msg(new PairDecision(PairDecisionCode.Rejected), 1_500)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Rejected));
    }

    [Fact]
    public void Pairing_Busy_WhileShowingAnotherRequest()
    {
        var other = PeerInfoOf(3);
        var conn2 = new ConnId(8);
        var showing = ReceiverShowing();
        var st = Pr(showing, new Pe.PairRequestReceived(conn2, other, PfR.PublicKey, PfCommit, PfNR, 2_000));
        Assert.Equal(showing, st.State);
        Effects(st, new Fx.Send(conn2, new PairDecision(PairDecisionCode.Busy)), new Fx.Close(conn2), new Fx.AutoRejected(other.DeviceId, PairingResult.Busy));
        Effects(Pr(InitiatorAwaiting(), Msg(new PairDecision(PairDecisionCode.Busy), 1_500)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Busy));
    }

    [Fact]
    public void Pairing_Timeout60s()
    {
        var awaitingPeer = InitiatorAwaiting();
        Unchanged(awaitingPeer, Pr(awaitingPeer, new Pe.Tick(60_999)));
        PairingEffect[] timeout = [new Fx.Send(PfConn, new PairCancel(PairCancelReason.Timeout)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Timeout)];
        Effects(Pr(awaitingPeer, new Pe.Tick(61_000)), timeout);
        Effects(Pr(InitiatorSas(), new Pe.Tick(61_000)), timeout);
        Effects(Pr(InitiatorSas(), Msg(new PairDecision(PairDecisionCode.Timeout), 61_000)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Timeout));

        var revealed = ReceiverRevealed();
        Unchanged(revealed, Pr(revealed, new Pe.Tick(60_999)));
        Effects(
            Pr(revealed, new Pe.Tick(61_000)),
            new Fx.Send(PfConn, new PairDecision(PairDecisionCode.Timeout)), new Fx.DismissRequest(), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.Timeout));
        Effects(
            Pr(revealed, Msg(new PairCancel(PairCancelReason.Timeout), 61_000)),
            new Fx.DismissRequest(), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.Timeout));
    }

    [Fact]
    public void Pairing_InitiatorCancel()
    {
        PairingEffect[] cancel = [new Fx.Send(PfConn, new PairCancel(PairCancelReason.User)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Cancelled)];
        Effects(Pr(InitiatorAwaiting(), new Pe.UserCancel(1_500)), cancel);
        Effects(Pr(InitiatorSas(), new Pe.UserCancel(2_500)), cancel);
        var connecting = Pr(new PairingState(), new Pe.StartPair(PfR.DeviceId, 0)).State;
        Effects(Pr(connecting, new Pe.UserCancel(10)), new Fx.CancelConnect(), Fin(PfR, PairingRole.Initiator, PairingResult.Cancelled));
        Effects(Pr(ReceiverShowing(), Msg(new PairCancel(PairCancelReason.User), 2_000)), new Fx.DismissRequest(), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.Cancelled));
        Effects(
            Pr(ReceiverCompleting(), Msg(new PairCancel(PairCancelReason.User), 6_000)),
            new Fx.DiscardStagedTrust(PfI.DeviceId), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.Cancelled));
    }

    [Fact]
    public void Pairing_CommitmentMismatch()
    {
        Effects(Pr(ReceiverShowing(), Msg(new PairReveal(Bytes32(41)), 2_000)), new Fx.DismissRequest(), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.CodeMismatch));
    }

    [Fact]
    public void Pairing_Disconnected_NoTrustKept()
    {
        Effects(Pr(ReceiverShowing(), new Pe.Disconnected(PfConn, 2_000)), new Fx.DismissRequest(), Fin(PfI, PairingRole.Receiver, PairingResult.Disconnected));
        Effects(Pr(ReceiverCompleting(), new Pe.Disconnected(PfConn, 6_000)), new Fx.DiscardStagedTrust(PfI.DeviceId), Fin(PfI, PairingRole.Receiver, PairingResult.Disconnected));
        Effects(
            Pr(ReceiverCompleting(), new Pe.Tick(20_000)),
            new Fx.DiscardStagedTrust(PfI.DeviceId), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.Disconnected));
        Effects(Pr(InitiatorAwaiting(), new Pe.Disconnected(PfConn, 1_500)), Fin(PfR, PairingRole.Initiator, PairingResult.Disconnected));
        Effects(Pr(InitiatorSaving(), new Pe.Disconnected(PfConn, 3_500)), new Fx.DeleteTrust(PfR.DeviceId), Fin(PfR, PairingRole.Initiator, PairingResult.Disconnected));

        // 接收方已收到 PAIR_COMPLETE：结局由提交结果决定
        var saving = ReceiverSaving();
        Unchanged(saving, Pr(saving, new Pe.Disconnected(PfConn, 7_000)));
    }

    [Fact]
    public void Pairing_ConnectFailed_WithinTenSeconds()
    {
        var connecting = Pr(new PairingState(), new Pe.StartPair(PfR.DeviceId, 0)).State;
        Effects(Pr(connecting, new Pe.ConnectFailed(500)), Fin(PfR, PairingRole.Initiator, PairingResult.ConnectFailed));
        Unchanged(connecting, Pr(connecting, new Pe.Tick(9_999)));
        Effects(Pr(connecting, new Pe.Tick(10_000)), new Fx.CancelConnect(), Fin(PfR, PairingRole.Initiator, PairingResult.ConnectFailed));
    }

    [Fact]
    public void Pairing_StoreFailed()
    {
        Effects(Pr(InitiatorSaving(), new Pe.TrustStored(PfR.DeviceId, false)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.StoreFailed));
        Effects(
            Pr(ReceiverSaving(), new Pe.TrustStored(PfI.DeviceId, false)),
            new Fx.DiscardStagedTrust(PfI.DeviceId), new Fx.Close(PfConn), Fin(PfI, PairingRole.Receiver, PairingResult.StoreFailed));
    }

    [Fact]
    public void Pairing_LateOrUnrelatedMessagesIgnored_OnlyOnePairingAtATime()
    {
        var awaitingPeer = InitiatorAwaiting();
        Unchanged(awaitingPeer, Pr(awaitingPeer, new Pe.MessageReceived(new ConnId(99), new PairNonce(PfNR), 1_500)));
        Unchanged(new PairingState(), Pr(new PairingState(), Msg(new PairDecision(PairDecisionCode.Accepted), 0)));
        Unchanged(awaitingPeer, Pr(awaitingPeer, new Pe.StartPair(PeerInfoOf(3).DeviceId, 1_500)));
        Effects(Pr(new PairingState(), new Pe.Connected(PfConn, PfR, PfI.PublicKey, PfNI, 0)), new Fx.Close(PfConn));

        // 未揭示随机数前收到 ACCEPTED：按协议错误结束
        Effects(Pr(awaitingPeer, Msg(new PairDecision(PairDecisionCode.Accepted), 1_500)), new Fx.Close(PfConn), Fin(PfR, PairingRole.Initiator, PairingResult.Disconnected));
    }
}
