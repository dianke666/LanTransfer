namespace LanTransfer.Protocol;

/// <summary>各消息类型的载荷（Struct）编解码（规范第 9 节），与 Kotlin 侧 <c>MessageCodec</c> 对应。</summary>
internal static class MessageCodec
{
    public static byte[] EncodePayload(ProtocolMessage msg)
    {
        var w = new TlvWriter();
        switch (msg)
        {
            case SessionResult m: w.Enum(1, m.Result); break;
            case SessionClose m: w.Enum(1, m.Reason); break;
            case Heartbeat: break;
            case PairRequest m: w.Bytes(1, m.Commitment.Span); break;
            case PairNonce m: w.Bytes(1, m.Nonce.Span); break;
            case PairReveal m: w.Bytes(1, m.Nonce.Span); break;
            case PairDecision m: w.Enum(1, m.Decision); break;
            case PairCancel m: w.Enum(1, m.Reason); break;
            case PairComplete: break;
            case QrPairRequest m: w.Bytes16(1, m.Token); break;
            case QrPairResult m: w.Enum(1, m.Result); break;
            case Text m:
                w.Bytes16(1, m.MessageId.Value);
                w.I64(2, m.SentAt);
                w.Bytes(3, m.Body.Span);
                break;
            case TextAck m: w.Bytes16(1, m.MessageId.Value); break;
            case TextCorrupt m: w.Bytes16(1, m.MessageId.Value); break;
            case FileOffer m:
                w.Bytes16(1, m.TransferId.Value);
                w.List(2, m.Files, static (e, f) =>
                {
                    e.String(1, f.Name);
                    e.U64(2, f.Size);
                    e.String(3, f.Mime);
                    if (f.Mtime is { } t)
                    {
                        e.I64(4, t);
                    }
                });
                break;
            case FileOfferReply m:
                w.Bytes16(1, m.TransferId.Value);
                w.Enum(2, m.Decision);
                break;
            case FileChunk m:
                w.Bytes16(1, m.TransferId.Value);
                w.U32(2, m.FileIndex);
                w.U64(3, m.Offset);
                w.Bytes(4, m.Data.Span);
                break;
            case FileEnd m:
                w.Bytes16(1, m.TransferId.Value);
                w.U32(2, m.FileIndex);
                w.Bytes(3, m.Sha256.Span);
                break;
            case FileAck m:
                w.Bytes16(1, m.TransferId.Value);
                w.U32(2, m.FileIndex);
                w.U64(3, m.Persisted);
                break;
            case FileResult m:
                w.Bytes16(1, m.TransferId.Value);
                w.U32(2, m.FileIndex);
                w.Enum(3, m.Status);
                if (m.Detail is { } d)
                {
                    w.String(4, d);
                }

                break;
            case FileReadFailed m:
                w.Bytes16(1, m.TransferId.Value);
                w.U32(2, m.FileIndex);
                break;
            case TransferCancel m:
                w.Bytes16(1, m.TransferId.Value);
                w.Enum(2, m.Reason);
                break;
            case ResumeRequest m: w.Bytes16(1, m.TransferId.Value); break;
            case ResumeState m:
                w.Bytes16(1, m.TransferId.Value);
                w.List(2, m.Files, static (e, f) =>
                {
                    e.U32(1, f.FileIndex);
                    e.Enum(2, f.State);
                    e.U64(3, f.Persisted);
                });
                break;
            case ResumePlan m:
                w.Bytes16(1, m.TransferId.Value);
                w.List(2, m.Files, static (e, f) =>
                {
                    e.U32(1, f.FileIndex);
                    e.Enum(2, f.Action);
                    e.U64(3, f.Start);
                    if (f.Size is { } size)
                    {
                        e.U64(4, size);
                    }

                    if (f.Mtime is { } t)
                    {
                        e.I64(5, t);
                    }
                });
                break;
            default:
                throw new ArgumentException($"unsupported message {msg.GetType().Name}", nameof(msg));
        }

        return w.ToArray();
    }

    /// <summary>解析载荷；不符合 Struct 规则时抛出 <see cref="MalformedException"/>。</summary>
    public static ProtocolMessage DecodePayload(MessageType type, byte[] buf, int offset, int length, ProtocolVersion v)
    {
        var s = new TlvStruct(buf, offset, length);
        return type switch
        {
            MessageType.SessionResult => new SessionResult(s.Enum<SessionResultCode>(1, "result", v)),
            MessageType.SessionClose => new SessionClose(s.Enum<SessionCloseReason>(1, "reason", v)),
            MessageType.Heartbeat => Heartbeat.Instance,
            MessageType.PairRequest => new PairRequest(s.BytesFixed(1, "commitment", 32)),
            MessageType.PairNonce => new PairNonce(s.BytesFixed(1, "nonce", 32)),
            MessageType.PairReveal => new PairReveal(s.BytesFixed(1, "nonce", 32)),
            MessageType.PairDecision => new PairDecision(s.Enum<PairDecisionCode>(1, "decision", v)),
            MessageType.PairCancel => new PairCancel(s.Enum<PairCancelReason>(1, "reason", v)),
            MessageType.PairComplete => PairComplete.Instance,
            MessageType.QrPairRequest => new QrPairRequest(s.Bytes16(1, "token")),
            MessageType.QrPairResult => new QrPairResult(s.Enum<QrPairResultCode>(1, "result", v)),
            MessageType.Text => new Text(new MessageId(s.Bytes16(1, "message_id")), s.I64(2, "sent_at"), s.Bytes(3, "text")),
            MessageType.TextAck => new TextAck(new MessageId(s.Bytes16(1, "message_id"))),
            MessageType.TextCorrupt => new TextCorrupt(new MessageId(s.Bytes16(1, "message_id"))),
            MessageType.FileOffer => new FileOffer(
                TransferIdOf(s),
                new ValueList<FileEntry>(s.List(2, "files").Select(e =>
                    new FileEntry(e.String(1, "name"), e.U64(2, "size"), e.String(3, "mime"), e.OptI64(4, "mtime"))))),
            MessageType.FileOfferReply => new FileOfferReply(TransferIdOf(s), s.Enum<OfferDecision>(2, "decision", v)),
            MessageType.FileChunk => DecodeChunk(s),
            MessageType.FileEnd => new FileEnd(TransferIdOf(s), s.U32(2, "file_index"), s.BytesFixed(3, "sha256", 32)),
            MessageType.FileAck => new FileAck(TransferIdOf(s), s.U32(2, "file_index"), s.U64(3, "persisted")),
            MessageType.FileResult => new FileResult(TransferIdOf(s), s.U32(2, "file_index"), s.Enum<FileResultStatus>(3, "status", v), s.OptString(4, "detail")),
            MessageType.FileReadFailed => new FileReadFailed(TransferIdOf(s), s.U32(2, "file_index")),
            MessageType.TransferCancel => new TransferCancel(TransferIdOf(s), s.Enum<TransferCancelReason>(2, "reason", v)),
            MessageType.ResumeRequest => new ResumeRequest(TransferIdOf(s)),
            MessageType.ResumeState => new ResumeState(
                TransferIdOf(s),
                new ValueList<ResumeFileState>(s.List(2, "files").Select(e =>
                    new ResumeFileState(e.U32(1, "file_index"), e.Enum<ResumeFileStatus>(2, "state", v), e.U64(3, "persisted"))))),
            MessageType.ResumePlan => new ResumePlan(
                TransferIdOf(s),
                new ValueList<ResumeFilePlan>(s.List(2, "files").Select(e =>
                    new ResumeFilePlan(e.U32(1, "file_index"), e.Enum<ResumeAction>(2, "action", v), e.U64(3, "start"), e.OptU64(4, "size"), e.OptI64(5, "mtime"))))),
            _ => throw new MalformedException($"unsupported type {type}"),
        };
    }

    private static FileChunk DecodeChunk(TlvStruct s)
    {
        if (s.Has(4) && s.Length(4) is < 1 or > ProtocolConstants.MaxChunk)
        {
            throw new MalformedException($"chunk data length {s.Length(4)} out of range");
        }

        return new FileChunk(TransferIdOf(s), s.U32(2, "file_index"), s.U64(3, "offset"), s.Bytes(4, "data"));
    }

    private static TransferId TransferIdOf(TlvStruct s) => new(s.Bytes16(1, "transfer_id"));
}
