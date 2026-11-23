using System.Text;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>LanTransfer.Testing 中引擎端口内存实现的自检（与 Kotlin 侧 EngineFixturesTest 对应）。</summary>
public sealed class EngineFixturesTests
{
    private static readonly LocalInterface IfA = Iface("wlan0", 192, 168, 1, 2);
    private static readonly LocalInterface IfB = Iface("eth0", 192, 168, 1, 3);
    private static readonly LocalInterface IfC = Iface("eth0", 10, 0, 0, 5, 8);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LocalInterface Iface(string name, byte a, byte b, byte c, byte d, int prefix = 24) =>
        new(name, IfaceKind.Wifi, Ipv4.Of(a, b, c, d), prefix);

    private static Bytes16 Id16(int b) => Bytes16.Of(Enumerable.Repeat((byte)b, 16).ToArray());

    private static async Task<byte[]> ReadAllAsync(ITcpConn conn, int n)
    {
        var buf = new byte[n];
        var got = 0;
        while (got < n)
        {
            var k = await conn.ReadAsync(buf.AsMemory(got), Ct);
            if (k < 0)
            {
                break;
            }

            got += k;
        }

        return buf[..got];
    }

    private static IUdpEndpoint Endpoint(UdpOpenResult r) => Assert.IsType<UdpOpenResult.Ok>(r).Endpoint;

    private static ITcpConn Conn(ConnectResult r) => Assert.IsType<ConnectResult.Ok>(r).Conn;

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // ---- InMemoryTransport：TCP ----

    [Fact]
    public async Task Tcp_pair_transfers_both_ways_captures_bytes_and_reads_eof_after_close()
    {
        var net = new InMemoryNetwork();
        var a = net.Host("A", [IfA]);
        var b = net.Host("B", [IfB]);
        var listener = b.Listen([42580])!;
        Assert.Equal(42580, listener.Port);
        var accepted = listener.AcceptAsync(Ct).AsTask();
        var client = Conn(await a.ConnectAsync(IfA, IfB.Address, 42580, TimeSpan.FromSeconds(10), Ct));
        var server = (await accepted)!;
        Assert.Equal(new SocketAddr(IfB.Address, 42580), client.Remote);
        Assert.Equal(client.Local, server.Remote);
        Assert.True(await client.WriteAsync(Utf8("hello"), Ct));
        Assert.Equal("hello", Encoding.UTF8.GetString(await ReadAllAsync(server, 5)));
        Assert.True(await server.WriteAsync(new byte[] { 1, 2, 3 }, Ct));
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadAllAsync(client, 3));
        var link = Assert.Single(net.Connections);
        Assert.Equal("hello", Encoding.UTF8.GetString(link.BytesFromClient()));
        Assert.Equal(new byte[] { 1, 2, 3 }, link.BytesFromServer());

        Assert.True(await client.WriteAsync(Utf8("bye"), Ct));
        client.Close();
        Assert.Equal("bye", Encoding.UTF8.GetString(await ReadAllAsync(server, 3))); // 先收完已发出的数据
        Assert.Equal(-1, await server.ReadAsync(new byte[4], Ct));
        Assert.False(await server.WriteAsync(new byte[] { 9 }, Ct));
        Assert.False(await client.WriteAsync(new byte[] { 9 }, Ct));
        Assert.False(link.IsOpen);
    }

    [Fact]
    public async Task Connect_failures_are_reported_and_every_attempt_is_recorded()
    {
        var clock = new FakeClock();
        var net = new InMemoryNetwork(clock);
        var a = net.Host("A", [IfA]);
        net.Host("C", [IfC]);
        var timeout = TimeSpan.FromSeconds(10);
        Assert.Equal(new ConnectResult.Unreachable(), await a.ConnectAsync(IfA, IfB.Address, 42580, timeout, Ct)); // 没有主机
        net.Host("B", [IfB]);
        Assert.Equal(new ConnectResult.Refused(), await a.ConnectAsync(IfA, IfB.Address, 42580, timeout, Ct)); // 没有监听
        Assert.Equal(new ConnectResult.Unreachable(), await a.ConnectAsync(IfA, IfC.Address, 42580, timeout, Ct)); // 不在发送接口的子网内
        Assert.Equal(new ConnectResult.BindFailed(VpnSuspected: false), await a.ConnectAsync(IfB, IfB.Address, 42580, timeout, Ct)); // 接口不在本机

        a.ConnectBehavior = t => t.Port == 1 ? new ConnectBehavior.Hang() : new ConnectBehavior.BindFail(VpnSuspected: true);
        var hanging = a.ConnectAsync(IfA, IfB.Address, 1, TimeSpan.FromSeconds(5), Ct);
        clock.AdvanceBy(4_999);
        Assert.False(hanging.IsCompleted);
        clock.AdvanceBy(1);
        Assert.Equal(new ConnectResult.Timeout(), await hanging);
        Assert.Equal(5_000, clock.MonotonicMs());
        Assert.Equal(new ConnectResult.BindFailed(VpnSuspected: true), await a.ConnectAsync(IfA, IfB.Address, 2, timeout, Ct));
        Assert.Equal(new[] { 42580, 42580, 42580, 42580, 1, 2 }, a.ConnectAttempts.Select(x => x.Target.Port));
    }

    [Fact]
    public async Task Listen_falls_back_to_next_port_and_system_port_and_accept_returns_null_after_close()
    {
        var net = new InMemoryNetwork();
        var b = net.Host("B", [IfB]);
        b.BlockedTcpPorts.Add(42580);
        var l1 = b.Listen([42580, 42590])!;
        Assert.Equal(42590, l1.Port);
        Assert.Null(b.Listen([42590])); // 已被自己占用
        var l2 = b.Listen([0])!;
        Assert.True(l2.Port >= 50_000);
        Assert.True(b.ListeningPorts.SetEquals([42590, l2.Port]));
        l1.Close();
        Assert.Null(await l1.AcceptAsync(Ct));
        Assert.True(b.ListeningPorts.SetEquals([l2.Port]));
    }

    [Fact]
    public async Task Reset_makes_both_ends_read_eof_and_blackhole_drops_data_without_eof()
    {
        var net = new InMemoryNetwork();
        var a = net.Host("A", [IfA]);
        var b = net.Host("B", [IfB]);
        var listener = b.Listen([42580])!;
        var c1 = Conn(await a.ConnectAsync(IfA, IfB.Address, 42580, TimeSpan.FromSeconds(1), Ct));
        var s1 = (await listener.AcceptAsync(Ct))!;
        Assert.True(await c1.WriteAsync(new byte[] { 1 }, Ct));
        net.Connections[0].Reset();
        Assert.Equal(-1, await s1.ReadAsync(new byte[4], Ct));
        Assert.Equal(-1, await c1.ReadAsync(new byte[4], Ct));
        Assert.False(await c1.WriteAsync(new byte[] { 2 }, Ct));

        // 已在等待中的读取也会被 RST 唤醒
        var c2 = Conn(await a.ConnectAsync(IfA, IfB.Address, 42580, TimeSpan.FromSeconds(1), Ct));
        var s2 = (await listener.AcceptAsync(Ct))!;
        var waiting = s2.ReadAsync(new byte[4], Ct).AsTask();
        net.Connections[1].Reset();
        Assert.Equal(-1, await waiting);

        var c3 = Conn(await a.ConnectAsync(IfA, IfB.Address, 42580, TimeSpan.FromSeconds(1), Ct));
        var s3 = (await listener.AcceptAsync(Ct))!;
        net.Connections[2].Blackhole();
        Assert.True(await c3.WriteAsync(new byte[] { 7 }, Ct));
        var silent = s3.ReadAsync(new byte[4], Ct).AsTask();
        await Task.Delay(100, Ct);
        Assert.False(silent.IsCompleted); // 收不到数据也收不到 EOF
        Assert.Equal(new byte[] { 7 }, net.Connections[2].BytesFromClient());
        c3.Close();
        s3.Close();
        Assert.Equal(-1, await silent);
    }

    // ---- InMemoryTransport：UDP ----

    [Fact]
    public async Task Udp_broadcast_and_multicast_reach_same_subnet_including_loopback_and_unicast_reaches_target_only()
    {
        var net = new InMemoryNetwork();
        var a = net.Host("A", [IfA]);
        var b = net.Host("B", [IfB], reportUdpVia: false);
        var c = net.Host("C", [IfC]);
        var ea = Endpoint(a.OpenUdp(42581));
        var eb = Endpoint(b.OpenUdp(42581));
        var ec = Endpoint(c.OpenUdp(42581));
        Assert.True(await ea.SendAsync(IfA, IfA.Broadcast, 42581, new byte[] { 1 }, Ct));
        var atB = (await eb.ReceiveAsync(Ct))!;
        Assert.Equal(Bytes.Of([1]), atB.Data);
        Assert.Equal(IfA.Address, atB.Src);
        Assert.Equal(42581, atB.SrcPort);
        Assert.Null(atB.Via); // Android 不报告接收接口
        Assert.Equal(IfA, (await ea.ReceiveAsync(Ct))!.Via); // 回环
        Assert.True(await ea.SendAsync(IfA, InMemoryNetwork.MulticastGroup, 42581, new byte[] { 2 }, Ct));
        Assert.Equal(Bytes.Of([2]), (await eb.ReceiveAsync(Ct))!.Data);
        Assert.True(await ea.SendAsync(IfA, IfB.Address, 42581, new byte[] { 3 }, Ct));
        Assert.Equal(Bytes.Of([3]), (await eb.ReceiveAsync(Ct))!.Data);
        var atC = ec.ReceiveAsync(Ct).AsTask();
        await Task.Delay(100, Ct);
        Assert.False(atC.IsCompleted);
        ec.Close();
        Assert.Null(await atC);
        Assert.Equal(new[] { IfA.Broadcast, InMemoryNetwork.MulticastGroup, IfB.Address }, net.Datagrams.Select(d => d.Dst));
    }

    [Fact]
    public async Task Udp_open_fails_on_busy_port_send_failure_can_be_injected_and_receive_returns_null_after_close()
    {
        var net = new InMemoryNetwork();
        var a = net.Host("A", [IfA]);
        a.BlockedUdpPorts.Add(42581);
        Assert.IsType<UdpOpenResult.Failed>(a.OpenUdp(42581));
        var e = Endpoint(a.OpenUdp(42582));
        Assert.IsType<UdpOpenResult.Failed>(a.OpenUdp(42582));
        a.UdpSendFails = true;
        Assert.False(await e.SendAsync(IfA, IfA.Broadcast, 42582, new byte[] { 1 }, Ct));
        e.Close();
        Assert.Null(await e.ReceiveAsync(Ct));
        Assert.IsType<UdpOpenResult.Ok>(a.OpenUdp(42582)); // 关闭后端口释放
    }

    [Fact]
    public void FakeNetworkMonitor_changes_interfaces_and_records_fast_polling()
    {
        var m = new FakeNetworkMonitor([IfA]);
        var seen = new List<int>();
        using var sub = m.Interfaces.Subscribe(v => seen.Add(v.Count));
        m.Set([IfA, IfB]);
        m.Set([IfA, IfB]); // 相同的集合不再推送
        Assert.Equal(new ValueList<LocalInterface>([IfA, IfB]), m.Interfaces.Value);
        Assert.Equal(new[] { 1, 2 }, seen);
        m.SetFastPolling(true);
        m.SetFastPolling(false);
        Assert.Equal(new[] { true, false }, m.FastPollingHistory);
        Assert.False(m.FastPolling);
    }

    // ---- FakeClock ----

    [Fact]
    public async Task FakeClock_drives_TimeProvider_timers_and_wall_skew_does_not_affect_monotonic_time()
    {
        var clock = new FakeClock();
        Assert.Equal(0, clock.MonotonicMs());
        var delay = Task.Delay(TimeSpan.FromMilliseconds(1_500), clock.Time, Ct);
        clock.AdvanceBy(1_499);
        Assert.False(delay.IsCompleted);
        clock.AdvanceBy(1);
        await delay;
        Assert.Equal(1_500, clock.MonotonicMs());
        Assert.Equal(FakeClock.DefaultWallStartMs + 1_500, clock.WallMs());
        Assert.Equal(clock.WallMs(), clock.GetUtcNow().ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), clock.GetElapsedTime(0));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock);
        clock.AdvanceBy(1_999);
        Assert.False(cts.IsCancellationRequested);
        clock.AdvanceBy(1);
        Assert.True(cts.IsCancellationRequested);

        using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        var tick = periodic.WaitForNextTickAsync(Ct).AsTask();
        clock.AdvanceBy(1_000);
        Assert.True(await tick);
        Assert.Equal(1, clock.PendingTimers); // 周期定时器仍在
        periodic.Dispose();
        Assert.Equal(0, clock.PendingTimers);

        clock.WallSkewMs = -3_600_000;
        Assert.Equal(4_500, clock.MonotonicMs());
        Assert.Equal(FakeClock.DefaultWallStartMs + 4_500 - 3_600_000, clock.WallMs());

        var manual = new FakeClock();
        manual.AdvanceBy(10);
        Assert.Equal(10, manual.MonotonicMs());
        Assert.Equal(new DateTime(2025, 10, 10, 0, 53, 20, 10, DateTimeKind.Unspecified), manual.LocalNow()); // UTC+8
    }

    [Fact]
    public void FakeClock_fires_timers_in_due_order_including_ones_created_by_callbacks()
    {
        var clock = new FakeClock();
        var fired = new List<string>();
        using var t2 = clock.CreateTimer(_ => fired.Add("t2@" + clock.MonotonicMs()), null, TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
        ITimer? nested = null;
        using var t1 = clock.CreateTimer(
            state =>
            {
                fired.Add("t1@" + clock.MonotonicMs());
                nested = clock.CreateTimer(_ => fired.Add("n@" + clock.MonotonicMs()), null, TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
            },
            null,
            TimeSpan.FromMilliseconds(100),
            Timeout.InfiniteTimeSpan);
        using var zero = clock.CreateTimer(_ => fired.Add("zero@" + clock.MonotonicMs()), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        clock.AdvanceBy(1_000);
        nested?.Dispose();
        Assert.Equal(new[] { "zero@0", "t1@100", "n@150", "t2@200" }, fired);
        Assert.Equal(1_000, clock.MonotonicMs());
    }

    // ---- 内存版存储 ----

    private static readonly DeviceId Peer = new(Id16(1));
    private static readonly TransferId Tid = new(Id16(2));

    private static TextRecord TextOf(int m, long utc) =>
        new(0, Peer, "对方", new MessageId(Id16(m)), Direction.Receive, Bytes.Of(Utf8("t" + m)), utc, new RecordTime(utc, 480), TextStatus.Received);

    private static TransferFile FileOf(int i) => new(Tid, i, 0, "f" + i, 10, "text/plain", null, FileState.Waiting);

    private static TransferTask TaskOf(TaskState state, params int[] idx) =>
        new(Tid, Peer, "对方", Direction.Send, state, null, null, new RecordTime(1_500, 480), 0, new ValueList<TransferFile>(idx.Select(FileOf)));

    private static ValueList<HistoryRecord> Items(ListResult<HistoryRecord> r) => Assert.IsType<ListResult<HistoryRecord>.Ok>(r).Items;

    [Fact]
    public void HistoryStore_dedups_shares_seq_sorts_deletes_and_clear_keeps_pending_cancel()
    {
        var db = new InMemoryDatabase();
        var history = new InMemoryHistoryStore(db);
        var tasks = new InMemoryTaskStore(db);
        Assert.Equal(new StoreResult.Ok(), history.AppendText(TextOf(10, 2_000)));
        Assert.Equal(new StoreResult.Duplicate(), history.AppendText(TextOf(10, 9_999)));
        Assert.Equal(new StoreResult.Ok(), tasks.Save(TaskOf(TaskState.Running, 0, 1)));
        Assert.Equal(new StoreResult.Ok(), history.AppendText(TextOf(11, 1_500)));
        var listed = Items(history.List());
        Assert.Equal(new long[] { 2, 3, 4, 1 }, listed.Select(r => r.Seq)); // 1 500 ms 的两个文件、1 500 ms 的文字、2 000 ms 的文字
        Assert.Equal(TaskState.Running, Assert.IsType<HistoryRecord.File>(listed[0]).Record.TaskState);

        // 重新保存保留 seq；新增文件分配新 seq
        Assert.Equal(new StoreResult.Ok(), tasks.Save(TaskOf(TaskState.Interrupted, 0, 1, 2)));
        Assert.Equal(new long[] { 2, 3, 5 }, tasks.Get(Tid)!.Files.Select(f => f.Seq));
        Assert.Single(tasks.Unfinished());

        Assert.Equal(new StoreResult.Ok(), history.UpdateTextStatus(new MessageId(Id16(10)), Peer, TextStatus.Delivered));
        Assert.Equal(TextStatus.Delivered, history.FindText(Peer, new MessageId(Id16(10)))!.Status);
        Assert.Equal(new StoreResult.Ok(), history.Delete(new RecordId(1)));
        Assert.Null(history.FindText(Peer, new MessageId(Id16(10))));

        history.DeleteFileRecord(Tid, 0);
        history.DeleteFileRecord(Tid, 1);
        Assert.Equal(new[] { 2 }, tasks.Get(Tid)!.Files.Select(f => f.Index));
        history.DeleteFileRecord(Tid, 2);
        Assert.Null(tasks.Get(Tid)); // 没有剩余文件行时任务行也被删除

        Assert.Equal(new StoreResult.Ok(), tasks.AddPendingCancel(Peer, Tid));
        Assert.Equal(new StoreResult.Ok(), tasks.AddPendingCancel(Peer, Tid)); // 主键 (peer, transfer_id)
        Assert.Equal(
            new StoreResult.Ok(),
            history.UpsertFile(new FileRecord(FileOf(0), Peer, "对方", Direction.Receive, new RecordTime(3_000, 0), TaskState.Completed)));
        Assert.Equal(new StoreResult.Ok(), history.ClearAll());
        Assert.Empty(Items(history.List()));
        history.UpdateTextStatus(new MessageId(Id16(11)), Peer, TextStatus.Failed); // 迟到的更新不重新创建记录
        Assert.Empty(Items(history.List()));
        Assert.Single(db.PendingCancels());
        Assert.Equal(new[] { Tid }, tasks.TakePendingCancels(Peer));
        Assert.Empty(tasks.TakePendingCancels(Peer));
        Assert.Equal(new StoreResult.Ok(), history.AppendText(TextOf(12, 1)));
        Assert.Equal(7, history.FindText(Peer, new MessageId(Id16(12)))!.Seq); // 清空不重置计数器

        db.FailWrites = op => op == "appendText";
        Assert.IsType<StoreResult.Failed>(history.AppendText(TextOf(13, 1)));
        Assert.Null(history.FindText(Peer, new MessageId(Id16(13))));
        db.FailReads = true;
        Assert.IsType<ListResult<HistoryRecord>.Failed>(history.List());
    }

    [Fact]
    public async Task TrustStore_replaces_deletes_updates_and_emits_changes()
    {
        var trust = new InMemoryTrustStore();
        var d = new TrustedDevice(Peer, "Pixel", DeviceType.Android, Bytes.Of(new byte[32]), AutoAccept: true, PairedAtUtcMs: 1);
        var changed = trust.Changes.FirstAsync(Ct);
        Assert.Equal(new StoreResult.Ok(), trust.Upsert(d));
        await changed;
        Assert.Equal(new StoreResult.Ok(), trust.Upsert(d with { DisplayName = "新名称" }));
        Assert.Equal(new[] { d with { DisplayName = "新名称" } }, trust.All());
        trust.SetAutoAccept(Peer, false);
        trust.UpdateName(Peer, "改名");
        trust.RecordAddress(Peer, Ipv4.Of(192, 168, 1, 9), 42590);
        Assert.Equal(d with { DisplayName = "改名", AutoAccept = false, LastAddress = Ipv4.Of(192, 168, 1, 9), LastPort = 42590 }, trust.Get(Peer));
        trust.Delete(Peer);
        Assert.Null(trust.Get(Peer));
        Assert.Equal(new StoreResult.Ok(), trust.SetAutoAccept(Peer, true)); // 不存在时不做任何事
        Assert.Empty(trust.All());
        trust.FailWrites = op => op == "upsert";
        Assert.IsType<StoreResult.Failed>(trust.Upsert(d));
        Assert.Empty(trust.All());
    }

    [Fact]
    public void IdentityStore_creates_then_loads_and_resets_corrupted_identity_keeping_display_name()
    {
        var store = new InMemoryIdentityStore(new SeededRng(1), "Pixel 9");
        var created = Assert.IsType<IdentityLoad.Created>(store.LoadOrCreate());
        Assert.Equal(created.Identity.DeviceId, Assert.IsType<IdentityLoad.Loaded>(store.LoadOrCreate()).Identity.DeviceId);
        Assert.Equal(new StoreResult.Ok(), store.SetDisplayName("小明的手机"));
        Assert.Equal("小明的手机", Assert.IsType<IdentityLoad.Loaded>(store.LoadOrCreate()).Identity.DisplayName);
        var broken = new InMemoryIdentityStore(new SeededRng(2), "默认", created.Identity, corrupted: true);
        var reset = Assert.IsType<IdentityLoad.ResetCorrupted>(broken.LoadOrCreate());
        Assert.NotEqual(created.Identity.DeviceId, reset.Identity.DeviceId);
        Assert.Equal("Pixel 9", reset.Identity.DisplayName);
        Assert.IsType<IdentityLoad.Loaded>(broken.LoadOrCreate());
    }

    [Fact]
    public void SettingsStore_exposes_current_value_and_changes()
    {
        var settings = new InMemorySettingsStore();
        var seen = new List<bool>();
        using var sub = settings.Changes.Subscribe(s => seen.Add(s.AutoCopy));
        Assert.Equal(new StoreResult.Ok(), settings.Update(s => s with { AutoCopy = true }));
        Assert.True(settings.Current().AutoCopy);
        Assert.True(settings.Changes.Value.AutoCopy);
        settings.FailWrites = true;
        Assert.IsType<StoreResult.Failed>(settings.Update(s => s with { AutoCopy = false }));
        Assert.True(settings.Current().AutoCopy);
        Assert.Equal(new[] { false, true }, seen);
    }

    // ---- 内存版文件 ----

    private static readonly TransferId FileTid = new(Id16(3));
    private static readonly DirRef Dir = new DirRef.Path("D:\\接收");

    [Fact]
    public void ReceiveStorage_probes_truncates_appends_reports_space_and_write_failures_and_commits_case_insensitively_on_windows()
    {
        var clock = new FakeClock();
        var storage = new InMemoryReceiveStorage(FsProfile.Windows, clock);
        Assert.Equal(DirStatus.Usable, storage.Probe(Dir)); // 不存在但可创建
        storage.Dir(DirRef.Default.Instance).Creatable = false;
        Assert.Equal(DirStatus.CannotCreate, storage.Probe(DirRef.Default.Instance));
        Assert.False(storage.EnsureDirectory(DirRef.Default.Instance));
        Assert.True(storage.EnsureDirectory(Dir));
        storage.PutFile(Dir, "A.txt", Bytes.Of([0]));

        var temp = storage.CreateTemp(Dir, FileTid, 0);
        Assert.Equal(temp, storage.CreateTemp(Dir, FileTid, 0));
        Assert.Equal(0L, storage.Length(temp));
        var w = Assert.IsType<WriteResult<ITempWriter>.Ok>(storage.OpenAppend(temp, 0)).Value;
        Assert.Equal(IoResult.Success, w.Write(Utf8("hello")));
        w.Close();
        clock.AdvanceBy(1_000);
        var w2 = Assert.IsType<WriteResult<ITempWriter>.Ok>(storage.OpenAppend(temp, 2)).Value; // 截断到 2 字节后追加
        Assert.Equal(IoResult.Success, w2.Write(Utf8("LLO")));
        Assert.Equal(IoResult.Success, w2.FlushSync());
        Assert.Equal("heLLO", Encoding.UTF8.GetString(storage.Temp(temp)!.Bytes()));
        Assert.Equal(clock.WallMs(), Assert.Single(storage.ListTemps()).LastWriteUtcMs);
        Assert.Equal(new[] { "A.txt" }, storage.FilesIn(Dir).Select(kv => kv.Key)); // 临时文件不以最终文件名出现
        Assert.IsType<WriteResult<ITempWriter>.Failed>(storage.OpenAppend(temp, 99));

        var reader = Assert.IsType<ReadResult<IChunkReader>.Ok>(storage.OpenRead(temp)).Value;
        var buf = new byte[16];
        Assert.Equal(new ReadResult<int>.Ok(5), reader.Read(buf));
        Assert.Equal(new ReadResult<int>.Ok(-1), reader.Read(buf));

        var committed = Assert.IsType<CommitResult.Ok>(storage.Commit(temp, "a.txt"));
        Assert.Equal("a (1).txt", committed.FinalName);
        Assert.Equal(Bytes.Of(Utf8("heLLO")), storage.FilesIn(Dir).Single(kv => kv.Key == "a (1).txt").Value);
        Assert.Null(storage.Length(temp));

        var t2 = storage.CreateTemp(Dir, FileTid, 1);
        storage.Dir(Dir).FreeSpace = 3;
        var w3 = Assert.IsType<WriteResult<ITempWriter>.Ok>(storage.OpenAppend(t2, 0)).Value;
        Assert.Equal(new IoResult.Failed("存储空间不足"), w3.Write(new byte[4]));
        Assert.Equal(IoResult.Success, w3.Write(new byte[3]));
        Assert.Equal(0L, storage.Dir(Dir).FreeSpace);
        storage.WriteFailure = (_, at) => at >= 3 ? "磁盘错误" : null;
        storage.Dir(Dir).FreeSpace = null;
        Assert.Equal(new IoResult.Failed("磁盘错误"), w3.Write(new byte[1]));
        storage.CommitFailure = _ => "重命名失败";
        Assert.Equal(new CommitResult.Failed("重命名失败"), storage.Commit(t2, "b.txt"));
        Assert.True(storage.Delete(t2));
        Assert.False(storage.Delete(t2));
    }

    [Fact]
    public void ReceiveStorage_name_collision_is_case_sensitive_on_android()
    {
        var storage = new InMemoryReceiveStorage(FsProfile.Android, new FakeClock());
        storage.PutFile(DirRef.Default.Instance, "A.txt", Bytes.Empty);
        var temp = storage.CreateTemp(DirRef.Default.Instance, FileTid, 0);
        Assert.Equal("a.txt", Assert.IsType<CommitResult.Ok>(storage.Commit(temp, "a.txt")).FinalName);
        Assert.False(storage.Exists(DirRef.Default.Instance, "a.TXT"));
    }

    [Fact]
    public void SourceFiles_describe_read_from_offset_fail_midway_and_handle_directories_and_missing_files()
    {
        var files = new InMemorySourceFiles();
        var src = new SourceRef("C:\\x\\1.jpg");
        files.Put(src, "photo.jpg", Bytes.Of(Enumerable.Range(0, 10).Select(i => (byte)i).ToArray()), mtime: 5, mime: "image/jpeg");
        Assert.Equal(10, files.Describe(src)!.Size);
        Assert.Equal(new SourceStat(10, 5), files.Stat(src));
        var r = Assert.IsType<ReadResult<IChunkReader>.Ok>(files.Open(src, 4)).Value;
        var buf = new byte[100];
        Assert.Equal(new ReadResult<int>.Ok(6), r.Read(buf));
        Assert.Equal(4, buf[0]);
        files.EntryOf(src)!.FailAt = 3;
        var r2 = Assert.IsType<ReadResult<IChunkReader>.Ok>(files.Open(src, 0)).Value;
        Assert.Equal(new ReadResult<int>.Ok(3), r2.Read(buf));
        Assert.IsType<ReadResult<int>.Failed>(r2.Read(buf));
        var dir = new SourceRef("C:\\x\\dir");
        files.PutDirectory(dir, "相册");
        Assert.True(files.Describe(dir)!.IsDirectory);
        Assert.Null(files.Stat(dir));
        Assert.IsType<ReadResult<IChunkReader>.Failed>(files.Open(new SourceRef("C:\\x\\none"), 0));
        files.Modify(src, Bytes.Of(new byte[2]), 6);
        Assert.Equal(new SourceStat(2, 6), files.Stat(src));
        files.EntryOf(src)!.Readable = false;
        Assert.Null(files.Stat(src));
        Assert.IsType<ReadResult<IChunkReader>.Failed>(files.Open(src, 0));
    }

    // ---- 系统服务替身与生成器 ----

    [Fact]
    public async Task Clipboard_notifier_and_file_locator_record_calls_and_inject_failures()
    {
        var clip = new RecordingClipboard();
        Assert.Equal(new ClipRead.NoText(), await clip.ReadTextAsync(Ct));
        Assert.Equal(new ClipWrite.Ok(), await clip.WriteTextAsync("a\u0000b", Ct));
        Assert.Equal(new ClipRead.Text("a\u0000b"), await clip.ReadTextAsync(Ct));
        clip.FailWrites = true;
        Assert.Equal(new ClipWrite.Failed(), await clip.WriteTextAsync("x", Ct));
        Assert.Equal(new[] { "a\u0000b", "x" }, clip.Writes);
        clip.FailReads = true;
        Assert.Equal(new ClipRead.Failed(), await clip.ReadTextAsync(Ct));

        var notifier = new RecordingNotifier();
        var key = new NotifyKey.PairingRequest(new DeviceId(Id16(4)));
        notifier.Request(key, "Pixel", RequestKind.Pairing, null);
        Assert.Equal(new NotifyKey[] { key }, notifier.Active.Select(kv => kv.Key));
        notifier.Progress(Tid, "Pixel", "a.txt", 10);
        notifier.Progress(Tid, "Pixel", "a.txt", 20); // 同一任务的进度通知原地更新
        Assert.Equal(2, notifier.Active.Count);
        notifier.Cancel(key);
        notifier.Cancel(new NotifyKey.Transfer(Tid));
        Assert.Empty(notifier.Active);
        Assert.Equal(5, notifier.Events.Count);
        Assert.True(notifier.Enabled.Value);
        notifier.SetEnabled(false);
        Assert.False(notifier.Enabled.Value);

        var locator = new RecordingFileLocator();
        Assert.Equal(LocateResult.Ok, locator.OpenDirectory(DirRef.Default.Instance));
        locator.Result = _ => LocateResult.NotFound;
        Assert.Equal(LocateResult.NotFound, locator.OpenFile(new StoredFileRef("mem:/a"), "text/plain"));
        Assert.Equal(
            new LocateCall[] { new LocateCall.OpenDirectory(DirRef.Default.Instance), new LocateCall.OpenFile(new StoredFileRef("mem:/a"), "text/plain") },
            locator.Calls);

        var lifecycle = new FakeAppLifecycle();
        lifecycle.SetBackground(true);
        Assert.True(lifecycle.Background.Value);
    }

    [Fact]
    public void Engine_generators_produce_valid_samples()
    {
        foreach (var files in Gens.SourceFileSet().Draw(50))
        {
            Assert.InRange(files.Length, 1, 5);
            Assert.All(files, f => Assert.True(f.Name.Length > 0 && f.Content.Length <= 5 * ProtocolConstants.MaxChunk));
            var total = files.Sum(f => (long)f.Content.Length);
            foreach (var points in Gens.InterruptionPoints(total).Draw(5))
            {
                Assert.All(points, p => Assert.InRange(p, 0, total));
                Assert.Equal(points.Order(), points);
            }
        }

        Assert.All(Gens.FailureSchedule().Draw(50), s => Assert.All(s, i => Assert.InRange(i, 0, 29)));
        Assert.All(Gens.BackgroundToggles().Draw(50), t => Assert.InRange(t.Length, 0, 6));
        Assert.Contains(Gens.FileSize().Draw(500), n => n == ProtocolConstants.MaxChunk); // 偏向块边界
    }
}
