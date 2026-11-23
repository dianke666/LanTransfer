using FsCheck.Fluent;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// Property 40–44（任务 15.3–15.7，与 Kotlin 侧 SessionPropertiesTest 对应）。被测对象是异步的，FsCheck 的同步属性不便驱动，
/// 因此用 [Fact] 按 Pbt.Iterations(100) 跑足用例数，每个用例由一个种子决定，失败信息里带种子。
/// </summary>
public sealed class SessionPropertyTests
{
    private static TextAck AckOf(int n) => new(new MessageId(Bytes16.Of(Enumerable.Repeat((byte)n, 16).ToArray())));

    private static Text TextOf(int n) => new(new MessageId(Bytes16.Of(Enumerable.Repeat((byte)n, 16).ToArray())), 1_700_000_000_000, Bytes.Of([(byte)n]));

    /// <summary>运行 Pbt.Iterations(base) 个用例；种子 = 基准种子 + 序号（基准种子取自 LT_PBT_SEED，未设置时随机）。</summary>
    private static async Task ForSeedsAsync(int baseIterations, Func<int, Task> body)
    {
        var start = int.TryParse(Environment.GetEnvironmentVariable(Pbt.SeedEnv), out var s) ? s : Random.Shared.Next(0, int.MaxValue / 2);
        var n = Pbt.Iterations(baseIterations);
        for (var i = 0; i < n; i++)
        {
            var seed = start + i;
            try
            {
                await body(seed);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new Xunit.Sdk.XunitException($"seed={seed}（可设 {Pbt.SeedEnv}={seed} 重放）: {e.Message}\n{e}");
            }
        }
    }

    // Feature: lan-file-transfer, Property 40: Pending 阶段的应用消息被拒
    // Validates: Requirements 10.4
    [Fact]
    public async Task Property40_PendingPhaseRejectsApplicationMessages()
    {
        // 前缀：白名单内、且不会改变状态的消息（SESSION_CLOSE 会关闭连接，接收方收到 PAIR_COMPLETE 会转为 Active）
        var allowed = Gens.ProtocolMessageGen().Where(m => Connection.PendingAllowed.Contains(m.Type) && m is not SessionClose and not PairComplete);
        var forbidden = Gens.ProtocolMessageGen().Where(m => !Connection.PendingAllowed.Contains(m.Type));
        await ForSeedsAsync(100, async seed =>
        {
            var r = new Random(seed);
            var prefix = allowed.Draw(r.Next(0, 4));
            var bad = forbidden.Draw(1)[0];
            var tail = Gens.ProtocolMessageGen().Draw(r.Next(0, 4));
            var t = await ConnUnderTest.CreateAsync(new FakeClock());
            foreach (var m in prefix.Append(bad).Concat(tail))
            {
                await t.Peer.SendAsync(m);
            }

            var clue = $"prefix=[{string.Join(", ", prefix)}] bad={bad}";
            Assert.True(new SessionResult(SessionResultCode.NotTrusted) == await t.Peer.ReceiveAsync(), clue);
            Assert.True(await t.Peer.ReceiveAsync() is null, clue);
            Assert.Equal(new CloseReason.PendingViolation(), await Kit.Within(t.Conn.Closed));
            Assert.Equal(prefix.Where(m => m is not Heartbeat), await t.DrainIncomingAsync());
        });
    }

    // Feature: lan-file-transfer, Property 41: 信任判定表
    // Validates: Requirements 6.6, 6.7, 6.9, 9.2, 9.4, 9.5
    [Fact]
    public async Task Property41_TrustDecisionTable()
    {
        var cases = new List<(bool Self, bool ATrustsB, bool AKeyOk, bool BTrustsA, bool BKeyOk)> { (true, false, true, false, true) };
        foreach (var aT in new[] { false, true })
        {
            foreach (var aK in aT ? new[] { false, true } : [true])
            {
                foreach (var bT in new[] { false, true })
                {
                    foreach (var bK in bT ? new[] { false, true } : [true])
                    {
                        cases.Add((false, aT, aK, bT, bK));
                    }
                }
            }
        }

        Assert.Equal(10, cases.Count);
        foreach (var c in cases)
        {
            var clock = new FakeClock();
            var net = new InMemoryNetwork(clock.Time);
            var ifA = Kit.Iface("wlan0", 192, 168, 1, 2);
            var ifB = Kit.Iface("wlan0", 192, 168, 1, 3);
            var a = new TestDevice("A", 1, ifA, net, clock);
            var b = new TestDevice("B", 2, ifB, net, clock, identityOverride: c.Self ? a.Identity : null);
            b.Listen();
            var wrongForB = Kit.KeyPairOf(90).PublicKey;
            var wrongForA = Kit.KeyPairOf(91).PublicKey;
            if (c.ATrustsB)
            {
                a.Trusts(b, key: c.AKeyOk ? b.PublicKey : wrongForB);
            }

            if (c.BTrustsA)
            {
                b.Trusts(a, key: c.BKeyOk ? a.PublicKey : wrongForA);
            }

            var r = await Kit.Within(a.Manager.OpenAsync(new Target.Address(ifB.Address, b.Port)));
            await Kit.Settle(20);
            b.Manager.PendingInbound.TryRead(out var inbound);
            var clue = $"{c} → {r}";
            if (c.Self)
            {
                Assert.True(new OpenResult.Failed(new OpenFailure.SelfConnection()) == r, clue);
            }
            else if (c.ATrustsB && !c.AKeyOk)
            {
                Assert.True(new OpenResult.Failed(new OpenFailure.KeyMismatch("B")) == r, clue);
                Assert.Equal(wrongForB, a.Trust.Get(b.Id)!.PublicKey);
            }
            else if (c.ATrustsB && c.BTrustsA && c.BKeyOk)
            {
                Assert.True(r is OpenResult.Ok, clue);
                Assert.NotNull(b.Manager.GetSession(a.Id));
            }
            else if (c.ATrustsB && c.BTrustsA)
            {
                Assert.True(new OpenResult.Failed(new OpenFailure.PeerKeyMismatch("B")) == r, clue);
                Assert.Equal(wrongForA, b.Trust.Get(a.Id)!.PublicKey);
            }
            else if (c.ATrustsB)
            {
                Assert.True(new OpenResult.Failed(new OpenFailure.NotTrusted()) == r, clue);
            }
            else
            {
                Assert.True(r is OpenResult.Pairing { Connection.Intent: Intent.Pair }, clue);
                Assert.True(inbound is { Intent: Intent.Pair }, clue);
            }

            if (r is not OpenResult.Pairing)
            {
                Assert.True(inbound is null, clue); // 不出现任何配对界面
            }

            if (r is not OpenResult.Ok)
            {
                Assert.Empty(a.Manager.Sessions.Value);
                Assert.Empty(b.Manager.Sessions.Value);
            }
        }
    }

    // Feature: lan-file-transfer, Property 42: 每个对端恰好 1 个 Session
    // Validates: Requirements 9.2, 25.2
    [Fact]
    public async Task Property42_ExactlyOneSessionPerPeer()
    {
        await ForSeedsAsync(100, async seed =>
        {
            var r = new Random(seed);
            var clock = new FakeClock();
            var net = new InMemoryNetwork(clock.Time);
            var n = r.Next(2, 5);
            var idBytes = Enumerable.Range(1, n).Select(i => i * 10).OrderBy(_ => r.Next()).ToArray(); // 设备 ID 的大小顺序与编号无关
            var devices = Enumerable.Range(0, n).Select(i => new TestDevice($"D{i}", idBytes[i], Kit.Iface("wlan0", 192, 168, 1, (byte)(10 + i)), net, clock)).ToArray();
            foreach (var d in devices)
            {
                d.Listen();
            }

            foreach (var x in devices)
            {
                foreach (var y in devices.Where(y => !ReferenceEquals(x, y)))
                {
                    x.Trusts(y);
                }
            }

            var opens = Enumerable.Range(0, r.Next(1, 12)).Select(_ =>
            {
                var from = r.Next(n);
                return (From: from, To: (from + r.Next(1, n)) % n, Wait: r.Next(0, 4));
            }).ToArray();
            var results = await Kit.Within(Task.WhenAll(opens.Select(o => Task.Run(
                async () =>
                {
                    await Task.Delay(o.Wait, Kit.Ct);
                    return await devices[o.From].Manager.OpenAsync(new Target.Device(devices[o.To].Id));
                },
                Kit.Ct))));
            var pairs = opens.Select(o => (Math.Min(o.From, o.To), Math.Max(o.From, o.To))).Distinct().ToArray();
            var clue = $"ids=[{string.Join(",", idBytes)}] opens=[{string.Join(" ", opens)}] results=[{string.Join(", ", results)}]";
            await Kit.WaitUntilAsync(() => net.Connections.Count(c => c.IsOpen) == pairs.Length, $"abandoned connections closed: {clue}");
            for (var k = 0; k < opens.Length; k++)
            {
                var s = devices[opens[k].From].Manager.GetSession(devices[opens[k].To].Id);
                Assert.True(s is not null, clue);
                Assert.True(results[k] is OpenResult.Ok ok && ReferenceEquals(ok.Session, s), $"open #{k}: {clue}");
            }

            foreach (var (i, j) in pairs)
            {
                var sx = devices[i].Manager.GetSession(devices[j].Id)!;
                var sy = devices[j].Manager.GetSession(devices[i].Id);
                Assert.True(sy is not null, clue);
                Assert.True(sx.Connection.Local == sy.Connection.Remote, $"同一条 TCP 连接: {clue}");
                Assert.True(sx.Initiator == sy.Initiator, clue);
            }

            for (var i = 0; i < n; i++)
            {
                Assert.True(devices[i].Manager.Sessions.Value.Count == pairs.Count(p => p.Item1 == i || p.Item2 == i), clue);
            }

            foreach (var d in devices)
            {
                d.Manager.Dispose();
            }
        });
    }

    // Feature: lan-file-transfer, Property 43: 心跳与无响应判定
    // Validates: Requirements 18.5, 18.6
    [Fact]
    public async Task Property43_HeartbeatAndUnresponsiveDetection()
    {
        await ForSeedsAsync(100, async seed =>
        {
            var r = new Random(seed);
            const long horizon = 60_000;
            // 对方发帧的间隔：避开恰好 15 000 ms（与判定时刻同时发生时先后不确定）
            var peerTimes = new List<long>();
            var t = 0L;
            while (true)
            {
                t += r.Next(5) == 0 ? r.NextInt64(15_001, 25_000) : r.NextInt64(1, 14_999);
                if (t > horizon)
                {
                    break;
                }

                peerTimes.Add(t);
            }

            var localTimes = Enumerable.Range(0, r.Next(0, 12)).Select(_ => r.NextInt64(0, horizon)).Order().ToList();
            var events = peerTimes.Select(p => (Time: p, Peer: true)).Concat(localTimes.Select(l => (Time: l, Peer: false))).OrderBy(e => e.Time).ToList();

            var clock = new FakeClock();
            RecordingConn? rec = null;
            var u = await ConnUnderTest.CreateAsync(clock, wrap: c => rec = new RecordingConn(c, clock));
            u.Conn.Activate();
            for (var i = 0; i < events.Count; i++)
            {
                await Kit.AdvanceToAsync(clock, u.Conn, events[i].Time, () => u.Peer.FramesSent);
                if (events[i].Peer)
                {
                    await u.Peer.SendAsync(i % 2 == 0 ? Heartbeat.Instance : AckOf(i));
                }
                else
                {
                    u.Conn.Enqueue(AckOf(i), Priority.Control);
                }
            }

            await Kit.AdvanceToAsync(clock, u.Conn, horizon, () => u.Peer.FramesSent);

            // 参考模型：第一个达到 15 s 的无接收区间
            var last = 0L;
            foreach (var p in peerTimes)
            {
                if (p - last >= 15_000)
                {
                    break;
                }

                last = p;
            }

            long? expectedClose = last + 15_000 <= horizon ? last + 15_000 : null;
            var writes = rec!.Writes;
            var clue = $"peer=[{string.Join(",", peerTimes)}] local=[{string.Join(",", localTimes)}] writes=[{string.Join(",", writes.Select(w => $"{w.Time}/{w.Length}"))}]";
            if (expectedClose is null)
            {
                Assert.True(u.Conn.State.Value is ConnState.Active, clue);
            }
            else
            {
                Assert.True(u.Conn.State.Value == new ConnState.Closed(new CloseReason.PeerUnresponsive()), $"{u.Conn.State.Value} {clue}");
            }

            var end = expectedClose ?? horizon;
            var prev = 0L;
            foreach (var (time, len) in writes)
            {
                Assert.True(time - prev <= 5_000, $"gap at {time}: {clue}");
                if (len == RecordingConn.HeartbeatWireLength)
                {
                    Assert.True(time - prev >= 5_000, $"early heartbeat at {time}: {clue}");
                }

                Assert.True(time <= end, $"write after close at {time}: {clue}");
                prev = time;
            }

            Assert.True(end - prev <= 5_000, $"idle tail: {clue}");
        });
    }

    // Feature: lan-file-transfer, Property 44: 写循环优先级与公平性
    // Validates: Requirements 25.1, 25.5, 25.6
    [Fact]
    public async Task Property44_WriterPriorityAndFairness()
    {
        await ForSeedsAsync(100, async seed =>
        {
            var r = new Random(seed);
            GatedConn? gate = null;
            var u = await ConnUnderTest.CreateAsync(new FakeClock(), wrap: c => gate = new GatedConn(c));
            u.Conn.Activate();

            // 参考模型
            var p0 = new Queue<ProtocolMessage>();
            var p1 = new Queue<ProtocolMessage>();
            var sources = new List<Queue<ProtocolMessage>>();
            var rr = 0;
            var inFlight = false;
            var releases = 0;
            var expected = new List<ProtocolMessage>();

            ProtocolMessage? Pick()
            {
                if (p0.TryDequeue(out var a))
                {
                    return a;
                }

                if (p1.TryDequeue(out var b))
                {
                    return b;
                }

                for (var k = 0; k < sources.Count; k++)
                {
                    var i = (rr + k) % sources.Count;
                    if (sources[i].TryDequeue(out var m))
                    {
                        rr = (i + 1) % sources.Count;
                        return m;
                    }
                }

                return null;
            }

            // 模型挑出下一项后，等实际写循环进入同一次写入；没有可写的数据时等它空闲
            async Task SettleAsync()
            {
                if (!inFlight && Pick() is { } next)
                {
                    expected.Add(next);
                    inFlight = true;
                }

                if (inFlight)
                {
                    await Kit.WaitUntilAsync(() => gate!.Entered == expected.Count, "writer enters gate");
                }
                else
                {
                    await Kit.WaitUntilAsync(() => gate!.Completed == releases && u.Conn.IsQuiescent(0), "writer idle");
                }
            }

            await SettleAsync();
            var counter = 0;
            var sourceCount = 0;
            for (var step = r.Next(5, 40); step > 0; step--)
            {
                switch (r.Next(5))
                {
                    case 0:
                        {
                            var m = AckOf(counter++ & 0x7F);
                            p0.Enqueue(m);
                            u.Conn.Enqueue(m, Priority.Control);
                            break;
                        }

                    case 1:
                        {
                            var m = TextOf(counter++ & 0x7F);
                            p1.Enqueue(m);
                            u.Conn.Enqueue(m, Priority.Text);
                            break;
                        }

                    case 2 when sourceCount < 4:
                        {
                            var tid = new TransferId(Bytes16.Of(Enumerable.Repeat((byte)sourceCount, 16).ToArray()));
                            sourceCount++;
                            var chunks = Enumerable.Range(0, r.Next(0, 7)).Select(k => (ProtocolMessage)new FileChunk(tid, 0, (ulong)k, Bytes.Of([(byte)k]))).ToList();
                            sources.Add(new Queue<ProtocolMessage>(chunks));
                            u.Conn.AttachChunkSource(new QueueSource(new Queue<ProtocolMessage>(chunks)));
                            break;
                        }

                    case 2:
                        break;

                    default:
                        for (var k = r.Next(1, 3); k > 0 && inFlight; k--)
                        {
                            gate!.Release();
                            releases++;
                            inFlight = false;
                            await SettleAsync();
                        }

                        break;
                }

                await SettleAsync();
            }

            while (inFlight)
            {
                gate!.Release();
                releases++;
                inFlight = false;
                await SettleAsync();
            }

            var actual = new List<ProtocolMessage>();
            for (var i = 0; i < expected.Count; i++)
            {
                actual.Add((await u.Peer.ReceiveAsync())!);
            }

            Assert.Equal(expected, actual);
            // 同一文件的 offset 严格连续、没有丢失
            foreach (var g in actual.OfType<FileChunk>().GroupBy(c => c.TransferId))
            {
                Assert.Equal(Enumerable.Range(0, g.Count()).Select(k => (ulong)k), g.Select(c => c.Offset));
            }
        });
    }

    private sealed class QueueSource(Queue<ProtocolMessage> queue) : IChunkSource
    {
        public ProtocolMessage? Poll() => queue.TryDequeue(out var m) ? m : null;
    }
}
