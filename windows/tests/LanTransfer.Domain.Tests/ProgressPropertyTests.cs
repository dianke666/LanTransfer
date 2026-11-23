using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Testing;
using Xunit;

namespace LanTransfer.Domain.Tests;

/// <summary>Property 27、28 的 C# 实现与示例测试，与 Kotlin 侧 ProgressPropertiesTest 对应。</summary>
public sealed class ProgressPropertyTests
{
    private const long MaxSize = 1L << 59;
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>参考模型：decimal 截断到 1 位小数，选使数值 ≥ 1 的最大单位。</summary>
    private static string RefFormat(long bytes)
    {
        var k = 4;
        while (k > 0 && (decimal)Math.Pow(1024, k) > bytes)
        {
            k--;
        }

        if (k == 0)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        var v = Math.Floor(bytes * 10m / (decimal)Math.Pow(1024, k)) / 10m;
        return v.ToString("0.0", CultureInfo.InvariantCulture) + " " + SizeUnits[k];
    }

    /// <summary>文件大小：均匀分布、单位边界 ±2、1 位小数的进位边界 ±1、小数值与固定样例。</summary>
    private static readonly Gen<long> SizeBytes = Gen.Frequency(
        (3, Gens.Choose64(0, MaxSize)),
        (3, from k in Gen.Choose(0, 5) from d in Gen.Choose(-2, 2) select Math.Clamp((1L << Math.Min(10 * k, 59)) + d, 0, MaxSize)),
        (3, from k in Gen.Choose(1, 4)
            from n in Gen.Choose(1, 1023)
            from tenth in Gen.Choose(0, 9)
            from d in Gen.Choose(-1, 1)
            let unit = 1L << (10 * k)
            let m = (n * 10L) + tenth
            select Math.Clamp((((m * unit) + 9) / 10) + d, 0, MaxSize)),
        (2, Gen.Choose(0, 5000).Select(i => (long)i)),
        (1, Gen.Elements(0L, 1023L, 1024L, 1_048_575L, MaxSize, MaxSize - 1)));

    private abstract record ProgressOp(long Dt);

    private sealed record ResetOp(long Dt, long Transferred) : ProgressOp(Dt);

    private sealed record SampleOp(long Dt, long Delta) : ProgressOp(Dt);

    /// <summary>TotalMode：0 → total = 0，1 → total = 当前值，2 → 当前值 + Extra，3 → Extra（可能小于当前值）。</summary>
    private sealed record SnapshotOp(long Dt, int TotalMode, long Extra, bool Completed) : ProgressOp(Dt);

    private static readonly Gen<long> DtGen = Gen.Frequency(
        (2, Gens.Choose64(0, 500)),
        (3, Gens.Choose64(500, 1500)),
        (1, Gens.Choose64(2990, 3010)),
        (1, Gen.Elements(0L, 1L, 2999L, 3000L, 3001L)),
        (1, Gens.Choose64(3000, 10_000)));

    private static readonly Gen<ProgressOp> OpGen = Gen.Frequency(
        (1, from dt in DtGen from v in Gen.Frequency((1, Gen.Constant(0L)), (2, Gens.Choose64(0, 1L << 40))) select (ProgressOp)new ResetOp(dt, v)),
        (6, from dt in DtGen
            from d in Gen.Frequency((1, Gen.Constant(0L)), (3, Gens.Choose64(0, 65_536)), (1, Gens.Choose64(0, 10_000_000)))
            select (ProgressOp)new SampleOp(dt, d)),
        (3, from dt in DtGen from m in Gen.Choose(0, 3) from e in Gens.Choose64(0, 1L << 41) from c in Gen.Elements(true, false)
            select (ProgressOp)new SnapshotOp(dt, m, e, c)));

    private static readonly Gen<ProgressOp[]> ProgressCase =
        from t0 in Gens.Choose64(0, 1_000_000)
        from v0 in Gens.Choose64(0, 1L << 40)
        from n in Gen.Choose(1, 60)
        from ops in OpGen.ArrayOf(n)
        select new ProgressOp[] { new ResetOp(t0, v0) }.Concat(ops).ToArray();

    /// <summary>参考实现：保留 reset 以来的全部样本，按定义直接计算。</summary>
    private sealed class RefProgress
    {
        private const long WindowMs = 3_000;
        private readonly List<(long Time, long Value)> _samples = [];
        private long _segmentStart;

        public long Current => _samples.Count > 0 ? _samples[^1].Value : 0;

        public void Reset(long now, long v)
        {
            _segmentStart = now;
            _samples.Clear();
            _samples.Add((now, v));
        }

        public void Sample(long now, long v)
        {
            if (_samples.Count == 0)
            {
                Reset(now, v);
            }
            else
            {
                _samples.Add((now, v));
            }
        }

        public Progress Snapshot(long now, long total, bool completed)
        {
            var start = Math.Max(now - WindowMs, _segmentStart);

            // 窗口起点时刻的已传输字节 = 起点之前（含）最后一个样本的值
            var before = _samples.Where(s => s.Time <= start).ToList();
            var @base = before.Count > 0 ? before[^1].Value : Current;
            var elapsed = now - start;
            var rate = elapsed > 0 ? (long)((Int128)(Current - @base) * 1000 / elapsed) : 0;
            var remaining = Math.Max(total - Current, 0);
            long? eta = rate == 0 ? null : (long)(((Int128)remaining + rate - 1) / rate);
            var percent = total == 0 ? (completed ? 100 : 0) : (int)((Int128)Math.Min(Current, total) * 100 / total);
            return new Progress(percent, Current, total, rate, eta);
        }
    }

    private static readonly CultureInfo[] Cultures =
        [CultureInfo.InvariantCulture, new("de-DE"), new("ar-EG"), new("hi-IN"), new("fr-FR"), new("fa-IR")];

    // Feature: lan-file-transfer, Property 27: 文件大小格式化
    // Validates: Requirements 15.1
    [PbtProperty(1000)]
    public Property Property27_SizeFormatting() =>
        Prop.ForAll(Arb.From(from b in SizeBytes from c in Gen.Elements(Cultures) select (b, c)), x =>
        {
            var expected = RefFormat(x.b);
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = x.c;
                Assert.Equal(expected, SizeFormatter.Format(x.b));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        });

    [Fact]
    public void Examples_SizeFormatter()
    {
        Assert.Equal("0 B", SizeFormatter.Format(0));
        Assert.Equal("1023 B", SizeFormatter.Format(1023));
        Assert.Equal("1.0 KB", SizeFormatter.Format(1024));
        Assert.Equal("1023.9 KB", SizeFormatter.Format(1_048_575));
        Assert.Equal("1.0 MB", SizeFormatter.Format(1_048_576));
        Assert.Equal("1.5 KB", SizeFormatter.Format(1536));
        Assert.Equal("1.0 TB", SizeFormatter.Format(1L << 40));
        Assert.Equal("524288.0 TB", SizeFormatter.Format(MaxSize));
    }

    // Feature: lan-file-transfer, Property 28: 进度计算
    // Validates: Requirements 18.1
    [PbtProperty(1000)]
    public Property Property28_ProgressCalculation() =>
        Prop.ForAll(Arb.From(ProgressCase), ops =>
        {
            var calc = new ProgressCalculator();
            var model = new RefProgress();
            var now = 0L;
            foreach (var op in ops)
            {
                now += op.Dt;
                switch (op)
                {
                    case ResetOp r:
                        calc.Reset(now, r.Transferred);
                        model.Reset(now, r.Transferred);
                        break;
                    case SampleOp s:
                        var v = model.Current + s.Delta;
                        calc.Sample(now, v);
                        model.Sample(now, v);
                        break;
                    case SnapshotOp snap:
                        var total = snap.TotalMode switch
                        {
                            0 => 0,
                            1 => model.Current,
                            2 => model.Current + snap.Extra,
                            _ => snap.Extra,
                        };
                        var got = calc.Snapshot(now, total, snap.Completed);
                        Assert.Equal(model.Snapshot(now, total, snap.Completed), got);
                        Assert.InRange(got.Percent, 0, 100);
                        Assert.Equal(got.BytesPerSec == 0, got.EtaSec is null);
                        break;
                }
            }
        });

    [Fact]
    public void Examples_ProgressRateAndEta()
    {
        var c = new ProgressCalculator();
        c.Reset(0, 1000);
        Assert.Equal(new Progress(10, 1000, 10_000, 0, null), c.Snapshot(0, 10_000, false));
        c.Sample(1000, 2000);
        Assert.Equal(new Progress(20, 2000, 10_000, 1000, 8), c.Snapshot(1000, 10_000, false));
        c.Sample(4000, 3500);

        // 窗口 [1000, 4000]：增量 1500 B / 3 s
        Assert.Equal(new Progress(35, 3500, 10_000, 500, 13), c.Snapshot(4000, 10_000, false));
        Assert.Equal(new Progress(35, 3500, 10_000, 0, null), c.Snapshot(8000, 10_000, false));
        Assert.Equal(0, c.Snapshot(8000, 0, false).Percent);
        Assert.Equal(100, c.Snapshot(8000, 0, true).Percent);
    }
}
