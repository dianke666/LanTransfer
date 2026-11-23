namespace LanTransfer.Protocol;

/// <summary>Frame 的 type（规范第 9 节）。全部在 1.0 中引入。</summary>
public enum MessageType : byte
{
    SessionResult = 0x01,
    SessionClose = 0x02,
    Heartbeat = 0x03,
    PairRequest = 0x10,
    PairNonce = 0x11,
    PairReveal = 0x12,
    PairDecision = 0x13,
    PairCancel = 0x14,
    PairComplete = 0x15,
    QrPairRequest = 0x16,
    QrPairResult = 0x17,
    Text = 0x20,
    TextAck = 0x21,
    TextCorrupt = 0x22,
    FileOffer = 0x30,
    FileOfferReply = 0x31,
    FileChunk = 0x32,
    FileEnd = 0x33,
    FileAck = 0x34,
    FileResult = 0x35,
    FileReadFailed = 0x36,
    TransferCancel = 0x37,
    ResumeRequest = 0x38,
    ResumeState = 0x39,
    ResumePlan = 0x3A,
}

public enum SessionResultCode : byte { Accepted = 0, NotTrusted = 1, KeyMismatch = 2, Duplicate = 3 }

public enum SessionCloseReason : byte { Normal = 0, Unpaired = 1, AppExit = 2, Background = 3, Duplicate = 4 }

public enum PairDecisionCode : byte { Accepted = 0, Rejected = 1, Busy = 2, Timeout = 3 }

public enum PairCancelReason : byte { User = 0, Timeout = 1 }

public enum QrPairResultCode : byte { Ok = 0, TokenInvalid = 1 }

public enum OfferDecision : byte { Accept = 0, Reject = 1, Timeout = 2, NoSpace = 3, CannotSave = 4, InvalidOffer = 5 }

public enum FileResultStatus : byte { Completed = 0, ChecksumFailed = 1, WriteFailed = 2 }

public enum TransferCancelReason : byte { User = 0, UnknownTransfer = 1, Unpaired = 2 }

public enum ResumeFileStatus : byte { None = 0, Partial = 1, Completed = 2, Terminal = 3 }

public enum ResumeAction : byte { Continue = 0, Restart = 1, Skip = 2 }

internal static class Check
{
    public static Bytes Size(Bytes b, int size, string name) =>
        b.Length == size ? b : throw new ArgumentException($"{name} must be {size} bytes, got {b.Length}", name);
}

/// <summary>C1.9 中的 Protocol_Message。可选字段用可空类型表示，保留“是否出现”的信息（规范 2.5）。</summary>
public abstract record ProtocolMessage
{
    public abstract MessageType Type { get; }
}

public sealed record SessionResult(SessionResultCode Result) : ProtocolMessage
{
    public override MessageType Type => MessageType.SessionResult;
}

public sealed record SessionClose(SessionCloseReason Reason) : ProtocolMessage
{
    public override MessageType Type => MessageType.SessionClose;
}

public sealed record Heartbeat : ProtocolMessage
{
    public static Heartbeat Instance { get; } = new();

    public override MessageType Type => MessageType.Heartbeat;
}

public sealed record PairRequest(Bytes Commitment) : ProtocolMessage
{
    public Bytes Commitment { get; } = Check.Size(Commitment, 32, nameof(Commitment));

    public override MessageType Type => MessageType.PairRequest;
}

public sealed record PairNonce(Bytes Nonce) : ProtocolMessage
{
    public Bytes Nonce { get; } = Check.Size(Nonce, 32, nameof(Nonce));

    public override MessageType Type => MessageType.PairNonce;
}

public sealed record PairReveal(Bytes Nonce) : ProtocolMessage
{
    public Bytes Nonce { get; } = Check.Size(Nonce, 32, nameof(Nonce));

    public override MessageType Type => MessageType.PairReveal;
}

public sealed record PairDecision(PairDecisionCode Decision) : ProtocolMessage
{
    public override MessageType Type => MessageType.PairDecision;
}

public sealed record PairCancel(PairCancelReason Reason) : ProtocolMessage
{
    public override MessageType Type => MessageType.PairCancel;
}

public sealed record PairComplete : ProtocolMessage
{
    public static PairComplete Instance { get; } = new();

    public override MessageType Type => MessageType.PairComplete;
}

public sealed record QrPairRequest(Bytes16 Token) : ProtocolMessage
{
    public override MessageType Type => MessageType.QrPairRequest;
}

public sealed record QrPairResult(QrPairResultCode Result) : ProtocolMessage
{
    public override MessageType Type => MessageType.QrPairResult;
}

/// <summary>
/// 文字消息（对应 Kotlin 侧 <c>Text.text</c>；C# 中成员名不能与类型同名，故称 <see cref="Body"/>）。
/// 正文是 UTF‑8 字节（类型为 Bytes），是否合法由文字规则检查（规范第 13 节）。
/// </summary>
public sealed record Text(MessageId MessageId, long SentAt, Bytes Body) : ProtocolMessage
{
    public override MessageType Type => MessageType.Text;
}

public sealed record TextAck(MessageId MessageId) : ProtocolMessage
{
    public override MessageType Type => MessageType.TextAck;
}

public sealed record TextCorrupt(MessageId MessageId) : ProtocolMessage
{
    public override MessageType Type => MessageType.TextCorrupt;
}

/// <summary>File_Offer 中的一个文件。size 与 mime 的语义约束由接收方校验，不属于解码错误。</summary>
public sealed record FileEntry(string Name, ulong Size, string Mime, long? Mtime = null);

public sealed record FileOffer(TransferId TransferId, ValueList<FileEntry> Files) : ProtocolMessage
{
    public override MessageType Type => MessageType.FileOffer;
}

public sealed record FileOfferReply(TransferId TransferId, OfferDecision Decision) : ProtocolMessage
{
    public override MessageType Type => MessageType.FileOfferReply;
}

public sealed record FileChunk(TransferId TransferId, uint FileIndex, ulong Offset, Bytes Data) : ProtocolMessage
{
    public Bytes Data { get; } = Data.Length is >= 1 and <= ProtocolConstants.MaxChunk
        ? Data
        : throw new ArgumentException($"chunk size must be 1..{ProtocolConstants.MaxChunk}", nameof(Data));

    public override MessageType Type => MessageType.FileChunk;
}

public sealed record FileEnd(TransferId TransferId, uint FileIndex, Bytes Sha256) : ProtocolMessage
{
    public Bytes Sha256 { get; } = Check.Size(Sha256, 32, nameof(Sha256));

    public override MessageType Type => MessageType.FileEnd;
}

public sealed record FileAck(TransferId TransferId, uint FileIndex, ulong Persisted) : ProtocolMessage
{
    public override MessageType Type => MessageType.FileAck;
}

public sealed record FileResult(TransferId TransferId, uint FileIndex, FileResultStatus Status, string? Detail = null) : ProtocolMessage
{
    public override MessageType Type => MessageType.FileResult;
}

public sealed record FileReadFailed(TransferId TransferId, uint FileIndex) : ProtocolMessage
{
    public override MessageType Type => MessageType.FileReadFailed;
}

public sealed record TransferCancel(TransferId TransferId, TransferCancelReason Reason) : ProtocolMessage
{
    public override MessageType Type => MessageType.TransferCancel;
}

public sealed record ResumeRequest(TransferId TransferId) : ProtocolMessage
{
    public override MessageType Type => MessageType.ResumeRequest;
}

public sealed record ResumeFileState(uint FileIndex, ResumeFileStatus State, ulong Persisted);

public sealed record ResumeState(TransferId TransferId, ValueList<ResumeFileState> Files) : ProtocolMessage
{
    public override MessageType Type => MessageType.ResumeState;
}

public sealed record ResumeFilePlan(uint FileIndex, ResumeAction Action, ulong Start, ulong? Size = null, long? Mtime = null);

public sealed record ResumePlan(TransferId TransferId, ValueList<ResumeFilePlan> Files) : ProtocolMessage
{
    public override MessageType Type => MessageType.ResumePlan;
}
