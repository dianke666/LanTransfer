using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;
using static LanTransfer.Engine.Tests.XferKit;

namespace LanTransfer.Engine.Tests;

/// <summary>FileTransferEngine 的恢复、清理与放弃（任务 18.6、18.9，与 Kotlin 侧 FileTransferRecoveryTest 对应）。</summary>
public sealed class FileTransferRecoveryTests
{
    // ---- pending_cancel（18.9） ----

    [Fact]
    public async Task CancelInterrupted_LocalCancelledAndPendingCancelSaved_DeliveredWithin2sOfNextSession()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = await w.InterruptedTransferAsync(gate);
        w.A.Engine.Cancel(id);
        await w.A.AwaitStateAsync(id, TaskState.Cancelled);
        Assert.Equal([id], w.A.Db.PendingCancels().Select(p => p.TransferId).ToArray());
        Assert.Equal(TaskState.Interrupted, w.B.State(id));
        var t0 = w.Clock.MonotonicMs();
        Assert.IsType<OpenResult.Ok>(await Kit.Within(w.A.D.Manager.OpenAsync(new Target.Device(w.B.D.Id))));
        await w.B.AwaitStateAsync(id, TaskState.PeerCancelled);
        Assert.True(w.Clock.MonotonicMs() - t0 <= 2_000);
        await Kit.WaitUntilAsync(() => w.A.Db.PendingCancels().Count == 0, "pending_cancel 删除");
        await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件");
    }

    [Fact]
    public async Task CancelInterrupted_WithExistingSession_DeliveredImmediately()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = await w.InterruptedTransferAsync(gate);
        Assert.IsType<OpenResult.Ok>(await Kit.Within(w.A.D.Manager.OpenAsync(new Target.Device(w.B.D.Id))));
        w.A.Engine.Cancel(id);
        await w.B.AwaitStateAsync(id, TaskState.PeerCancelled);
        await Kit.WaitUntilAsync(() => w.A.Db.PendingCancels().Count == 0, "pending_cancel 已送达");
    }

    // ---- E4：提交失败与接收目录 ----

    [Fact]
    public async Task CommitFails_FileWriteFailed_TaskInterrupted_TempKept_ResumeRevalidatesAndCommits()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        w.B.Storage.CommitFailure = _ => "被占用";
        var r = w.A.File("doc.pdf", 300_000);
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [r]));
        await w.B.AwaitStateAsync(id, TaskState.Interrupted);
        await w.A.AwaitStateAsync(id, TaskState.Interrupted);
        Assert.Equal(new InterruptInfo(InterruptReason.WriteFailed, "被占用"), w.B.TaskOf(id).Interrupt);
        Assert.Equal(FileState.WriteFailed, w.B.TaskOf(id).Files[0].State);
        Assert.Equal(300_000L, w.B.Storage.Length(Assert.Single(w.B.Storage.TempRefs)));
        w.B.Storage.CommitFailure = _ => null;
        Assert.Equal(new ResumeResult.Started(), await Kit.Within(w.B.Engine.ResumeAsync(id)));
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(w.A.Content(r), w.B.Received()["doc.pdf"]);
    }

    [Fact]
    public async Task ReceiveDirFixedAtAccept_LaterSettingChangeDoesNotAffectTask()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = await w.InterruptedTransferAsync(gate);
        var other = new DirRef.Path("/other");
        w.B.Settings.Update(_ => new Settings(other));
        gate.Release();
        Assert.Equal(new ResumeResult.Started(), await Kit.Within(w.A.Engine.ResumeAsync(id)));
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(["big"], w.B.Received().Keys.ToArray());
        Assert.Empty(w.B.Storage.FilesIn(other));
        Assert.Equal(DirRef.Default.Instance, w.B.TaskOf(id).ReceiveDir);
    }

    // ---- 启动恢复与临时文件清理（17.7、17.9、M3.2） ----

    [Fact]
    public async Task Restore_RunningResumingToAppKilled_OfferingAwaitingToConnectionLost_InterruptedKeepsReason_TerminalUnchanged()
    {
        using var w = new XferWorld();
        TransferTask Make(TransferId id, TaskState state, Direction dir = Direction.Send, InterruptInfo? interrupt = null) => new(
            id, w.B.D.Id, "B", dir, state, interrupt, dir == Direction.Receive ? DirRef.Default.Instance : null, new RecordTime(1, 480), 1,
            new ValueList<TransferFile>([new TransferFile(id, 0, 0, "f", 10, "a/b", null, FileState.Waiting)]));
        var ids = Enumerable.Range(1, 6).Select(Tid).ToArray();
        w.A.Store.Save(Make(ids[0], TaskState.Running));
        w.A.Store.Save(Make(ids[1], TaskState.Resuming, Direction.Receive));
        w.A.Store.Save(Make(ids[2], TaskState.Offering));
        w.A.Store.Save(Make(ids[3], TaskState.AwaitingUser, Direction.Receive));
        w.A.Store.Save(Make(ids[4], TaskState.Interrupted, interrupt: new InterruptInfo(InterruptReason.PeerUnresponsive)));
        w.A.Store.Save(Make(ids[5], TaskState.Cancelled));
        await w.A.Engine.RestoreOnStartupAsync();
        Assert.Equal(new InterruptInfo(InterruptReason.AppKilled), w.A.Store.Get(ids[0])!.Interrupt);
        Assert.Equal(TaskState.Interrupted, w.A.Store.Get(ids[1])!.State);
        Assert.Equal(TaskState.ConnectionLost, w.A.Store.Get(ids[2])!.State);
        Assert.Equal(TaskState.ConnectionLost, w.A.Store.Get(ids[3])!.State);
        Assert.Equal(new InterruptInfo(InterruptReason.PeerUnresponsive), w.A.Store.Get(ids[4])!.Interrupt);
        Assert.Equal(TaskState.Cancelled, w.A.Store.Get(ids[5])!.State);
        Assert.Equal(3, w.A.Engine.UnfinishedCount.Value);
        Assert.Null(w.A.State(ids[5])); // 已结束的任务不载入
    }

    [Fact]
    public async Task TempFileJanitor_DeletesOver24hNotActive_Exactly24hKept_RunsHourlyAfterStartup()
    {
        using var w = new XferWorld();
        var s = w.A.Storage;
        var now = w.Clock.WallMs();
        var old = s.CreateTemp(DirRef.Default.Instance, Tid(1), 0);
        var edge = s.CreateTemp(DirRef.Default.Instance, Tid(2), 0);
        var fresh = s.CreateTemp(DirRef.Default.Instance, Tid(3), 0);
        s.Temp(old)!.LastWriteUtcMs = now - TempFileSelector.MaxAgeMs - 1;
        s.Temp(edge)!.LastWriteUtcMs = now - TempFileSelector.MaxAgeMs;
        s.Temp(fresh)!.LastWriteUtcMs = now - (23 * 3_600_000L);
        await w.A.Engine.RestoreOnStartupAsync();
        Assert.Equal([edge, fresh], s.TempRefs);

        // 没有连接，可以一步推进
        w.Clock.AdvanceBy(3_600_000L - 1);
        await Kit.Settle();
        Assert.Equal([edge, fresh], s.TempRefs);
        w.Clock.AdvanceBy(1);
        await Kit.WaitUntilAsync(() => s.TempRefs.SequenceEqual([fresh]), "第 1 小时的清理");
        w.Clock.AdvanceBy(3_600_000L);
        await Kit.WaitUntilAsync(() => s.TempRefs.Count == 0, "第 2 小时的清理");
    }

    [Fact]
    public async Task TempOfActiveTask_NotCleaned()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("big", Big, gate: gate)]));
        await w.B.AwaitDataAsync(id);
        var temp = Assert.Single(w.B.Storage.TempRefs);
        w.B.Storage.Temp(temp)!.LastWriteUtcMs = 0;
        await w.B.Engine.RunJanitorAsync();
        Assert.Equal([temp], w.B.Storage.TempRefs);
    }

    [Fact]
    public async Task TxtCache_DeletedWhenSendEnds_StartupDeletesUnreferenced()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        var r = Assert.IsType<WriteResult<SourceRef>.Ok>(w.A.Cache.Write("aa", "文字_1.txt", [1, 2, 3])).Value;
        var stale = Assert.IsType<WriteResult<SourceRef>.Ok>(w.A.Cache.Write("bb", "文字_2.txt", [4])).Value;
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [r]));
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        Assert.False(w.A.Cache.Owns(r));
        Assert.True(w.A.Cache.Owns(stale));
        await w.A.Engine.RestoreOnStartupAsync();
        Assert.Empty(w.A.Cache.List());
    }

    // ---- 放弃与解除配对（20.5、20.10、Q7） ----

    [Fact]
    public async Task AbandonAll_AwaitingRejected_RunningCancelled_TasksEmpty_UnfinishedZero()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        var waiting = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("x", 10)]));
        await w.B.AwaitStateAsync(waiting, TaskState.AwaitingUser);
        w.B.D.Trust.SetAutoAccept(w.A.D.Id, true);
        var running = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("big", Big, gate: gate)]));
        await w.B.AwaitDataAsync(running);
        Assert.Equal(2, w.B.Engine.UnfinishedCount.Value);
        await w.B.Engine.AbandonAllAsync();
        Assert.Empty(w.B.Engine.Tasks.Value);
        Assert.Equal(0, w.B.Engine.UnfinishedCount.Value);
        Assert.Equal(TaskState.Rejected, w.B.Store.Get(waiting)!.State);
        Assert.Equal(TaskState.Cancelled, w.B.Store.Get(running)!.State);
        await w.A.AwaitStateAsync(waiting, TaskState.Rejected);
        await w.A.AwaitStateAsync(running, TaskState.PeerCancelled);
        await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件");
    }

    [Fact]
    public async Task AbandonInterrupted_CancelledAndPendingCancelSaved()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = await w.InterruptedTransferAsync(gate);
        await w.B.Engine.AbandonAsync(id);
        Assert.Equal(TaskState.Cancelled, w.B.State(id));
        Assert.Equal([id], w.B.Db.PendingCancels().Select(p => p.TransferId).ToArray());
    }

    [Fact]
    public async Task CancelPeer_AllUnfinishedCancelledLocally_PeerGetsUnpairedCancel()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("big", Big, gate: gate)]));
        await w.B.AwaitDataAsync(id);
        await w.A.Engine.CancelPeerAsync(w.B.D.Id);
        Assert.Equal(TaskState.Cancelled, w.A.State(id));
        await w.B.AwaitStateAsync(id, TaskState.PeerCancelled);
        Assert.Equal(TaskState.Cancelled, w.A.Store.Get(id)!.State);
    }
}
