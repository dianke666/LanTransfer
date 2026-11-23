using LanTransfer.Domain;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;
using static LanTransfer.Engine.Tests.XferKit;

namespace LanTransfer.Engine.Tests;

/// <summary>
/// Property 46（任务 18.7，与 Kotlin 侧 ResumePropertyTest 对应）。Kotlin 的两个回归种子在 C# 的随机数序列下没有意义，
/// 这里把它们覆盖的两种情形写成确定的用例：接收方已完成而发送方没收到最后的 FILE_RESULT；写入失败后由接收方继续。
/// </summary>
public sealed class ResumePropertyTests
{
    // Feature: lan-file-transfer, Property 46: 续传等价
    // Validates: Requirements 17.3, 17.4, 17.5, 17.6, 17.8, 17.10
    [Fact]
    public async Task Property46_ResumeEquivalence()
    {
        // 任意文件集合（含 0 字节）；0–3 次中断（在任意字节位置断开连接，或接收方写入失败）；中断期间可能修改未完成的源文件、
        // 删除接收方的临时文件；由任意一方点击继续。最终每个文件与源文件当前内容逐字节一致。
        await ForSeedsAsync(100, ResumeScenarioAsync);
    }

    [Fact]
    public async Task ReceiverCompleted_SenderMissedLastFileResult_SenderResumeCompletes()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        var r = w.A.File("x", 100_000);

        // 提交时让连接静默丢弃之后写出的全部字节：B 的 FILE_RESULT 到不了 A
        w.B.Storage.CommitFailure = _ =>
        {
            foreach (var c in w.Net.Connections)
            {
                c.Blackhole();
            }

            return null;
        };
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [r]));
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        w.Cut();
        await w.A.AwaitStateAsync(id, TaskState.Interrupted);
        Assert.Equal(new ResumeResult.Started(), await Kit.Within(w.A.Engine.ResumeAsync(id)));
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        Assert.All(w.A.TaskOf(id).Files, f => Assert.Equal(FileState.Completed, f.State));
        Assert.Equal(w.A.Content(r), w.B.Received()["x"]);
    }

    [Fact]
    public async Task WriteFailure_ThenReceiverResumes_Completes()
    {
        using var w = new XferWorld();
        w.AutoAccept();
        var fail = true;
        w.B.Storage.WriteFailure = (_, len) => Volatile.Read(ref fail) && len >= 100_000 ? "磁盘已满" : null;
        var r = w.A.File("x", 300_000);
        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, [r]));
        await w.B.AwaitStateAsync(id, TaskState.Interrupted);
        await w.A.AwaitStateAsync(id, TaskState.Interrupted);
        Volatile.Write(ref fail, false);
        Assert.Equal(new ResumeResult.Started(), await Kit.Within(w.B.Engine.ResumeAsync(id)));
        await w.A.AwaitStateAsync(id, TaskState.Completed);
        await w.B.AwaitStateAsync(id, TaskState.Completed);
        Assert.Equal(w.A.Content(r), w.B.Received()["x"]);
        Assert.Empty(w.B.Storage.TempRefs);
    }

    /// <summary>本轮的中断方式：接收方写入到 At 字节时触发（只在 B 处于 Running 时触发，排除上一轮写盘任务的尾声）。</summary>
    private sealed class Trigger
    {
        public long At = long.MaxValue;
        public bool FailWrite;
        public bool Fired;
        public TransferId? Id;
    }

    /// <summary>Property 46 的一个用例（由 seed 决定）。卡住时（真实时间 30 s）报告双方状态与步骤日志。</summary>
    private static async Task ResumeScenarioAsync(int seed)
    {
        var rnd = new Random(seed);
        using var w = new XferWorld();
        w.AutoAccept();
        w.A.D.Listen();
        w.B.D.Knows(w.A.D);
        var refs = Enumerable.Range(0, rnd.Next(1, 5)).Select(i => w.A.File($"f{i}", rnd.Next(4) == 0 ? 0 : rnd.Next(1, 400_000), rnd.Next())).ToList();
        var log = new List<string>();
        void Note(string s)
        {
            lock (log)
            {
                log.Add(s);
            }
        }

        string Log()
        {
            lock (log)
            {
                return string.Join("\n", log);
            }
        }

        Note($"seed={seed} files=[{string.Join(", ", refs.Select(r => w.A.Content(r).Length))}]");
        var trig = new Trigger();
        w.B.Storage.WriteFailure = (_, len) =>
        {
            bool failWrite;
            lock (trig)
            {
                if (trig.Fired || len < trig.At || trig.Id is not { } tid || w.B.State(tid) != TaskState.Running)
                {
                    return null;
                }

                trig.Fired = true;
                failWrite = trig.FailWrite;
            }

            if (failWrite)
            {
                return "磁盘已满";
            }

            w.Cut();
            return null;
        };
        void Arm()
        {
            lock (trig)
            {
                trig.Fired = false;
                trig.FailWrite = rnd.Next(3) == 0;
                trig.At = rnd.NextInt64(0, 400_000);
            }
        }

        bool Fired()
        {
            lock (trig)
            {
                return trig.Fired;
            }
        }

        async Task UntilAsync(TransferId id, string what, Func<bool> cond)
        {
            var deadline = Environment.TickCount64 + 30_000;
            while (!cond())
            {
                if (Environment.TickCount64 > deadline)
                {
                    throw new Xunit.Sdk.XunitException($"stuck waiting for {what}\nA={Describe(w.A, id)}\nB={Describe(w.B, id)}\n{Log()}");
                }

                await Task.Delay(5, Kit.Ct);
            }
        }

        static bool Settled(TaskState? s) => TerminalOrInterrupted(s);

        var interruptions = rnd.Next(0, 4);
        if (interruptions > 0)
        {
            Arm();
        }

        var id = SentId(await w.A.Engine.SendAsync(w.B.D.Id, refs));
        lock (trig)
        {
            trig.Id = id;
        }

        for (var round = 0; round <= interruptions; round++)
        {
            // 本轮结束：中断已触发且双方都已停下，或双方都已结束（续传之前的“已中断”状态不算）
            await UntilAsync(id, $"round {round} end", () =>
            {
                var sa = w.A.State(id);
                var sb = w.B.State(id);
                return (Fired() && Settled(sa) && Settled(sb)) || (sa?.IsTerminal() == true && sb?.IsTerminal() == true);
            });
            Note($"round={round} fired={Fired()} A={Describe(w.A, id)} B={Describe(w.B, id)}");
            if (w.A.State(id) != TaskState.Interrupted)
            {
                break;
            }

            // 中断期间：修改两端都未完成（接收方也未在校验）的源文件、删除临时文件
            for (var k = 0; k < refs.Count; k++)
            {
                var rs = w.B.TaskOf(id).Files[k].State;
                if (w.A.TaskOf(id).Files[k].State == FileState.Completed || rs == FileState.Completed || rs == FileState.Verifying)
                {
                    continue;
                }

                if (rnd.Next(5) == 0)
                {
                    var data = new byte[rnd.Next(0, 300_000)];
                    rnd.NextBytes(data);
                    w.A.Sources.Modify(refs[k], Bytes.Of(data), rnd.NextInt64(1, 1_000_000));
                    Note($"modify f{k}");
                }
            }

            if (rnd.Next(4) == 0)
            {
                foreach (var t in w.B.Storage.TempRefs)
                {
                    w.B.Storage.RemoveTemp(t);
                }

                Note("remove temps");
            }

            if (round + 1 < interruptions)
            {
                Arm();
            }
            else
            {
                lock (trig)
                {
                    trig.At = long.MaxValue;
                    trig.Fired = false;
                }
            }

            // 接收方可能已经完成（最后的 FILE_RESULT 在断开时丢失）：此时只有发送方能点击继续
            var byReceiver = rnd.Next(2) == 0 && w.B.State(id) == TaskState.Interrupted;
            Note($"resume by {(byReceiver ? "B" : "A")}");
            var res = await Kit.Within(byReceiver ? w.B.Engine.ResumeAsync(id) : w.A.Engine.ResumeAsync(id));
            Assert.True(res == new ResumeResult.Started(), $"resume: {res}\n{Log()}");
        }

        await UntilAsync(id, "completion", () => w.A.State(id) == TaskState.Completed && w.B.State(id) == TaskState.Completed);
        var got = w.B.Received();
        for (var k = 0; k < refs.Count; k++)
        {
            Assert.True(w.B.TaskOf(id).Files[k].State == FileState.Completed, $"f{k} {w.B.TaskOf(id).Files[k].State}\n{Log()}");
            Assert.True(got.TryGetValue($"f{k}", out var b) && b == w.A.Content(refs[k]), $"f{k} content differs\n{Log()}");
        }

        await Kit.WaitUntilAsync(() => w.B.Storage.TempRefs.Count == 0, "删除临时文件\n" + Log());
    }
}
