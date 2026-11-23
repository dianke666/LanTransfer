using LanTransfer.Domain;
using LanTransfer.Engine.Ports;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// Property 39（任务 17.3，与 Kotlin 侧 TextPropertiesTest 对应）。被测对象是异步的，因此用 [Fact] 按 Pbt.Iterations(100) 跑足用例数，
/// 每个用例由一个种子决定动作序列，失败信息里带种子与动作日志。
/// </summary>
public sealed class TextPropertyTests
{
    private const int Keys = 4;

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

    // Feature: lan-file-transfer, Property 39: 文字接收恰好一次
    // Validates: Requirements 11.4, 11.8, 11.12, 12.7, 13.8, 20.8, 21.6
    [Fact]
    public async Task Property39_TextReceivedExactlyOnce()
    {
        // 两个发送方共用同一组消息 ID（检查按（发送方, 消息 ID）区分）；每个 ID 一份合法正文、一份非法正文。
        // 动作：投递（75% 合法）、切换 History_Store 写入失败、切换前后台、切换自动复制。参考模型按 C1.12 编写。
        await ForSeedsAsync(100, async seed =>
        {
            var rnd = new Random(seed);
            var bodies = Gens.UnicodeTextFragments(8).Draw(Keys).Select(f => string.Concat(f)).ToArray();
            var invalid = Gens.InvalidUtf8Gen().Draw(Keys);
            var clock = new FakeClock();
            var net = new InMemoryNetwork(clock.Time);
            var bd = new TestDevice("B", 2, Kit.Iface("wlan0", 192, 168, 1, 3), net, clock);
            bd.Listen();
            var b = new TextDevice(bd, clock, 2, dispatch: false);
            var processed = 0;
            using var dispatch = TextKit.DispatchTo(bd.Manager, (s, m) =>
            {
                b.Service.OnMessage(s, m);
                Interlocked.Increment(ref processed);
            });
            TestDevice[] senders =
            [
                new("A", 1, Kit.Iface("wlan0", 192, 168, 1, 2), net, clock),
                new("C", 3, Kit.Iface("wlan0", 192, 168, 1, 4), net, clock),
            ];
            foreach (var sd in senders)
            {
                sd.Trusts(bd);
                bd.Trusts(sd);
            }

            var failing = false;
            var background = false;
            var autoCopy = false;
            b.Db.FailWrites = op => Volatile.Read(ref failing) && op == "appendText";

            // 参考模型
            var store = new HashSet<TextKey>();
            var shown = new Dictionary<TextKey, bool>();
            var expReplies = senders.Select(_ => new List<ProtocolMessage>()).ToArray();
            var expItems = senders.Select(_ => new List<string>()).ToArray();
            var expNotify = new List<NotifyKey>();
            var expCopies = new List<string>();

            // 此前运行中已保存的记录
            foreach (var sd in senders)
            {
                for (var k = 0; k < Keys; k++)
                {
                    if (rnd.Next(5) != 0)
                    {
                        continue;
                    }

                    var key = new TextKey(sd.Id, TextKit.MessageIdOf(k + 1));
                    b.History.AppendText(new TextRecord(0, sd.Id, sd.Name, key.MessageId, Direction.Receive, Bytes.Of(TextKit.Utf8(bodies[k])), 1, new RecordTime(1, 480), TextStatus.Received));
                    store.Add(key);
                }
            }

            var sessions = new Session[senders.Length];
            var replies = senders.Select(_ => new List<ProtocolMessage>()).ToArray();
            for (var i = 0; i < senders.Length; i++)
            {
                sessions[i] = Assert.IsType<OpenResult.Ok>(await Kit.Within(senders[i].Manager.OpenAsync(new Target.Device(bd.Id)))).Session;
                var (s, list) = (sessions[i], replies[i]);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var m in s.Incoming.ReadAllAsync(Kit.Ct))
                        {
                            lock (list)
                            {
                                list.Add(m);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            }

            var log = new List<string>();
            var delivered = 0;
            var steps = rnd.Next(1, 40);
            for (var step = 0; step < steps; step++)
            {
                switch (rnd.Next(10))
                {
                    case 0:
                        Volatile.Write(ref failing, !failing);
                        break;
                    case 1:
                        background = !background;
                        b.Lifecycle.SetBackground(background);
                        break;
                    case 2:
                        autoCopy = !autoCopy;
                        var on = autoCopy;
                        b.Settings.Update(x => x with { AutoCopy = on });
                        break;
                    default:
                    {
                        var i = rnd.Next(senders.Length);
                        var k = rnd.Next(Keys);
                        var valid = rnd.Next(4) != 0;
                        log.Add($"deliver(s={i}, k={k}, valid={valid}, failing={failing}, bg={background}, copy={autoCopy})");
                        var id = TextKit.MessageIdOf(k + 1);
                        sessions[i].Send(TextKit.RawText(k + 1, valid ? TextKit.Utf8(bodies[k]) : invalid[k].Bytes), Priority.Text);

                        // 等 B 处理完这条消息：分发器已交给服务，再用一次 actor 往返作为屏障
                        delivered++;
                        await Kit.WaitUntilAsync(() => Volatile.Read(ref processed) == delivered, "dispatch");
                        await b.Service.ForgetAsync(new HashSet<TextKey>()).WaitAsync(TimeSpan.FromSeconds(10), Kit.Ct);

                        var key = new TextKey(senders[i].Id, id);
                        bool? seen = shown.TryGetValue(key, out var sv) ? sv : null;
                        void Display()
                        {
                            expItems[i].Add(bodies[k]);
                            if (background)
                            {
                                expNotify.Add(new NotifyKey.Text(key.Peer, key.MessageId));
                            }

                            if (autoCopy)
                            {
                                expCopies.Add(bodies[k]);
                            }
                        }

                        if (!valid)
                        {
                            expReplies[i].Add(new TextCorrupt(id));
                        }
                        else if (seen == true)
                        {
                            expReplies[i].Add(new TextAck(id));
                        }
                        else if (store.Contains(key))
                        {
                            expReplies[i].Add(new TextAck(id));
                            shown[key] = true;
                        }
                        else if (failing)
                        {
                            if (seen is null)
                            {
                                Display();
                                shown[key] = false;
                            }
                        }
                        else
                        {
                            store.Add(key);
                            expReplies[i].Add(new TextAck(id));
                            if (seen is null)
                            {
                                Display();
                            }

                            shown[key] = true;
                        }

                        break;
                    }
                }
            }

            var context = $"seed={seed}\n{string.Join("\n", log)}";
            for (var i = 0; i < senders.Length; i++)
            {
                var (list, expected) = (replies[i], expReplies[i]);
                await Kit.WaitUntilAsync(() => { lock (list) { return list.Count >= expected.Count; } }, $"replies {context}");
            }

            await Kit.WaitUntilAsync(() => b.Clipboard.Writes.Count >= expCopies.Count, $"clipboard {context}");
            await Kit.Settle(20);
            for (var i = 0; i < senders.Length; i++)
            {
                List<ProtocolMessage> got;
                lock (replies[i])
                {
                    got = [.. replies[i]];
                }

                Assert.True(expReplies[i].SequenceEqual(got), $"replies[{i}] {context}");
                Assert.True(expItems[i].SequenceEqual(b.Items(senders[i]).Select(t => t.Body)), $"items[{i}] {context}");

                // 只在写入 History_Store 之后回复 TEXT_ACK
                foreach (var ack in got.OfType<TextAck>())
                {
                    Assert.True(b.History.FindText(senders[i].Id, ack.MessageId) is not null, $"ack without record {context}");
                }
            }

            Assert.True(expNotify.SequenceEqual(b.Notifier.Events.Select(e => ((NotifyEvent.Text)e).Key)), $"notifications {context}");
            Assert.True(expCopies.SequenceEqual(b.Clipboard.Writes), $"clipboard {context}");
            Assert.Equal(expCopies.LastOrDefault(), b.Clipboard.Content);

            // 非法正文、写入失败都不留下记录
            Assert.True(store.SetEquals(b.TextRecords().Select(r => new TextKey(r.Peer, r.MessageId))), $"store {context}");
            b.Service.Dispose();
        });
    }
}
