namespace LanTransfer.Domain;

/// <summary>文件大小格式化（需求 15.1，设计 C3.4，Q6），与 Kotlin 侧 <c>SizeFormatter</c> 对应：1024 进制，B 以外截断到 1 位小数，与区域设置无关。</summary>
public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>前置条件：0 ≤ bytes ≤ 2^59。</summary>
    public static string Format(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes, 1L << 59);
        var k = 0;
        while (k < Units.Length - 1 && bytes >= 1L << (10 * (k + 1)))
        {
            k++;
        }

        // 只做整数运算并用不变区域设置格式化，结果与当前区域设置无关
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (k == 0)
        {
            return bytes.ToString(inv) + " B";
        }

        var tenths = bytes * 10 / (1L << (10 * k));
        return (tenths / 10).ToString(inv) + "." + (tenths % 10).ToString(inv) + " " + Units[k];
    }
}

/// <summary>进度快照（需求 18.1）。<see cref="EtaSec"/> 为 null 表示剩余时间未知（速率为 0）。</summary>
public sealed record Progress(int Percent, long Transferred, long Total, long BytesPerSec, long? EtaSec);

/// <summary>
/// 进度计算（设计 C3.4），与 Kotlin 侧 <c>ProgressCalculator</c> 对应：速率 = 最近 windowMs 内的字节增量 ÷ 窗口时长，
/// reset 之后窗口相应缩短；时间由调用方传入（单调时钟毫秒）。
/// </summary>
public sealed class ProgressCalculator(long windowMs = 3_000)
{
    private readonly List<(long Time, long Value)> _samples = [];
    private long _resetAt;

    /// <summary>开始或续传时调用：已有字节不计入速率。</summary>
    public void Reset(long nowMono, long transferred)
    {
        _samples.Clear();
        _samples.Add((nowMono, transferred));
        _resetAt = nowMono;
    }

    public void Sample(long nowMono, long transferred)
    {
        if (_samples.Count == 0)
        {
            Reset(nowMono, transferred);
            return;
        }

        _samples.Add((nowMono, transferred));

        // 只保留窗口起点之前的最后一个样本
        var start = Math.Max(nowMono - windowMs, _resetAt);
        var drop = 0;
        while (_samples.Count - drop >= 2 && _samples[drop + 1].Time <= start)
        {
            drop++;
        }

        _samples.RemoveRange(0, drop);
    }

    public Progress Snapshot(long nowMono, long total, bool completed)
    {
        var current = _samples.Count > 0 ? _samples[^1].Value : 0L;
        var start = Math.Max(nowMono - windowMs, _resetAt);
        var @base = _samples.Count > 0 ? _samples[0].Value : current;
        foreach (var (time, value) in _samples)
        {
            if (time <= start)
            {
                @base = value;
            }
        }

        var elapsed = nowMono - start;
        var rate = elapsed > 0 && current > @base ? MulDiv(current - @base, 1000, elapsed) : 0L;
        var remaining = Math.Max(total - current, 0L);

        // ⌈remaining ÷ rate⌉，写成不会溢出的形式
        long? eta = rate == 0 ? null : (remaining / rate) + (remaining % rate == 0 ? 0 : 1);
        var percent = total <= 0 ? (completed ? 100 : 0) : (int)MulDiv(Math.Clamp(current, 0L, total), 100, total);
        return new Progress(percent, current, total, rate, eta);
    }

    /// <summary>⌊a × b ÷ c⌋（a、b ≥ 0，c &gt; 0），用 128 位中间值避免溢出。</summary>
    private static long MulDiv(long a, long b, long c) => (long)((Int128)a * b / c);
}
