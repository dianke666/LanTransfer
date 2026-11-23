using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;
using static LanTransfer.Engine.Tests.XferKit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// FileTransferEngine 的单元测试（任务 18.5–18.8，与 Kotlin 侧 FileTransferEngineTest 对应）。C# 的传输在线程池上、真实时间里进行：
/// 测试等待可观察的结果；需要“传输进行中”时用 ReadGate 让发送方停在 PauseAt 处；计时器用 TextKit.StepToAsync 小步推进。
/// </summary>
public sealed class FileTransferEngineTests
{
    // ---- 收发主流程（14、15、16） ----

    [Fact]
    public async Task AcceptedOffer_TransfersFilesInOrder_IncludingEmptyAndMultiChunk_ContentIdentical()
    {
        using var w = new XferWorld();
        SourceRef[] refs = [w.A.File("empty.bin", 0), w.A.File("small.txt", 1000), w.A.File("big.dat", 700_000)];
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, refs));
        Assert.Equal(TaskState.Offering, w.A.State(id));
        await w.B.AwaitStateAsync(id, TaskState.AwaitingUser);
        Assert.Empty(w.B.Received());
        w.B.Engine.Accept(id);
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        var got = w.B.Received();
        Assert.Equal(new HashSet<string> { "empty.bin", "small.txt", "big.dat" }, got.Keys.ToHashSet());
        foreach (var r in refs)
        {
            Assert.Equal(w.A.Content(r), got[w.A.Sources.EntryOf(r)!.Name]);
        }

        Assert.All(w.A.TaskOf(id).Files, f => Assert.Equal(FileState.Completed, f.State));
        Assert.All(w.B.TaskOf(id).Files, f => Assert.Equal(FileState.Completed, f.State));
        Assert.Equal([0L, 1000L, 700_000L], w.A.TaskOf(id).Files.Select(f => f.Persisted).ToArray());
        Assert.Empty(w.B.Storage.TempRefs);
        Assert.Equal(100, w.A.View(id)!.Progress.Percent);
        Assert.Equal(TaskState.Completed, w.B.Store.Get(id)!.State);
        Assert.Equal(0, w.A.Engine.UnfinishedCount.Value);
    }

    [Fact]
    public async Task AutoAccept_ReceivesWithoutConfirmation()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("x", 5000)]));
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(w.A.Content(w.A.TaskOf(id).Files[0].SourceRef!.Value), w.B.Received()["x"]);
    }

    [Fact]
    public async Task Reject_BothRejected_NoFilesCreated()
    {
        using var w = new XferWorld();
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("x", 10)]));
        await w.B.AwaitStateAsync(id, TaskState.AwaitingUser);
        w.B.Engine.Reject(id);
        await w.A.AwaitStateAsync(id, TaskState.Rejected);
        Assert.Equal(TaskState.Rejected, w.B.State(id));
        Assert.Empty(w.B.Received());
        Assert.Empty(w.B.Storage.TempRefs);
    }

    [Fact]
    public async Task ReceiverNoResponse_StillWaitingAt59999ms_BothNoResponseAt60000ms()
    {
        using var w = new XferWorld();
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("x", 10)]));
        await w.B.AwaitStateAsync(id, TaskState.AwaitingUser);
        var t0 = w.Clock.MonotonicMs();
        await TextKit.StepToAsync(w.Clock, t0 + 59_999);
        await Kit.Settle();
        Assert.Equal(TaskState.AwaitingUser, w.B.State(id));
        await TextKit.StepToAsync(w.Clock, t0 + 60_000);
        await w.B.AwaitStateAsync(id, TaskState.NoResponse);
        await w.A.AwaitStateAsync(id, TaskState.NoResponse);
    }

    [Fact]
    public async Task SenderFallback_PeerNeverReplies_NoResponseAt65000ms()
    {
        using var w = new XferWorld(bDispatch: false);
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("x", 10)]));
        var t1 = w.Clock.MonotonicMs();
        await TextKit.StepToAsync(w.Clock, t1 + 64_999);
        await Kit.Settle();
        Assert.Equal(TaskState.Offering, w.A.State(id));
        await TextKit.StepToAsync(w.Clock, t1 + 65_000);
        await w.A.AwaitStateAsync(id, TaskState.NoResponse);
    }

    [Fact]
    public async Task Precheck_NoSpace_And_CannotSave()
    {
        using var w = new XferWorld();
        w.B.Storage.Dir(DirRef.Default.Instance).FreeSpace = 99;
        var id1 = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("x", 100)]));
        await w.A.AwaitStateAsync(id1, TaskState.NoSpace);
        Assert.Equal(TaskState.NoSpace, w.B.State(id1));
        w.B.Storage.Dir(DirRef.Default.Instance).FreeSpace = null;
        w.B.Storage.Dir(DirRef.Default.Instance).Creatable = false;
        var id2 = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("y", 100)]));
        await w.A.AwaitStateAsync(id2, TaskState.CannotSave);
        Assert.Equal(TaskState.CannotSave, w.B.State(id2));
    }

    [Fact]
    public async Task MoreThan1000Files_Refused_Exactly1000Accepted()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        var many = Enumerable.Range(0, 1001).Select(i => w.A.File($"f{i}", 0)).ToList();
        Assert.Equal(new SendOfferResult.TooMany(), await w.A.Engine.SendAsync(w.B.D.Id, many));
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, many.Take(1000).ToList()));
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(1000, w.B.Received().Count);
    }

    [Fact]
    public async Task FolderOrUnreadable_NoOffer_ConnectFailure_KeepsList()
    {
        using var w = new XferWorld();
        var dir = new SourceRef("src:dir");
        w.A.Sources.PutDirectory(dir, "folder");
        Assert.Equal(new SendOfferResult.Unreadable(new ValueList<SourceRef>([dir])), await w.A.Engine.SendAsync(w.B.D.Id, [dir]));
        Assert.Equal(
            new SendOfferResult.ConnectFailed(new OpenFailure.Unreachable()),
            await Kit.Within(w.A.Engine.SendAsync(Kit.DeviceIdOf(9), [w.A.File("x", 1)])));
        Assert.Empty(w.A.Engine.Tasks.Value);
    }

    // ---- 取消、失败与中断（14.7、17.2、17.3、17.6、18.2、18.3、18.7） ----

    [Fact]
    public async Task SenderCancelsMidTransfer_LocalCancelled_PeerCancelled_TempDeleted()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("big", Big, gate: gate)]));
        await w.B.AwaitDataAsync(id);
        w.A.Engine.Cancel(id);
        await w.A.AwaitStateAsync(id, TaskState.Cancelled);
        await w.B.AwaitStateAsync(id, TaskState.PeerCancelled);
        await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件");
        Assert.Empty(w.B.Received());
    }

    [Fact]
    public async Task ReceiverCancels_PeerCancelled_TempDeleted_CompletedFileKept()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File("done", 10), w.A.File("big", Big, gate: gate)]));
        await w.B.AwaitDataAsync(id, 1);
        w.B.Engine.Cancel(id);
        await w.B.AwaitStateAsync(id, TaskState.Cancelled);
        await w.A.AwaitStateAsync(id, TaskState.PeerCancelled);
        await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "5 s 内删除临时文件");
        Assert.Equal(["done"], w.B.Received().Keys.ToArray());
    }

    [Fact]
    public async Task SourceReadFails_FileReadFailed_NextFileContinues_TaskCompletes()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        var bad = w.A.File("bad", 500_000);
        w.A.Sources.EntryOf(bad)!.FailAt = 200_000;
        var good = w.A.File("good", 1000);
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [bad, good]));
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal([FileState.ReadFailed, FileState.Completed], w.A.TaskOf(id).Files.Select(f => f.State).ToArray());
        Assert.Equal([FileState.ReadFailed, FileState.Completed], w.B.TaskOf(id).Files.Select(f => f.State).ToArray());
        Assert.Equal(["good"], w.B.Received().Keys.ToArray());
        await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件");
    }

    [Fact]
    public async Task ContentDiffersFromOffer_ChecksumFailed_TempDeleted_NextFileContinues()
    {
        using var w = new XferWorld();
        var longer = w.A.File("longer", 1000);
        var shorter = w.A.File("shorter", 1000);
        var ok = w.A.File("ok", 10);
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [longer, shorter, ok]));
        w.A.Sources.Modify(longer, Bytes.Of(Enumerable.Repeat((byte)1, 1500).ToArray()), 1);
        w.A.Sources.Modify(shorter, Bytes.Of(Enumerable.Repeat((byte)2, 10).ToArray()), 1);
        await w.B.AwaitStateAsync(id, TaskState.AwaitingUser);
        w.B.Engine.Accept(id);
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        FileState[] expected = [FileState.ChecksumFailed, FileState.ChecksumFailed, FileState.Completed];
        Assert.Equal(expected, w.A.TaskOf(id).Files.Select(f => f.State).ToArray());
        Assert.Equal(expected, w.B.TaskOf(id).Files.Select(f => f.State).ToArray());
        Assert.Equal(["ok"], w.B.Received().Keys.ToArray());
        await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件");
    }

    [Fact]
    public async Task Disconnect_BothInterrupted_TempKept_SenderResumesFromPersisted()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = await w.InterruptedTransferAsync(gate);
        var r = w.A.TaskOf(id).Files[0].SourceRef!.Value;
        Assert.Equal(InterruptReason.PeerClosed, w.A.TaskOf(id).Interrupt!.Reason);
        var temp = Assert.Single(w.B.Storage.TempRefs);
        Assert.True(w.B.Storage.Length(temp) > 0);
        gate.Release();
        Assert.Equal(new ResumeResult.Started(), await Kit.Within(w.A.Engine.ResumeAsync(id)));
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(w.A.Content(r), w.B.Received()["big"]);
    }

    [Fact]
    public async Task ReceiverResumes_Too_ConnectFailureKeepsInterrupted()
    {
        using var w = new XferWorld();
        using var gate = new ReadGate();
        w.AutoAccept();
        var id = await w.InterruptedTransferAsync(gate);
        var r = w.A.TaskOf(id).Files[0].SourceRef!.Value;

        // B 不知道 A 的地址（A 没有监听）：无法连接
        Assert.IsType<ResumeResult.ConnectFailed>(await Kit.Within(w.B.Engine.ResumeAsync(id)));
        Assert.Equal(TaskState.Interrupted, w.B.State(id));
        Assert.Single(w.B.Storage.TempRefs);
        w.A.D.Listen();
        w.B.D.Knows(w.A.D);
        gate.Release();
        Assert.Equal(new ResumeResult.Started(), await Kit.Within(w.B.Engine.ResumeAsync(id)));
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(w.A.Content(r), w.B.Received()["big"]);
    }

    [Fact]
    public async Task ReceiverWriteFails_BothInterruptedWriteFailed_TempKept_ResumeCompletes()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        var fail = true;
        w.B.Storage.WriteFailure = (_, len) => Volatile.Read(ref fail) && len >= 300_000 ? "磁盘已满" : null;
        var r = w.A.File("big", 1_000_000);
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [r]));
        await w.B.AwaitStateAsync(id, TaskState.Interrupted);
        await w.A.AwaitStateAsync(id, TaskState.Interrupted);
        Assert.Equal(new InterruptInfo(InterruptReason.WriteFailed, "磁盘已满"), w.A.TaskOf(id).Interrupt);
        Assert.Equal(FileState.WriteFailed, w.B.TaskOf(id).Files[0].State);
        Assert.Single(w.B.Storage.TempRefs);
        Volatile.Write(ref fail, false);
        Assert.Equal(new ResumeResult.Started(), await Kit.Within(w.A.Engine.ResumeAsync(id)));
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(w.A.Content(r), w.B.Received()["big"]);
    }

    // ---- E3：未知任务与非法 Offer ----

    private static async Task<Session> RawSessionToBAsync(XferWorld w)
    {
        var raw = new TestDevice("R", 5, Kit.Iface("wlan0", 192, 168, 1, 9), w.Net, w.Clock);
        raw.Trusts(w.B.D);
        w.B.D.Trusts(raw);
        return Assert.IsType<OpenResult.Ok>(await Kit.Within(raw.Manager.OpenAsync(new Target.Device(w.B.D.Id)))).Session;
    }

    [Fact]
    public async Task UnknownTransfer_ChunkGetsUnknownTransferCancel_CancelGetsNoReply()
    {
        using var w = new XferWorld();
        var s = await RawSessionToBAsync(w);
        var tid = Tid(7);
        s.Send(new TransferCancel(tid, TransferCancelReason.User));
        s.Send(new FileChunk(tid, 0, 0, Bytes.Of([1])));
        Assert.Equal(new TransferCancel(tid, TransferCancelReason.UnknownTransfer), await TextKit.NextAsync(s));
        await Kit.Settle();
        Assert.False(s.Incoming.TryRead(out _));
    }

    [Fact]
    public async Task OfferWithFileOver1TiB_InvalidOffer_NoTask_Exactly1TiBAccepted()
    {
        using var w = new XferWorld();
        var s = await RawSessionToBAsync(w);
        var big = Tid(1);
        s.Send(new FileOffer(big, new ValueList<FileEntry>([new FileEntry("x", ProtocolConstants.MaxFileSize + 1, "a/b")])));
        Assert.Equal(new FileOfferReply(big, OfferDecision.InvalidOffer), await TextKit.NextAsync(s));
        Assert.Null(w.B.View(big));
        Assert.Null(w.B.Store.Get(big));
        var max = Tid(2);
        s.Send(new FileOffer(max, new ValueList<FileEntry>([new FileEntry("x", ProtocolConstants.MaxFileSize, "a/b")])));
        await w.B.AwaitStateAsync(max, TaskState.AwaitingUser);
    }
}
