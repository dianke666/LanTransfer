using System.Globalization;
using System.Text.Json;
using LanTransfer.Protocol;

namespace LanTransfer.Testing;

/// <summary>测试向量中的模型 → 协议模型（写法见 protocol/test-vectors/README.md），与 Kotlin 侧 <c>VectorJson</c> 对应。</summary>
public static class VectorJson
{
    /// <summary>规范中的取值名称（UPPER_SNAKE）→ C# 枚举（PascalCase）。</summary>
    public static T EnumOf<T>(string specName)
        where T : struct, Enum =>
        Enum.Parse<T>(string.Concat(specName.Split('_').Select(p => p[..1] + p[1..].ToLowerInvariant())));

    /// <summary>C# 枚举 → 规范中的取值名称。</summary>
    public static string SpecName<T>(T value)
        where T : struct, Enum =>
        string.Concat(value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : char.ToUpperInvariant(c).ToString()));

    public static uint ParseIpv4(string s)
    {
        var p = s.Split('.').Select(x => byte.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];
    }

    public static ProtocolVersion ParseVersion(string s)
    {
        var p = s.Split('.');
        return new ProtocolVersion(byte.Parse(p[0], CultureInfo.InvariantCulture), byte.Parse(p[1], CultureInfo.InvariantCulture));
    }

    private static Bytes B(JsonElement o, string k) => Bytes.OfHex(o.Str(k));

    private static Bytes16 B16(JsonElement o, string k) => Bytes16.OfHex(o.Str(k));

    private static ulong U64(JsonElement o, string k) => ulong.Parse(o.Str(k), CultureInfo.InvariantCulture);

    private static long I64(JsonElement o, string k) => long.Parse(o.Str(k), CultureInfo.InvariantCulture);

    private static uint U32(JsonElement o, string k) => o.GetProperty(k).GetUInt32();

    private static T E<T>(JsonElement o, string k)
        where T : struct, Enum => EnumOf<T>(o.Str(k));

    private static TransferId Tid(JsonElement o) => new(B16(o, "transfer_id"));

    private static ValueList<T> List<T>(JsonElement o, Func<JsonElement, T> f) => new(o.GetProperty("files").EnumerateArray().Select(f).ToArray());

    public static ProtocolMessage Message(MessageType type, JsonElement o) => type switch
    {
        MessageType.SessionResult => new SessionResult(E<SessionResultCode>(o, "result")),
        MessageType.SessionClose => new SessionClose(E<SessionCloseReason>(o, "reason")),
        MessageType.Heartbeat => Heartbeat.Instance,
        MessageType.PairRequest => new PairRequest(B(o, "commitment")),
        MessageType.PairNonce => new PairNonce(B(o, "nonce")),
        MessageType.PairReveal => new PairReveal(B(o, "nonce")),
        MessageType.PairDecision => new PairDecision(E<PairDecisionCode>(o, "decision")),
        MessageType.PairCancel => new PairCancel(E<PairCancelReason>(o, "reason")),
        MessageType.PairComplete => PairComplete.Instance,
        MessageType.QrPairRequest => new QrPairRequest(B16(o, "token")),
        MessageType.QrPairResult => new QrPairResult(E<QrPairResultCode>(o, "result")),
        MessageType.Text => new Text(new MessageId(B16(o, "message_id")), I64(o, "sent_at"), B(o, "text")),
        MessageType.TextAck => new TextAck(new MessageId(B16(o, "message_id"))),
        MessageType.TextCorrupt => new TextCorrupt(new MessageId(B16(o, "message_id"))),
        MessageType.FileOffer => new FileOffer(Tid(o), List(o, f => new FileEntry(f.Str("name"), U64(f, "size"), f.Str("mime"), f.Optional("mtime") is not null ? I64(f, "mtime") : null))),
        MessageType.FileOfferReply => new FileOfferReply(Tid(o), E<OfferDecision>(o, "decision")),
        MessageType.FileChunk => new FileChunk(Tid(o), U32(o, "file_index"), U64(o, "offset"), B(o, "data")),
        MessageType.FileEnd => new FileEnd(Tid(o), U32(o, "file_index"), B(o, "sha256")),
        MessageType.FileAck => new FileAck(Tid(o), U32(o, "file_index"), U64(o, "persisted")),
        MessageType.FileResult => new FileResult(Tid(o), U32(o, "file_index"), E<FileResultStatus>(o, "status"), o.Optional("detail")?.GetString()),
        MessageType.FileReadFailed => new FileReadFailed(Tid(o), U32(o, "file_index")),
        MessageType.TransferCancel => new TransferCancel(Tid(o), E<TransferCancelReason>(o, "reason")),
        MessageType.ResumeRequest => new ResumeRequest(Tid(o)),
        MessageType.ResumeState => new ResumeState(Tid(o), List(o, f => new ResumeFileState(U32(f, "file_index"), E<ResumeFileStatus>(f, "state"), U64(f, "persisted")))),
        MessageType.ResumePlan => new ResumePlan(Tid(o), List(o, f => new ResumeFilePlan(
            U32(f, "file_index"), E<ResumeAction>(f, "action"), U64(f, "start"),
            f.Optional("size") is not null ? U64(f, "size") : null, f.Optional("mtime") is not null ? I64(f, "mtime") : null))),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static Announcement Announcement(JsonElement o) => new(
        new DeviceId(B16(o, "device_id")), o.Str("display_name"), EnumOf<DeviceType>(o.Str("device_type")),
        new ProtocolVersion(o.GetProperty("proto_major").GetByte(), o.GetProperty("proto_minor").GetByte()), o.GetProperty("port").GetUInt16());

    public static QrPayload Qr(JsonElement o) => new(
        new DeviceId(B16(o, "device_id")), o.Str("display_name"),
        new ValueList<uint>(o.GetProperty("ipv4_list").EnumerateArray().Select(e => ParseIpv4(e.GetString()!)).ToArray()),
        o.GetProperty("port").GetUInt16(), B(o, "fingerprint"), B16(o, "token"));
}
