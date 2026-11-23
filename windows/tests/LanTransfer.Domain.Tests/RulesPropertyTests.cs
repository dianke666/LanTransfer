using System.Numerics;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>Property 30–34 的 C# 实现与示例测试，与 Kotlin 侧 RulesPropertiesTest 对应。</summary>
public sealed partial class RulesPropertyTests
{
    /// <summary>由种子决定的 IRng（测试用）。</summary>
    private sealed class SeededRng(int seed) : IRng
    {
        private readonly Random _r = new(seed);

        public byte[] NextBytes(int n)
        {
            var b = new byte[n];
            _r.NextBytes(b);
            return b;
        }
    }

    // ---- Property 30：令牌注册表 ----

    private abstract record TokenOp;

    private sealed record IssueOp : TokenOp;

    /// <summary>Which：0 当前令牌，1 上一个令牌，2 随机 16 字节，3 当前令牌改 1 个字节，4 当前令牌截短，5 空串。</summary>
    private sealed record CheckOp(int Which, int FlipAt) : TokenOp;

    private sealed record MarkUsedOp(int K) : TokenOp;

    private sealed record ReleaseOp(int K) : TokenOp;

    private sealed record AdvanceOp(long Dt) : TokenOp;

    private static readonly Gen<TokenOp> TokenOpGen = Gen.Frequency(
        (2, Gen.Constant((TokenOp)new IssueOp())),
        (6, from w in Gen.Frequency((6, Gen.Constant(0)), (1, Gen.Choose(1, 5))) from f in Gen.Choose(0, 15) select (TokenOp)new CheckOp(w, f)),
        (2, Gen.Choose(0, 7).Select(k => (TokenOp)new MarkUsedOp(k))),
        (2, Gen.Choose(0, 7).Select(k => (TokenOp)new ReleaseOp(k))),
        (3, Gen.Frequency(
                (3, Gens.Choose64(0, 2_000)),
                (2, Gens.Choose64(60_000, 120_000)),
                (1, Gen.Elements(1L, 299_999L, 300_000L, 300_001L)),
                (1, Gens.Choose64(300_000, 1_000_000)))
            .Select(dt => (TokenOp)new AdvanceOp(dt))));

    /// <summary>参考模型：直接按 8.6 的四个条件判定。</summary>
    private sealed class RefRegistry(int seed)
    {
        private readonly Random _rnd = new(seed);
        private long _issuedAt;
        private int _failures;
        private bool _used;
        private int? _activeLease;
        private int _leaseCount;

        public List<byte[]> Issued { get; } = [];

        public byte[] Issue(long now)
        {
            var v = new byte[16];
            _rnd.NextBytes(v);
            Issued.Add(v);
            _issuedAt = now;
            _failures = 0;
            _used = false;
            _activeLease = null;
            return v;
        }

        private bool Valid(long now) => Issued.Count > 0 && !_used && _failures < 5 && now - _issuedAt < 300_000;

        /// <summary>返回租约编号或 null（Invalid）。</summary>
        public int? Check(byte[] token, long now)
        {
            if (Issued.Count > 0 && token.AsSpan().SequenceEqual(Issued[^1]) && Valid(now) && _activeLease is null)
            {
                _activeLease = _leaseCount;
                return _leaseCount++;
            }

            _failures++;
            return null;
        }

        public void MarkUsed(int k)
        {
            if (_activeLease == k)
            {
                _used = true;
                _activeLease = null;
            }
        }

        public void Release(int k)
        {
            if (_activeLease == k)
            {
                _activeLease = null;
            }
        }

        public bool NeedsRefresh(long now) => !Valid(now);
    }

    // Feature: lan-file-transfer, Property 30: 配对令牌注册表
    // Validates: Requirements 8.6, 8.7, 8.9
    [PbtProperty(1000)]
    public Property Property30_PairingTokenRegistry() =>
        Prop.ForAll(Arb.From(from seed in Gen.Choose(0, int.MaxValue - 1) from n in Gen.Choose(1, 60) from ops in TokenOpGen.ArrayOf(n) select (seed, ops)), x =>
        {
            var reg = new PairingTokenRegistry(new SeededRng(x.seed));
            var model = new RefRegistry(x.seed);
            var leases = new List<TokenLease>();
            var now = 1_000_000L;
            Assert.True(reg.NeedsRefresh(now));
            foreach (var op in x.ops)
            {
                switch (op)
                {
                    case IssueOp:
                    {
                        var t = reg.Issue(now);
                        Assert.Equal(model.Issue(now), t.Value.ToArray());
                        Assert.Equal(now, t.IssuedAtMono);
                        break;
                    }

                    case CheckOp c:
                    {
                        var cur = model.Issued.Count > 0 ? model.Issued[^1] : new byte[16];
                        byte[] token = c.Which switch
                        {
                            0 => cur,
                            1 => model.Issued.Count >= 2 ? model.Issued[^2] : Enumerable.Repeat((byte)7, 16).ToArray(),
                            2 => new SeededRng(c.FlipAt + x.seed).NextBytes(16),
                            3 => FlipByte(cur, c.FlipAt),
                            4 => cur[..c.FlipAt],
                            _ => [],
                        };
                        var expected = model.Check(token, now);
                        switch (reg.Check(token, now))
                        {
                            case TokenCheck.Ok ok:
                                Assert.Equal((int?)leases.Count, expected);
                                leases.Add(ok.Lease);
                                break;
                            default:
                                Assert.Null(expected);
                                break;
                        }

                        break;
                    }

                    case MarkUsedOp m when leases.Count > 0:
                        reg.MarkUsed(leases[m.K % leases.Count]);
                        model.MarkUsed(m.K % leases.Count);
                        break;
                    case ReleaseOp r when leases.Count > 0:
                        reg.Release(leases[r.K % leases.Count]);
                        model.Release(r.K % leases.Count);
                        break;
                    case AdvanceOp a:
                        now += a.Dt;
                        break;
                }

                Assert.Equal(model.NeedsRefresh(now), reg.NeedsRefresh(now));
            }
        });

    private static byte[] FlipByte(byte[] b, int at)
    {
        var c = (byte[])b.Clone();
        c[at] ^= 0x01;
        return c;
    }

    // ---- Property 31：待发送列表 ----

    private static readonly Gen<SourceRef> RefGen = Gen.Choose(0, 5000).Select(i => new SourceRef($"C:\\files\\{i}"));

    private static readonly Gen<PickedItem> PickedGen =
        from r in RefGen from d in Gen.Frequency((4, Gen.Constant(false)), (1, Gen.Constant(true))) select new PickedItem(r, d);

    private static readonly Gen<SourceRef[]> CurrentListGen = Gen.Frequency(
        (3, Gen.Choose(0, 20).SelectMany(n => RefGen.ArrayOf(n))),
        (2, Gen.Choose(980, 1000).Select(n => Enumerable.Range(0, n).Select(i => new SourceRef($"C:\\c\\{i}")).ToArray())),
        (1, Gen.Choose(0, 1000).SelectMany(n => RefGen.ArrayOf(n))));

    private static readonly Gen<PickedItem[]> IncomingGen = Gen.Frequency(
        (4, Gen.Choose(0, 30).SelectMany(n => PickedGen.ArrayOf(n))),
        (1, Gen.Choose(990, 1010).SelectMany(n => PickedGen.ArrayOf(n))));

    // Feature: lan-file-transfer, Property 31: 待发送列表规则
    // Validates: Requirements 14.1, 14.2, 14.8, 14.9
    [PbtProperty(1000)]
    public Property Property31_PendingListRules() =>
        Prop.ForAll(Arb.From(from c in CurrentListGen from i in IncomingGen select (c, i)), x =>
        {
            var files = x.i.Where(p => !p.IsDirectory).Select(p => p.Ref).ToList();
            var hasFolder = x.i.Any(p => p.IsDirectory);
            var expected = x.c.Length + files.Count > 1000
                ? new AddResult(new ValueList<SourceRef>(x.c), hasFolder, true)
                : new AddResult(new ValueList<SourceRef>(x.c.Concat(files)), hasFolder, false);
            Assert.Equal(expected, PendingListRules.Add(x.c, x.i));
        });

    // ---- Property 32：Offer 预检 ----

    private const ulong Tib = ProtocolConstants.MaxFileSize;

    private static readonly string[] BadMimes = [new string('a', 256), "text/plain\0", "图片/jpeg", "\u007F", "text\tplain", "é"];

    private static readonly string[] GoodMimes = ["application/octet-stream", "text/plain", "image/jpeg", "video/mp4", "application/pdf", "", new string('a', 255), "~ !"];

    private static readonly Gen<ulong> SizeGen = Gen.Frequency(
        (4, Gens.Choose64(0, 1L << 20).Select(v => (ulong)v)),
        (2, Gen.Elements(0UL, Tib, Tib + 1, ulong.MaxValue)),
        (1, Gens.Choose64(0, (long)Tib).Select(v => (ulong)v)));

    private static readonly Gen<FileEntry> EntryGen =
        from s in SizeGen
        from m in Gen.Frequency((6, Gen.Elements(GoodMimes)), (1, Gen.Elements(BadMimes)))
        from t in Gen.Frequency((2, Gens.Choose64(0, 2_000_000_000_000).Select(v => (long?)v)), (1, Gen.Constant((long?)null)))
        select new FileEntry("f", s, m, t);

    private static readonly TransferId OfferId = new(Bytes16.OfHex(string.Concat(Enumerable.Repeat("01", 16))));

    /// <summary>小 Offer 随机取每个字段；大 Offer（0、999–1001 个文件）大多全部有效，偶尔把其中 1 个换成无效项。</summary>
    private static readonly Gen<FileOffer> OfferGen = Gen.Frequency(
        (3, Gen.Choose(1, 6).SelectMany(n => EntryGen.ArrayOf(n)).Select(fs => new FileOffer(OfferId, new ValueList<FileEntry>(fs)))),
        (2, Gen.Choose(0, int.MaxValue - 1).Select(seed =>
        {
            var r = new Random(seed);
            int[] counts = [0, 1, 999, 1000, 1001];
            var n = counts[r.Next(counts.Length)];
            var files = Enumerable.Range(0, n).Select(i => new FileEntry($"f{i}", (ulong)r.NextInt64(0, 1L << 30), "text/plain")).ToArray();
            if (n > 0 && r.Next(3) == 0)
            {
                files[r.Next(n)] = r.Next(2) == 0 ? new FileEntry("x", Tib + 1, "text/plain") : new FileEntry("x", 1, BadMimes[r.Next(BadMimes.Length)]);
            }

            return new FileOffer(OfferId, new ValueList<FileEntry>(files));
        })));

    [GeneratedRegex(@"^[\x20-\x7E]{0,255}$")]
    private static partial Regex MimePattern();

    private static PrecheckDecision RefDecide(FileOffer offer, DirStatus dir, long? free, bool autoAccept)
    {
        var valid = offer.Files.Count is >= 1 and <= 1000 && offer.Files.All(f => f.Size <= Tib && MimePattern().IsMatch(f.Mime));
        var total = offer.Files.Aggregate(BigInteger.Zero, (acc, f) => acc + f.Size);
        return !valid ? new PrecheckDecision.Reject(OfferDecision.InvalidOffer)
            : dir != DirStatus.Usable ? new PrecheckDecision.Reject(OfferDecision.CannotSave)
            : free is { } fb && fb < total ? new PrecheckDecision.Reject(OfferDecision.NoSpace)
            : autoAccept ? new PrecheckDecision.AutoAccept()
            : new PrecheckDecision.AskUser();
    }

    // Feature: lan-file-transfer, Property 32: File_Offer 预检判定表
    // Validates: Requirements 15.1, 15.2, 15.5, 15.7
    [PbtProperty(1000)]
    public Property Property32_OfferPrecheck() =>
        Prop.ForAll(
            Arb.From(
                from o in OfferGen
                from d in Gen.Elements(DirStatus.Usable, DirStatus.CannotCreate, DirStatus.NotWritable)
                from m in Gen.Choose(0, 5)
                from fr in Gens.Choose64(0, 1L << 51)
                from a in Gen.Elements(true, false)
                select (o, d, m, fr, a)),
            x =>
            {
                var total = x.o.Files.Aggregate(BigInteger.Zero, (acc, f) => acc + f.Size);
                static long Clamp(BigInteger v) => (long)BigInteger.Min(BigInteger.Max(v, 0), long.MaxValue);
                long? free = x.m switch
                {
                    0 => null,
                    1 => Clamp(total),
                    2 => Clamp(total - 1),
                    3 => Clamp(total + 1),
                    4 => x.fr,
                    _ => 0,
                };
                Assert.Equal(RefDecide(x.o, x.d, free, x.a), OfferPrecheck.Decide(x.o, x.d, free, x.a));
            });

    // ---- Property 33：续传计划 ----

    private static readonly TransferId SampleTransferId = new(Bytes16.OfHex(string.Concat(Enumerable.Repeat("0f", 16))));
    private static readonly DeviceId SamplePeer = new(Bytes16.OfHex(string.Concat(Enumerable.Repeat("a0", 16))));

    private static TransferTask MakeTask(IEnumerable<TransferFile> files) =>
        new(SampleTransferId, SamplePeer, "对方", Direction.Send, TaskState.Resuming, null, null, new RecordTime(1_700_000_000_000, 480), 1_700_000_000_000, new ValueList<TransferFile>(files));

    private static TransferFile MakeFile(int index, long size, long? mtime, FileState state) =>
        new(SampleTransferId, index, index, $"f{index}", size, "application/octet-stream", mtime, state);

    private static readonly FileState[] AllFileStates = Enum.GetValues<FileState>();

    private static (TransferTask Task, List<ResumeFileState> State, List<SourceStat?> Stats) BuildResumeCase(int seed)
    {
        var r = new Random(seed);
        var n = r.Next(1, 9);
        var files = Enumerable.Range(0, n).Select(i =>
        {
            long[] sizes = [0, 1, 65_536, r.NextInt64(0, 1L << 40)];
            long? mtime = r.Next(4) == 0 ? null : r.NextInt64(1_600_000_000_000, 2_000_000_000_000);
            return MakeFile(i, sizes[r.Next(4)], mtime, AllFileStates[r.Next(AllFileStates.Length)]);
        }).ToList();
        var state = new List<ResumeFileState>();
        foreach (var f in files)
        {
            if (r.Next(8) == 0)
            {
                continue; // RESUME_STATE 中缺少该文件
            }

            var persisted = r.Next(6) switch
            {
                0 => 0,
                1 => f.Size,
                2 => Math.Max(f.Size - 1, 0),
                3 => f.Size + 1,
                4 => f.Size == 0 ? 0 : r.NextInt64(0, f.Size + 1),
                _ => r.NextInt64(0, long.MaxValue),
            };
            state.Add(new ResumeFileState((uint)f.Index, (ResumeFileStatus)r.Next(4), (ulong)persisted));
        }

        if (r.Next(5) == 0)
        {
            state.Add(new ResumeFileState((uint)(n + r.Next(5)), ResumeFileStatus.Partial, 1)); // 不存在的文件序号
        }

        var shuffled = state.OrderBy(_ => r.Next()).ToList();
        var stats = files.Select(SourceStat? (f) => r.Next(7) switch
        {
            0 => null,
            1 => new SourceStat(f.Size + 1 + r.NextInt64(0, 1000), f.Mtime),
            2 => new SourceStat(f.Size, (f.Mtime ?? 0) + 1),
            3 => new SourceStat(f.Size, f.Mtime is null ? 1_700_000_000_000 : null),
            _ => new SourceStat(f.Size, f.Mtime),
        }).ToList();
        return (MakeTask(files), shuffled, stats);
    }

    private static readonly HashSet<FileState> TerminalFileStates = [FileState.Completed, FileState.ReadFailed, FileState.ChecksumFailed];

    private static List<ResumePlanEntry> RefPlan(TransferTask task, List<ResumeFileState> state, List<SourceStat?> stats) =>
        task.Files.Select((f, k) =>
        {
            var r = state.FirstOrDefault(s => s.FileIndex == (uint)f.Index);
            var stat = stats[k];
            if (TerminalFileStates.Contains(f.State) || r?.State == ResumeFileStatus.Completed || r?.State == ResumeFileStatus.Terminal)
            {
                return new ResumePlanEntry(f.Index, ResumeAction.Skip, 0);
            }

            if (stat is not null && (stat.Size != f.Size || stat.Mtime != f.Mtime))
            {
                return new ResumePlanEntry(f.Index, ResumeAction.Restart, 0, stat.Size, stat.Mtime);
            }

            if (r is null || r.State == ResumeFileStatus.None)
            {
                return new ResumePlanEntry(f.Index, ResumeAction.Continue, 0);
            }

            return r.Persisted <= (ulong)f.Size
                ? new ResumePlanEntry(f.Index, ResumeAction.Continue, (long)r.Persisted)
                : new ResumePlanEntry(f.Index, ResumeAction.Restart, 0);
        }).ToList();

    // Feature: lan-file-transfer, Property 33: 续传计划
    // Validates: Requirements 17.4, 17.5
    [PbtProperty(1000)]
    public Property Property33_ResumePlan() =>
        Prop.ForAll(Arb.From(Gen.Choose(0, int.MaxValue - 1)), seed =>
        {
            var (task, state, stats) = BuildResumeCase(seed);
            var plan = ResumePlanner.Plan(task, state, stats);
            Assert.Equal(RefPlan(task, state, stats), plan);
            Assert.Equal(task.Files.Select(f => f.Index), plan.Select(e => e.Index));
            foreach (var e in plan)
            {
                Assert.Equal(new ResumeFilePlan((uint)e.Index, e.Action, (ulong)e.Start, e.Size is { } s ? (ulong)s : null, e.Mtime), e.ToWire());
                if (e.Action != ResumeAction.Restart)
                {
                    Assert.True(e.Size is null && e.Mtime is null);
                }
            }
        });

    // ---- Property 34：临时文件选择 ----

    private static (List<TempInfo> Temps, HashSet<TransferId> Active, long Now) BuildTempCase(int seed)
    {
        var r = new Random(seed);
        var pool = Enumerable.Range(0, 4).Select(_ =>
        {
            var b = new byte[16];
            r.NextBytes(b);
            return new TransferId(Bytes16.Of(b));
        }).ToArray();
        var now = r.NextInt64(1_600_000_000_000, 2_000_000_000_000);
        const long day = TempFileSelector.MaxAgeMs;
        var temps = Enumerable.Range(0, r.Next(0, 12)).Select(k =>
        {
            long[] edges = [day - 1, day, day + 1];
            var age = r.Next(5) switch
            {
                0 => edges[r.Next(3)],
                1 => -r.NextInt64(0, 10_000), // 写入时间在“未来”（时钟回拨）
                _ => r.NextInt64(0, 3 * day),
            };
            return new TempInfo(new TempRef($"tmp-{k}"), r.Next(6) == 0 ? null : pool[r.Next(4)], now - age);
        }).ToList();
        return (temps, pool.Where(_ => r.Next(2) == 0).ToHashSet(), now);
    }

    // Feature: lan-file-transfer, Property 34: 过期临时文件选择
    // Validates: Requirements 17.7
    [PbtProperty(1000)]
    public Property Property34_StaleTempSelection() =>
        Prop.ForAll(Arb.From(Gen.Choose(0, int.MaxValue - 1)), seed =>
        {
            var (temps, active, now) = BuildTempCase(seed);
            var expected = temps.Where(t =>
                !(t.TransferId.HasValue && active.Contains(t.TransferId.Value)) &&
                TimeSpan.FromMilliseconds(now - t.LastWriteUtcMs) > TimeSpan.FromHours(24)).ToList();
            Assert.Equal(expected, TempFileSelector.Select(temps, active, now));
        });

    // ---- 示例 ----

    private sealed class CountingRng : IRng
    {
        private int _counter;

        public byte[] NextBytes(int n)
        {
            var c = _counter++;
            return Enumerable.Range(0, n).Select(i => (byte)(c + i)).ToArray();
        }
    }

    [Fact]
    public void Examples_TokenLifetimeInUseFailuresAndRefresh()
    {
        var reg = new PairingTokenRegistry(new CountingRng());
        var t1 = reg.Issue(0).Value.ToArray();
        Assert.False(reg.NeedsRefresh(0));
        var lease = Assert.IsType<TokenCheck.Ok>(reg.Check(t1, 1_000)).Lease;
        Assert.IsType<TokenCheck.Invalid>(reg.Check(t1, 1_001)); // 使用中
        Assert.False(reg.NeedsRefresh(1_001));
        reg.Release(lease);
        var lease2 = Assert.IsType<TokenCheck.Ok>(reg.Check(t1, 2_000)).Lease;
        reg.MarkUsed(lease); // 旧租约不起作用
        Assert.False(reg.NeedsRefresh(2_000));
        reg.MarkUsed(lease2);
        Assert.True(reg.NeedsRefresh(2_000));
        Assert.IsType<TokenCheck.Invalid>(reg.Check(t1, 2_001));

        var t2 = reg.Issue(10_000).Value.ToArray();
        Assert.IsType<TokenCheck.Invalid>(reg.Check(t1, 10_001)); // 已被取代
        Assert.IsType<TokenCheck.Ok>(reg.Check(t2, 10_000 + 299_999));

        var t3 = reg.Issue(0).Value.ToArray();
        Assert.IsType<TokenCheck.Invalid>(reg.Check(t3, 300_000)); // 恰好 5 min 时已失效
        Assert.False(reg.NeedsRefresh(299_999)); // 失败 1 次、未满 5 min
        Assert.True(reg.NeedsRefresh(300_000));
        var t4 = reg.Issue(0).Value.ToArray();
        for (var i = 0; i < 4; i++)
        {
            Assert.IsType<TokenCheck.Invalid>(reg.Check(new byte[16], 1));
        }

        Assert.False(reg.NeedsRefresh(1));
        Assert.IsType<TokenCheck.Ok>(reg.Check(t4, 1)); // 失败 4 次时仍有效
        var t5 = reg.Issue(0).Value.ToArray();
        for (var i = 0; i < 5; i++)
        {
            reg.Check(new byte[16], 1);
        }

        Assert.True(reg.NeedsRefresh(1));
        Assert.IsType<TokenCheck.Invalid>(reg.Check(t5, 1));
    }

    [Fact]
    public void Examples_PendingListPrecheckResumePlanAndTemps()
    {
        var thousand = Enumerable.Range(0, 1000).Select(i => new SourceRef($"p{i}")).ToList();
        Assert.Equal(1000, PendingListRules.Add(thousand.Take(999).ToList(), [new PickedItem(new SourceRef("x"), false)]).List.Count);
        Assert.Equal(new AddResult(new ValueList<SourceRef>(thousand), false, true), PendingListRules.Add(thousand, [new PickedItem(new SourceRef("x"), false)]));
        Assert.Equal(
            new AddResult(new ValueList<SourceRef>([new SourceRef("a")]), true, false),
            PendingListRules.Add([], [new PickedItem(new SourceRef("d"), true), new PickedItem(new SourceRef("a"), false)]));

        static FileOffer Offer(params ulong[] sizes) => new(OfferId, new ValueList<FileEntry>(sizes.Select(s => new FileEntry("f", s, "text/plain"))));
        static FileOffer Many(int n) => new(OfferId, new ValueList<FileEntry>(Enumerable.Range(0, n).Select(_ => new FileEntry("f", 0, string.Empty))));
        Assert.Equal(new PrecheckDecision.AskUser(), OfferPrecheck.Decide(Offer(Tib), DirStatus.Usable, null, false));
        Assert.Equal(new PrecheckDecision.Reject(OfferDecision.InvalidOffer), OfferPrecheck.Decide(Offer(Tib + 1), DirStatus.Usable, null, false));
        Assert.Equal(new PrecheckDecision.Reject(OfferDecision.InvalidOffer), OfferPrecheck.Decide(Offer(), DirStatus.Usable, null, false));
        Assert.Equal(new PrecheckDecision.AutoAccept(), OfferPrecheck.Decide(Many(1000), DirStatus.Usable, 0, true));
        Assert.Equal(new PrecheckDecision.Reject(OfferDecision.InvalidOffer), OfferPrecheck.Decide(Many(1001), DirStatus.Usable, 0, true));
        Assert.Equal(new PrecheckDecision.Reject(OfferDecision.CannotSave), OfferPrecheck.Decide(Offer(10, 5), DirStatus.NotWritable, 0, true));
        Assert.Equal(new PrecheckDecision.Reject(OfferDecision.NoSpace), OfferPrecheck.Decide(Offer(10, 5), DirStatus.Usable, 14, true));
        Assert.Equal(new PrecheckDecision.AskUser(), OfferPrecheck.Decide(Offer(10, 5), DirStatus.Usable, 15, false));

        var task = MakeTask(
        [
            MakeFile(0, 100, 5, FileState.Completed),
            MakeFile(1, 100, 5, FileState.Transferring),
            MakeFile(2, 100, 5, FileState.WriteFailed),
            MakeFile(3, 100, 5, FileState.Waiting),
            MakeFile(4, 100, 5, FileState.Transferring),
        ]);
        List<ResumeFileState> state =
        [
            new(1, ResumeFileStatus.Partial, 40),
            new(2, ResumeFileStatus.Partial, 100),
            new(4, ResumeFileStatus.Partial, 101),
        ];
        List<SourceStat?> stats = [new(100, 5), new(100, 5), new(100, 5), new(100, 6), new(100, 5)];
        Assert.Equal(
            new[]
            {
                new ResumePlanEntry(0, ResumeAction.Skip, 0),
                new ResumePlanEntry(1, ResumeAction.Continue, 40),
                new ResumePlanEntry(2, ResumeAction.Continue, 100), // 写入失败的文件：临时文件已完整，start = size，重发 FILE_END
                new ResumePlanEntry(3, ResumeAction.Restart, 0, 100, 6),
                new ResumePlanEntry(4, ResumeAction.Restart, 0),
            },
            ResumePlanner.Plan(task, state, stats));

        const long day = TempFileSelector.MaxAgeMs;
        var a = new TempInfo(new TempRef("a"), OfferId, 0);
        var b = new TempInfo(new TempRef("b"), null, 1);
        Assert.Empty(TempFileSelector.Select([a, b], new HashSet<TransferId>(), day));
        Assert.Equal(new[] { a }, TempFileSelector.Select([a, b], new HashSet<TransferId>(), day + 1));
        Assert.Equal(new[] { b }, TempFileSelector.Select([a, b], new HashSet<TransferId> { OfferId }, day + 2));
    }

    [Fact]
    public void Examples_EnumCodesDirRefAndSettingsDefaults()
    {
        Assert.Equal(Enumerable.Range(10, 9), Enum.GetValues<TaskState>().Where(s => s.IsTerminal()).Select(s => (int)s));
        Assert.Equal(Enumerable.Range(0, 5), Enum.GetValues<TaskState>().Where(s => !s.IsTerminal()).Select(s => (int)s));
        Assert.Equal(new[] { 0, 1, 2, 3, 10, 11, 12 }, Enum.GetValues<FileState>().Select(s => (int)s));
        Assert.False(FileState.WriteFailed.IsTerminal());
        Assert.Equal(Enumerable.Range(1, 8), Enum.GetValues<InterruptReason>().Select(s => (int)s));
        Assert.Equal(Enumerable.Range(0, 4), Enum.GetValues<TextStatus>().Select(s => (int)s));
        Assert.True(TaskState.Running.IsActive() && TaskState.Resuming.IsActive() && !TaskState.Interrupted.IsActive());

        foreach (DirRef d in new DirRef[] { DirRef.Default.Instance, new DirRef.Tree("content://com.android.externalstorage.documents/tree/primary%3ADownload"), new DirRef.Path("D:\\接收\\LanTransfer") })
        {
            Assert.Equal(d, DirRef.Parse(d.Serialize()));
        }

        Assert.Equal("default", DirRef.Default.Instance.Serialize());
        Assert.Equal("path:C:\\x", new DirRef.Path("C:\\x").Serialize());
        Assert.Null(DirRef.Parse("tree:"));
        Assert.Null(DirRef.Parse("Default"));
        Assert.Null(DirRef.Parse(string.Empty));
        Assert.Equal(new Settings(DirRef.Default.Instance, AutoCopy: false, BackgroundReceive: false), new Settings());
    }
}
