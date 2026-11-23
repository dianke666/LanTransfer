using FsCheck;
using FsCheck.Fluent;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

// C1.9 每种消息类型的生成器，与 Kotlin 侧 MessageGens.kt 对应。
public static partial class Gens
{
    private static Gen<byte> ByteGen() => Gen.Choose(0, 255).Select(b => (byte)b);

    /// <summary>任意字节串，长度 0..<paramref name="maxLength"/>。</summary>
    public static Gen<Bytes> BytesGen(int maxLength = 64) =>
        Gen.Choose(0, maxLength).SelectMany(n => ByteGen().ArrayOf(n)).Select(b => Bytes.Of(b));

    /// <summary>恰好 <paramref name="size"/> 字节的字节串。</summary>
    public static Gen<Bytes> BytesOfSize(int size) => ByteGen().ArrayOf(size).Select(b => Bytes.Of(b));

    public static Gen<Bytes16> Bytes16Gen() => ByteGen().ArrayOf(16).Select(b => Bytes16.Of(b));

    public static Gen<TransferId> TransferIdGen() => Bytes16Gen().Select(b => new TransferId(b));

    public static Gen<MessageId> MessageIdGen() => Bytes16Gen().Select(b => new MessageId(b));

    private static Gen<uint> AnyUInt() => from hi in Gen.Choose(0, 0xFFFF) from lo in Gen.Choose(0, 0xFFFF) select ((uint)hi << 16) | (uint)lo;

    private static Gen<ulong> AnyULong() => from hi in AnyUInt() from lo in AnyUInt() select ((ulong)hi << 32) | lo;

    /// <summary>u32：偏向 0、小值与上界。</summary>
    public static Gen<uint> U32Gen() => Gen.Frequency(
        (3, Gen.Choose(0, 1000).Select(i => (uint)i)),
        (1, AnyUInt()),
        (1, Gen.Elements(0u, uint.MaxValue)));

    /// <summary>u64：偏向 0、2³²、1 TiB 等边界。</summary>
    public static Gen<ulong> U64Gen() => Gen.Frequency(
        (3, Gen.Choose(0, 1 << 20).Select(i => (ulong)i)),
        (2, Gen.Elements(0UL, (1UL << 32) - 1, 1UL << 32, (1UL << 32) + 1, 1UL << 40, (1UL << 40) + 1, ulong.MaxValue)),
        (1, AnyULong()));

    /// <summary>i64（时间戳等）：偏向常见毫秒时间与边界。</summary>
    public static Gen<long> I64Gen() => Gen.Frequency(
        (3, Gen.Choose(0, 400_000_000).Select(i => 1_600_000_000_000L + (i * 1000L))),
        (1, Gen.Elements(0L, -1L, long.MinValue, long.MaxValue)),
        (1, AnyULong().Select(u => unchecked((long)u))));

    /// <summary>协议 String 字段：不含 Unpaired_Surrogate 的 Unicode 文本。</summary>
    public static Gen<string> ProtocolString(int maxFragments = 8) => UnicodeTextFragments(maxFragments).Select(f => string.Concat(f));

    /// <summary>可打印 ASCII 的 MIME 类型。</summary>
    public static Gen<string> Mime() => Gen.Elements("application/octet-stream", "text/plain", "image/jpeg", "video/mp4", "application/pdf", "");

    private static Gen<T?> OrNull<T>(Gen<T> gen, int nullWeight, int valueWeight)
        where T : struct => Gen.Frequency((nullWeight, Gen.Constant<T?>(null)), (valueWeight, gen.Select(v => (T?)v)));

    private static Gen<string?> OrNullString(Gen<string> gen) => Gen.Frequency((1, Gen.Constant<string?>(null)), (1, gen.Select(s => (string?)s)));

    private static Gen<TEnum> EnumGen<TEnum>()
        where TEnum : struct, Enum => Gen.Elements(Enum.GetValues<TEnum>());

    /// <summary>FileEntry：size 偏向 0、2³²、1 TiB；mtime 随机出现或缺省。</summary>
    public static Gen<FileEntry> FileEntryGen() =>
        from name in ProtocolString(4)
        from size in U64Gen()
        from mime in Mime()
        from mtime in OrNull(I64Gen(), 3, 7)
        select new FileEntry(name, size, mime, mtime);

    public static Gen<ResumeFileState> ResumeFileStateGen() =>
        from i in U32Gen() from s in EnumGen<ResumeFileStatus>() from p in U64Gen() select new ResumeFileState(i, s, p);

    public static Gen<ResumeFilePlan> ResumeFilePlanGen() =>
        from i in U32Gen()
        from a in EnumGen<ResumeAction>()
        from start in U64Gen()
        from size in OrNull(U64Gen(), 1, 1)
        from mtime in OrNull(I64Gen(), 1, 1)
        select new ResumeFilePlan(i, a, start, size, mtime);

    /// <summary>文字正文：大多为合法 UTF‑8，偶尔为任意字节（正文类型是 Bytes）。</summary>
    public static Gen<Bytes> TextBody() => Gen.Frequency(
        (4, UnicodeTextFragments().Select(f => Bytes.Of(StrictUtf8.Encode(string.Concat(f))))),
        (1, BytesGen(32)));

    private static Gen<ValueList<T>> ListGen<T>(Gen<T> element, int max) =>
        Gen.Choose(0, max).SelectMany(n => element.ArrayOf(n)).Select(a => new ValueList<T>(a));

    /// <summary>C1.9 中任意一种消息的任意实例。</summary>
    public static Gen<ProtocolMessage> ProtocolMessageGen() => Gen.OneOf(
        EnumGen<SessionResultCode>().Select(ProtocolMessage (c) => new SessionResult(c)),
        EnumGen<SessionCloseReason>().Select(ProtocolMessage (c) => new SessionClose(c)),
        Gen.Constant<ProtocolMessage>(Heartbeat.Instance),
        BytesOfSize(32).Select(ProtocolMessage (b) => new PairRequest(b)),
        BytesOfSize(32).Select(ProtocolMessage (b) => new PairNonce(b)),
        BytesOfSize(32).Select(ProtocolMessage (b) => new PairReveal(b)),
        EnumGen<PairDecisionCode>().Select(ProtocolMessage (c) => new PairDecision(c)),
        EnumGen<PairCancelReason>().Select(ProtocolMessage (c) => new PairCancel(c)),
        Gen.Constant<ProtocolMessage>(PairComplete.Instance),
        Bytes16Gen().Select(ProtocolMessage (t) => new QrPairRequest(t)),
        EnumGen<QrPairResultCode>().Select(ProtocolMessage (c) => new QrPairResult(c)),
        from id in MessageIdGen() from t in I64Gen() from body in TextBody() select (ProtocolMessage)new Text(id, t, body),
        MessageIdGen().Select(ProtocolMessage (id) => new TextAck(id)),
        MessageIdGen().Select(ProtocolMessage (id) => new TextCorrupt(id)),
        from id in TransferIdGen() from files in ListGen(FileEntryGen(), 5) select (ProtocolMessage)new FileOffer(id, files),
        from id in TransferIdGen() from d in EnumGen<OfferDecision>() select (ProtocolMessage)new FileOfferReply(id, d),
        from id in TransferIdGen()
        from i in U32Gen()
        from off in U64Gen()
        from n in Gen.Frequency((4, Gen.Choose(1, 512)), (1, Gen.Constant(ProtocolConstants.MaxChunk)))
        from data in BytesOfSize(n)
        select (ProtocolMessage)new FileChunk(id, i, off, data),
        from id in TransferIdGen() from i in U32Gen() from h in BytesOfSize(32) select (ProtocolMessage)new FileEnd(id, i, h),
        from id in TransferIdGen() from i in U32Gen() from p in U64Gen() select (ProtocolMessage)new FileAck(id, i, p),
        from id in TransferIdGen()
        from i in U32Gen()
        from s in EnumGen<FileResultStatus>()
        from d in OrNullString(ProtocolString(3))
        select (ProtocolMessage)new FileResult(id, i, s, d),
        from id in TransferIdGen() from i in U32Gen() select (ProtocolMessage)new FileReadFailed(id, i),
        from id in TransferIdGen() from r in EnumGen<TransferCancelReason>() select (ProtocolMessage)new TransferCancel(id, r),
        TransferIdGen().Select(ProtocolMessage (id) => new ResumeRequest(id)),
        from id in TransferIdGen() from f in ListGen(ResumeFileStateGen(), 5) select (ProtocolMessage)new ResumeState(id, f),
        from id in TransferIdGen() from f in ListGen(ResumeFilePlanGen(), 5) select (ProtocolMessage)new ResumePlan(id, f));

    public static Arbitrary<ProtocolMessage> ProtocolMessage() => Arb.From(ProtocolMessageGen());
}
