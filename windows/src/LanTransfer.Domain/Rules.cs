using System.Security.Cryptography;
using LanTransfer.Protocol;

namespace LanTransfer.Domain;

// C3.5 规则（需求 8、14、15、17），与 Kotlin 侧 Rules.kt 对应。

/// <summary>随机数端口（C5 的 Rng，对应 Kotlin 的 <c>Rng</c>）：生产环境为 RandomNumberGenerator，测试时注入确定的序列。</summary>
public interface IRng
{
    byte[] NextBytes(int n);
}

/// <summary>一次 Issue 产生的 Pairing_Token。</summary>
public sealed record PairingToken(Bytes16 Value, long IssuedAtMono);

/// <summary>校验通过后得到的租约；只有最近一次校验通过时得到的租约能 MarkUsed / Release。</summary>
public sealed class TokenLease
{
    internal TokenLease(long id) => Id = id;

    internal long Id { get; }

    public override string ToString() => $"TokenLease({Id})";
}

public abstract record TokenCheck
{
    public sealed record Ok(TokenLease Lease) : TokenCheck;

    public sealed record Invalid : TokenCheck;
}

/// <summary>
/// Pairing_Token 注册表（需求 8.6、8.7、8.9，设计 C3.5），与 Kotlin 侧 <c>PairingTokenRegistry</c> 对应。令牌有效的条件：
/// 是最近一次 Issue 的令牌、发出不足 5 min、未被使用、失败次数少于 5。校验通过的令牌进入“使用中”，直到 MarkUsed 或 Release；
/// 使用中的令牌再次被校验时返回 Invalid。每次 Invalid 使失败计数加 1。令牌比较使用常数时间比较。时间为单调时钟毫秒。
/// </summary>
public sealed class PairingTokenRegistry(IRng rng)
{
    public const int TokenSize = 16;
    public const long LifetimeMs = 5 * 60_000L;
    public const int MaxFailures = 5;

    private byte[]? _current;
    private long _issuedAt;
    private int _failures;
    private bool _used;
    private long? _activeLease;
    private long _nextLeaseId = 1;

    /// <summary>生成新令牌，替换掉之前的令牌（失败计数、使用状态一并重置）。</summary>
    public PairingToken Issue(long nowMono)
    {
        var v = rng.NextBytes(TokenSize);
        if (v.Length != TokenSize)
        {
            throw new InvalidOperationException($"IRng returned {v.Length} bytes");
        }

        _current = (byte[])v.Clone();
        _issuedAt = nowMono;
        _failures = 0;
        _used = false;
        _activeLease = null;
        return new PairingToken(Bytes16.Of(v), nowMono);
    }

    public TokenCheck Check(ReadOnlySpan<byte> token, long nowMono)
    {
        // FixedTimeEquals 在长度不同时直接返回 false
        var matches = _current is not null && CryptographicOperations.FixedTimeEquals(_current, token);
        if (matches && _activeLease is null && IsUsable(nowMono))
        {
            var lease = new TokenLease(_nextLeaseId++);
            _activeLease = lease.Id;
            return new TokenCheck.Ok(lease);
        }

        _failures++;
        return new TokenCheck.Invalid();
    }

    /// <summary>配对完成：令牌作废。</summary>
    public void MarkUsed(TokenLease lease)
    {
        if (_activeLease == lease.Id)
        {
            _used = true;
            _activeLease = null;
        }
    }

    /// <summary>配对中断：令牌回到可用状态（仍受 5 min 与失败次数约束）。</summary>
    public void Release(TokenLease lease)
    {
        if (_activeLease == lease.Id)
        {
            _activeLease = null;
        }
    }

    /// <summary>当前令牌已失效（或从未 Issue）→ 调用方在 2 s 内重新 Issue（8.7）。使用中不算失效。</summary>
    public bool NeedsRefresh(long nowMono) => _current is null || !IsUsable(nowMono);

    private bool IsUsable(long nowMono) => !_used && _failures < MaxFailures && nowMono - _issuedAt < LifetimeMs;
}

/// <summary>选入待发送列表的一项（文件选择器、拖放或分享）。</summary>
public sealed record PickedItem(SourceRef Ref, bool IsDirectory);

/// <summary>
/// <see cref="PendingListRules.Add"/> 的结果。List 为加入后的列表（TooMany 时等于原列表）；FoldersSkipped 时提示“暂不支持发送文件夹”（14.9）；
/// TooMany 时提示单次最多发送 1000 个文件（14.8）。
/// </summary>
public sealed record AddResult(ValueList<SourceRef> List, bool FoldersSkipped, bool TooMany);

/// <summary>待发送列表规则（需求 14.1、14.2、14.8、14.9），与 Kotlin 侧 <c>PendingListRules</c> 对应。</summary>
public static class PendingListRules
{
    public const int MaxFiles = ProtocolConstants.MaxFilesPerOffer;

    public static AddResult Add(IReadOnlyList<SourceRef> current, IReadOnlyList<PickedItem> incoming)
    {
        var files = incoming.Where(i => !i.IsDirectory).Select(i => i.Ref).ToList();
        var foldersSkipped = files.Count != incoming.Count;
        return current.Count + files.Count > MaxFiles
            ? new AddResult(new ValueList<SourceRef>(current), foldersSkipped, TooMany: true)
            : new AddResult(new ValueList<SourceRef>(current.Concat(files)), foldersSkipped, TooMany: false);
    }
}

/// <summary>接收目录状态（ReceiveStorage.Probe，15.7）。</summary>
public enum DirStatus
{
    Usable,
    CannotCreate,
    NotWritable,
}

public abstract record PrecheckDecision
{
    /// <summary>回复 FILE_OFFER_REPLY 的拒绝码：InvalidOffer、CannotSave 或 NoSpace。</summary>
    public sealed record Reject(OfferDecision Reply) : PrecheckDecision;

    public sealed record AutoAccept : PrecheckDecision;

    public sealed record AskUser : PrecheckDecision;
}

/// <summary>File_Offer 预检（需求 15.1、15.2、15.5、15.7，设计 C3.5），与 Kotlin 侧 <c>OfferPrecheck</c> 对应。</summary>
public static class OfferPrecheck
{
    /// <summary>
    /// 判定顺序：语义无效 → InvalidOffer；目录不可用 → CannotSave；可用空间已知且小于文件大小之和 → NoSpace；
    /// 已开启自动接收 → AutoAccept；其余 → AskUser。<paramref name="freeBytes"/> 为 null 表示无法获知可用空间，此时不做空间检查。
    /// </summary>
    public static PrecheckDecision Decide(FileOffer offer, DirStatus dir, long? freeBytes, bool autoAccept)
    {
        if (!IsSemanticallyValid(offer))
        {
            return new PrecheckDecision.Reject(OfferDecision.InvalidOffer);
        }

        if (dir != DirStatus.Usable)
        {
            return new PrecheckDecision.Reject(OfferDecision.CannotSave);
        }

        if (freeBytes is { } free && free < TotalSize(offer))
        {
            return new PrecheckDecision.Reject(OfferDecision.NoSpace);
        }

        return autoAccept ? new PrecheckDecision.AutoAccept() : new PrecheckDecision.AskUser();
    }

    /// <summary>语义校验（规范 FILE_OFFER）：文件数 1–1000、每个文件 size ≤ 1 TiB、mime 为 ≤ 255 字节的可打印 ASCII。</summary>
    public static bool IsSemanticallyValid(FileOffer offer) =>
        offer.Files.Count is >= 1 and <= ProtocolConstants.MaxFilesPerOffer &&
        offer.Files.All(f => f.Size <= ProtocolConstants.MaxFileSize && IsValidMime(f.Mime));

    /// <summary>可打印 ASCII（20–7E），1 个字符即 1 个字节，最多 255 个。</summary>
    public static bool IsValidMime(string mime) => mime.Length <= ProtocolConstants.MaxMimeBytes && mime.All(c => c is >= ' ' and <= '~');

    /// <summary>文件大小之和；前置条件：语义有效（因此不会溢出：1000 × 1 TiB &lt; 2^50）。</summary>
    public static long TotalSize(FileOffer offer) => offer.Files.Sum(f => (long)f.Size);
}

/// <summary>源文件的当前大小与修改时间（SourceFiles.Stat，17.5）。</summary>
public sealed record SourceStat(long Size, long? Mtime);

/// <summary>续传计划中的一项；Size / Mtime 只在因源文件变化而 Restart 时给出。</summary>
public sealed record ResumePlanEntry(int Index, ResumeAction Action, long Start, long? Size = null, long? Mtime = null)
{
    public ResumeFilePlan ToWire() => new((uint)Index, Action, (ulong)Start, Size is { } s ? (ulong)s : null, Mtime);
}

/// <summary>续传计划（需求 17.4、17.5，设计 C1.13、C3.5），与 Kotlin 侧 <c>ResumePlanner</c> 对应。</summary>
public static class ResumePlanner
{
    /// <summary>
    /// 按 <paramref name="task"/> 中文件的顺序，为每个文件恰好给出 1 项；<paramref name="stats"/> 与 task.Files 一一对应，null 表示无法获取 stat
    /// （按未变化处理，之后打开失败时按 14.7 报告读取失败）。RESUME_STATE 中缺少某个文件时按 None 处理。
    /// 发送方记录为终态或接收方报告 Completed / Terminal → Skip；源文件大小或修改时间与记录不同 → Restart(0, 新 size, 新 mtime)；
    /// None → Continue(0)；Partial 且 persisted ≤ size → Continue(persisted)；persisted &gt; size → Restart(0)。
    /// </summary>
    public static IReadOnlyList<ResumePlanEntry> Plan(TransferTask task, IReadOnlyList<ResumeFileState> state, IReadOnlyList<SourceStat?> stats)
    {
        if (stats.Count != task.Files.Count)
        {
            throw new ArgumentException("stats must match task files", nameof(stats));
        }

        var byIndex = new Dictionary<uint, ResumeFileState>();
        foreach (var s in state)
        {
            byIndex.TryAdd(s.FileIndex, s);
        }

        var result = new List<ResumePlanEntry>(task.Files.Count);
        for (var k = 0; k < task.Files.Count; k++)
        {
            var f = task.Files[k];
            var r = byIndex.GetValueOrDefault((uint)f.Index);
            var stat = stats[k];
            ResumePlanEntry entry;
            if (f.State.IsTerminal() || r?.State is ResumeFileStatus.Completed or ResumeFileStatus.Terminal)
            {
                entry = new ResumePlanEntry(f.Index, ResumeAction.Skip, 0);
            }
            else if (stat is not null && (stat.Size != f.Size || stat.Mtime != f.Mtime))
            {
                entry = new ResumePlanEntry(f.Index, ResumeAction.Restart, 0, stat.Size, stat.Mtime);
            }
            else if (r is null || r.State == ResumeFileStatus.None)
            {
                entry = new ResumePlanEntry(f.Index, ResumeAction.Continue, 0);
            }
            else if (r.Persisted <= (ulong)f.Size)
            {
                entry = new ResumePlanEntry(f.Index, ResumeAction.Continue, (long)r.Persisted);
            }
            else
            {
                entry = new ResumePlanEntry(f.Index, ResumeAction.Restart, 0);
            }

            result.Add(entry);
        }

        return result;
    }
}

/// <summary>一个临时文件；TransferId 为 null 表示无法从名称或数据库确定所属任务。LastWriteUtcMs 的取法见设计 M4。</summary>
public sealed record TempInfo(TempRef Ref, TransferId? TransferId, long LastWriteUtcMs);

/// <summary>过期临时文件选择（需求 17.7），与 Kotlin 侧 <c>TempFileSelector</c> 对应。</summary>
public static class TempFileSelector
{
    public const long MaxAgeMs = 24L * 60 * 60 * 1000;

    /// <summary>不属于进行中任务（<paramref name="active"/>）且最后写入时间距 <paramref name="nowWall"/> 超过 24 h 的临时文件，保持输入顺序。</summary>
    public static IReadOnlyList<TempInfo> Select(IEnumerable<TempInfo> temps, IReadOnlySet<TransferId> active, long nowWall) =>
        temps.Where(t => (t.TransferId is not { } id || !active.Contains(id)) && nowWall - t.LastWriteUtcMs > MaxAgeMs).ToList();
}
