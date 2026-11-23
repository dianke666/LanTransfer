using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

/// <summary>生成的源文件：名称取自文件名生成器（含需要清理的名称）。</summary>
public sealed record GeneratedFile(string Name, Bytes Content, long? Mtime, string Mime)
{
    public override string ToString() => $"GeneratedFile(name={Gens.Escape(Name)}, size={Content.Length}, mtime={Mtime}, mime={Mime})";
}

// 引擎事件序列生成器（任务 14.2），与 Kotlin 侧 EngineGens.kt 同名、分布相近：文件集合与内容、中断位置、存储失败计划、前后台切换。
public static partial class Gens
{
    private const int Chunk = ProtocolConstants.MaxChunk;

    /// <summary>文件长度：偏向 0 字节、64 KiB 块边界两侧和小文件。</summary>
    public static Gen<int> FileSize(int max = 5 * Chunk) => Gen.Frequency(
        (2, Gen.Elements(0, 1, Chunk - 1, Chunk, Chunk + 1, 2 * Chunk).Select(n => Math.Min(n, max))),
        (3, Gen.Choose(0, Math.Min(4_096, max))),
        (1, Gen.Choose(0, max)));

    /// <summary>文件内容：长度取 <see cref="FileSize"/>，内容由种子决定（生成大文件时比逐字节生成快得多）。</summary>
    public static Gen<Bytes> FileContent(int max = 5 * Chunk) =>
        from n in FileSize(max)
        from seed in AnyUInt()
        select SeededBytes(n, unchecked((int)seed));

    public static Gen<GeneratedFile> GeneratedFileGen(int max = 5 * Chunk) =>
        from name in RawFileNameGen()
        from content in FileContent(max)
        from mtime in Gen.Frequency((1, Gen.Constant<long?>(null)), (4, Choose64(1_600_000_000_000L, 2_000_000_000_000L).Select(v => (long?)v)))
        from mime in Gen.Elements("application/octet-stream", "text/plain", "image/jpeg", "application/pdf")
        select new GeneratedFile(name.Length == 0 ? "f" : name, content, mtime, mime);

    /// <summary>1–<paramref name="maxFiles"/> 个源文件。</summary>
    public static Gen<GeneratedFile[]> SourceFileSet(int maxFiles = 5, int max = 5 * Chunk) =>
        Gen.Choose(1, maxFiles).SelectMany(n => GeneratedFileGen(max).ArrayOf(n));

    /// <summary>中断位置：[0, total] 内 0–<paramref name="maxCount"/> 个字节偏移，升序（可重复，表示同一位置多次中断）。</summary>
    public static Gen<long[]> InterruptionPoints(long total, int maxCount = 3)
    {
        var point = Gen.Frequency((3, Choose64(0, total)), (1, Gen.Elements(0L, total)));
        return Gen.Choose(0, maxCount).SelectMany(n => point.ArrayOf(n)).Select(a => a.Order().ToArray());
    }

    /// <summary>存储写入失败计划：第几次写操作失败（从 0 计）。</summary>
    public static Gen<IReadOnlySet<int>> FailureSchedule(int maxOps = 30) => Gen.Frequency(
        (3, Gen.Constant<IReadOnlySet<int>>(new HashSet<int>())),
        (2, Gen.Choose(1, 4).SelectMany(n => Gen.Choose(0, maxOps - 1).ArrayOf(n)).Select(a => (IReadOnlySet<int>)a.ToHashSet())));

    /// <summary>前后台切换：（距上次切换的毫秒数, 切换后是否在后台）。</summary>
    public static Gen<(long DelayMs, bool Background)[]> BackgroundToggles(int maxCount = 6)
    {
        var toggle = from dt in Choose64(0, 40_000) from bg in Gen.Elements(false, true) select (dt, bg);
        return Gen.Choose(0, maxCount).SelectMany(n => toggle.ArrayOf(n));
    }

    private static Bytes SeededBytes(int n, int seed)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return Bytes.Of(b);
    }
}
