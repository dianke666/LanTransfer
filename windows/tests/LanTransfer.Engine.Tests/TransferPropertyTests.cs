using System.Security.Cryptography;
using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;
using static LanTransfer.Engine.Tests.XferKit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// Property 45、47、52（任务 18.6–18.9，与 Kotlin 侧 TransferPropertiesTest 对应；Property 46 在 ResumePropertyTests 中）。
/// 被测对象是异步的，因此用 [Fact] 按 Pbt.Iterations(100) 跑足用例数，每个用例由一个种子决定，失败信息里带种子。
/// </summary>
public sealed class TransferPropertyTests
{
    private static byte[] Sha(byte[] b) => SHA256.HashData(b);

    private static byte[] RandomBytes(Random rnd, int n)
    {
        var b = new byte[n];
        rnd.NextBytes(b);
        return b;
    }

    /// <summary>只持有会话的原始对端 R（192.168.1.9），与 B 互相信任；B 对它开启自动接收。</summary>
    private static async Task<Session> RawSessionAsync(XferWorld w)
    {
        var raw = new TestDevice("R", 5, Kit.Iface("wlan0", 192, 168, 1, 9), w.Net, w.Clock);
        raw.Trusts(w.B.D);
        w.B.D.Trusts(raw);
        w.B.D.Trust.SetAutoAccept(raw.Id, true);
        return Assert.IsType<OpenResult.Ok>(await Kit.Within(raw.Manager.OpenAsync(new Target.Device(w.B.D.Id)))).Session;
    }

    // Feature: lan-file-transfer, Property 45: 当且仅当校验通过才提交
    // Validates: Requirements 16.4, 16.5, 17.1, 17.2
    [Fact]
    public async Task Property45_CommitIffChecksumPasses()
    {
        // 原始发送方按扰动方式发送数据块与 FILE_END：替换某块数据、末尾多发、少发最后一块、篡改哈希（可组合）
        await ForSeedsAsync(100, async seed =>
        {
            var rnd = new Random(seed);
            using var w = new XferWorld();
            var s = await RawSessionAsync(w);
            var size = rnd.Next(5) == 0 ? 0 : rnd.Next(1, 300_000);
            var content = RandomBytes(rnd, size);
            var chunks = content.Chunk(65_536).ToList();
            var replace = chunks.Count > 0 && rnd.Next(2) == 0;
            var extra = rnd.Next(4) == 0;
            var drop = chunks.Count > 0 && rnd.Next(4) == 0;
            var tamper = rnd.Next(4) == 0;
            if (replace)
            {
                var k = rnd.Next(chunks.Count);
                var c = chunks[k].ToArray();
                c[rnd.Next(c.Length)] = unchecked((byte)(c[0] + 1));
                chunks[k] = c;
            }

            if (drop)
            {
                chunks.RemoveAt(chunks.Count - 1);
            }

            if (extra)
            {
                chunks.Add(RandomBytes(rnd, rnd.Next(1, 1000)));
            }

            var sent = chunks.SelectMany(c => c).ToArray();
            var endHash = tamper ? Sha([9, .. sent]) : Sha(rnd.Next(2) == 0 ? sent : content);
            var id = new TransferId(Bytes16.Of(RandomBytes(rnd, 16)));
            s.Send(new FileOffer(id, new ValueList<FileEntry>([new FileEntry("f.bin", (ulong)size, "application/octet-stream")])));
            Assert.Equal(new FileOfferReply(id, OfferDecision.Accept), await TextKit.NextAsync(s));
            long off = 0;
            foreach (var c in chunks)
            {
                s.Send(new FileChunk(id, 0, (ulong)off, Bytes.Of(c)));
                off += c.Length;
            }

            Assert.Empty(w.B.Received()); // 提交之前不出现最终文件名
            s.Send(new FileEnd(id, 0, Bytes.Of(endHash)));
            FileResult result;
            while (true)
            {
                var m = await TextKit.NextAsync(s);
                if (m is FileResult r)
                {
                    result = r;
                    break;
                }

                Assert.IsType<FileAck>(m);
            }

            var expectCommit = sent.Length == size && Sha(sent).AsSpan().SequenceEqual(endHash);
            var clue = $"size={size} replace={replace} extra={extra} drop={drop} tamper={tamper}";
            if (expectCommit)
            {
                Assert.True(result.Status == FileResultStatus.Completed, clue);
                await w.B.AwaitAsync(l => l.Any(v => v.Id == id && v.Task.Files[0].State == FileState.Completed));
                Assert.Equal(Bytes.Of(sent), w.B.Received()["f.bin"]);
            }
            else
            {
                Assert.True(result.Status == FileResultStatus.ChecksumFailed, clue);
                Assert.Empty(w.B.Received());
                await w.B.AwaitAsync(l => l.Any(v => v.Id == id && v.Task.Files[0].State == FileState.ChecksumFailed));
                await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件 " + clue);
            }
        });
    }

    // Feature: lan-file-transfer, Property 47: 故障隔离
    // Validates: Requirements 14.6, 17.2, 17.6, 18.2, 18.3, 18.7
    [Fact]
    public async Task Property47_FaultIsolation()
    {
        // 对端 A、C 各向 B 并行发送 2 个任务；对其中一个任务注入取消 / 校验失败 / 写入失败，或断开 A–B 的 Session。
        // 其他任务（以及不属于被断开 Session 的任务）全部完成，内容一致，接收方看到的已传输字节数不回退。
        await ForSeedsAsync(100, async seed =>
        {
            var rnd = new Random(seed);
            using var w = new XferWorld();
            w.AutoAccept();
            using var c = new XferDevice(new TestDevice("C", 3, Kit.Iface("wlan0", 192, 168, 1, 4), w.Net, w.Clock), w.Clock, 3);
            c.D.Trusts(w.B.D);
            w.B.D.Trusts(c.D);
            w.B.D.Trust.SetAutoAccept(c.D.Id, true);
            XferDevice[] senders = [w.A, w.A, c, c];
            var refs = senders.Select((s, k) => new[]
            {
                s.File($"t{k}a", rnd.Next(1, 600_000), rnd.Next()),
                s.File($"t{k}b", rnd.Next(0, 200_000), rnd.Next()),
            }).ToArray();
            var fault = rnd.Next(4); // 0 取消、1 校验失败、2 写入失败、3 断开 A–B
            var victim = rnd.Next(4);
            var ids = new TransferId?[4];
            string? victimHex = null;
            if (fault == 1)
            {
                // 发出 Offer 后修改源文件，使接收方校验失败
                ids[victim] = SentId(await senders[victim].Engine.SendAsync(w.B.D.Id, refs[victim]));
                senders[victim].Sources.Modify(refs[victim][0], Bytes.Of(RandomBytes(rnd, 10)), 7);
            }

            if (fault == 2)
            {
                w.B.Storage.WriteFailure = (r, len) =>
                    Volatile.Read(ref victimHex) is { } hex && r.Value.Contains(hex, StringComparison.Ordinal) && len > 1000 ? "写入失败" : null;
            }

            for (var k = 0; k < 4; k++)
            {
                ids[k] ??= SentId(await senders[k].Engine.SendAsync(w.B.D.Id, refs[k]));
                if (fault == 2 && k == victim)
                {
                    Volatile.Write(ref victimHex, ids[k]!.Value.Value.ToHex());
                }
            }

            var monotonic = new Dictionary<TransferId, long>();
            string? regressed = null;
            using var sub = w.B.Engine.Tasks.Subscribe(list =>
            {
                lock (monotonic)
                {
                    foreach (var v in list)
                    {
                        var now = v.Task.Files.Sum(f => f.Persisted);
                        var prev = monotonic.GetValueOrDefault(v.Id);
                        if (v.Task.State == TaskState.Running && now < prev)
                        {
                            regressed = $"{v.Id} {prev} -> {now}";
                        }

                        monotonic[v.Id] = Math.Max(prev, now);
                    }
                }
            });
            switch (fault)
            {
                case 0:
                    senders[victim].Engine.Cancel(ids[victim]!.Value);
                    break;
                case 3:
                    await w.B.AwaitAsync(l => l.Any(v => v.Task.Files.Any(f => f.Persisted > 0)));
                    foreach (var conn in w.Net.Connections.Where(x => x.ClientHost == "A"))
                    {
                        conn.Reset();
                    }

                    break;
            }

            var affected = fault == 3 ? new HashSet<int> { 0, 1 } : [victim];
            var clue = $"fault={fault} victim={victim}";
            for (var k = 0; k < 4; k++)
            {
                var id = ids[k]!.Value;
                if (affected.Contains(k))
                {
                    await w.B.AwaitAsync(l => l.Any(v => v.Id == id && TerminalOrInterrupted(v.Task.State)));
                    continue;
                }

                await w.B.AwaitStateAsync(id, TaskState.Completed);
            }

            lock (monotonic)
            {
                Assert.True(regressed is null, $"{clue} regressed {regressed}");
            }

            var got = w.B.Received();
            for (var k = 0; k < 4; k++)
            {
                if (affected.Contains(k))
                {
                    continue;
                }

                var id = ids[k]!.Value;
                await senders[k].AwaitStateAsync(id, TaskState.Completed);
                Assert.Equal([FileState.Completed, FileState.Completed], w.B.TaskOf(id).Files.Select(f => f.State).ToArray());
                Assert.Equal(senders[k].Content(refs[k][0]), got[$"t{k}a"]);
                Assert.Equal(senders[k].Content(refs[k][1]), got[$"t{k}b"]);
            }
        });
    }

    // Feature: lan-file-transfer, Property 52: 清空记录放弃未完成任务
    // Validates: Requirements 18.9, 20.5, 20.10
    [Fact]
    public async Task Property52_ClearAllAbandonsUnfinishedTasks()
    {
        // B 上有若干任务：等待确认（A 发来）、传输中（R 发来）、已中断（A 发来，A 随后在线或离线）、已完成；B 作为发送方的 OFFERING。
        await ForSeedsAsync(100, async seed =>
        {
            var rnd = new Random(seed);
            using var w = new XferWorld();
            using var gate = new ReadGate();
            var cd = new TestDevice("C", 3, Kit.Iface("wlan0", 192, 168, 1, 4), w.Net, w.Clock);
            cd.Listen();
            using var c = new XferDevice(cd, w.Clock, 3);
            c.D.Trusts(w.B.D);
            w.B.D.Trusts(c.D);
            w.A.D.Listen();
            w.B.D.Knows(w.A.D);

            // 传输中的任务由只发一块数据的原始发送方 R 产生，保证在放弃时仍在 RUNNING
            var rawSession = await RawSessionAsync(w);
            var rawInbox = new List<ProtocolMessage>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var m in rawSession.Incoming.ReadAllAsync(Kit.Ct))
                    {
                        lock (rawInbox)
                        {
                            rawInbox.Add(m);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            // 0 等待确认 1 传输中 2 已中断 3 已完成 4 B 发出的 OFFERING；断开 A–B 的步骤（2）排在 A 发起的其他任务之前
            int[] order = [3, 2, 0, 1, 4];
            var kinds = Enumerable.Range(0, rnd.Next(1, 6)).Select(_ => rnd.Next(5)).ToList().OrderBy(k => Array.IndexOf(order, k)).ToList();
            var ids = new List<(int Kind, TransferId Id)>();
            foreach (var k in kinds)
            {
                switch (k)
                {
                    case 0:
                    {
                        w.B.D.Trust.SetAutoAccept(w.A.D.Id, false);
                        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File($"w{ids.Count}", 10)]));
                        await w.B.AwaitStateAsync(id, TaskState.AwaitingUser);
                        ids.Add((k, id));
                        break;
                    }

                    case 1:
                    {
                        var id = new TransferId(Bytes16.Of(RandomBytes(rnd, 16)));
                        rawSession.Send(new FileOffer(id, new ValueList<FileEntry>([new FileEntry($"r{ids.Count}", 1_000_000, "a/b")])));
                        rawSession.Send(new FileChunk(id, 0, 0, Bytes.Of(new byte[1000])));
                        await w.B.AwaitDataAsync(id);
                        ids.Add((k, id));
                        break;
                    }

                    case 2:
                    {
                        w.B.D.Trust.SetAutoAccept(w.A.D.Id, true);
                        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [w.A.File($"i{ids.Count}", Big, gate: gate)]));
                        await w.B.AwaitDataAsync(id);
                        w.Cut("A");
                        await w.B.AwaitStateAsync(id, TaskState.Interrupted);
                        await w.A.AwaitStateAsync(id, TaskState.Interrupted);
                        ids.Add((k, id));
                        break;
                    }

                    case 3:
                    {
                        w.B.D.Trust.SetAutoAccept(c.D.Id, true);
                        var id = SentId(await c.Engine.SendAsync(w.B.D.Id, [c.File($"d{ids.Count}", 100)]));
                        await w.B.AwaitStateAsync(id, TaskState.Completed);
                        ids.Add((k, id));
                        break;
                    }

                    default:
                    {
                        c.D.Trust.SetAutoAccept(w.B.D.Id, false);
                        w.B.D.Knows(c.D);
                        var id = SentId(await w.B.Engine.SendAsync(c.D.Id, [w.B.File($"o{ids.Count}", 10)]));
                        await c.AwaitStateAsync(id, TaskState.AwaitingUser);
                        ids.Add((k, id));
                        break;
                    }
                }
            }

            var clue = $"kinds=[{string.Join(", ", kinds)}]";
            var unfinished = ids.Count(x => x.Kind != 3);
            var completedNames = w.B.Received().Keys.ToHashSet();
            Assert.True(unfinished == w.B.Engine.UnfinishedCount.Value, clue);
            await w.B.Engine.AbandonAllAsync();
            Assert.IsType<StoreResult.Ok>(w.B.History.ClearAll());
            foreach (var (k, id) in ids)
            {
                Assert.Null(w.B.Store.Get(id)); // ClearAll 之后不再有任何任务行
                switch (k)
                {
                    case 0:
                        await w.A.AwaitStateAsync(id, TaskState.Rejected);
                        break;
                    case 1:
                        await Kit.WaitUntilAsync(
                            () =>
                            {
                                lock (rawInbox)
                                {
                                    return rawInbox.Contains(new TransferCancel(id, TransferCancelReason.User));
                                }
                            },
                            "R 收到取消 " + clue);
                        break;
                    case 4:
                        await c.AwaitStateAsync(id, TaskState.PeerCancelled);
                        break;
                }
            }

            // 已中断的任务：与对方没有 Session 时写入 pending_cancel，否则已直接送达
            var pending = w.B.Db.PendingCancels().Select(p => p.TransferId).ToHashSet();
            foreach (var id in ids.Where(x => x.Kind == 2).Select(x => x.Id).Where(i => !pending.Contains(i)))
            {
                await w.A.AwaitStateAsync(id, TaskState.PeerCancelled);
            }

            // 对方下次建立 Session 后 2 s 内收到取消
            if (pending.Count > 0)
            {
                var t0 = w.Clock.MonotonicMs();
                Assert.IsType<OpenResult.Ok>(await Kit.Within(w.A.D.Manager.OpenAsync(new Target.Device(w.B.D.Id))));
                foreach (var id in pending)
                {
                    await w.A.AwaitStateAsync(id, TaskState.PeerCancelled);
                }

                Assert.True(w.Clock.MonotonicMs() - t0 <= 2_000, clue);
            }

            await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件 " + clue);
            Assert.True(completedNames.SetEquals(w.B.Received().Keys), clue);
            Assert.Empty(Assert.IsType<ListResult<HistoryRecord>.Ok>(w.B.History.List()).Items);
            Assert.Empty(w.B.Engine.Tasks.Value);
            await w.B.Engine.RestoreOnStartupAsync();
            Assert.Empty(w.B.Engine.Tasks.Value);
        });
    }
}
