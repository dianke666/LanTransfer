using System.Buffers.Binary;

namespace LanTransfer.Protocol;

/// <summary><see cref="FrameEncoder.Encode"/> 的结果：成功时 <see cref="Bytes"/> 为完整 Frame；载荷超限时 <see cref="PayloadTooLarge"/> 为载荷长度，且不输出字节。</summary>
public readonly record struct EncodeResult(byte[]? Bytes, long? PayloadTooLarge)
{
    public bool IsOk => Bytes is not null;
}

/// <summary>Frame 编码器（规范第 8 节），与 Kotlin 侧 <c>FrameEncoder</c> 对应。</summary>
public static class FrameEncoder
{
    /// <summary>
    /// 编码一条消息。只输出 <paramref name="version"/> 中已定义的字段；消息类型在该版本中未定义时抛出 <see cref="ArgumentException"/>；
    /// 载荷超过 <paramref name="maxPayload"/> 时返回 PayloadTooLarge，不输出任何字节（需求 22.12）。
    /// </summary>
    public static EncodeResult Encode(ProtocolMessage msg, ProtocolVersion? version = null, int maxPayload = ProtocolConstants.MaxPayload)
    {
        var v = version ?? ProtocolVersion.Current;
        if (!ProtocolEnums.Since(msg.Type).DefinedIn(v))
        {
            throw new ArgumentException($"{msg.Type} is not defined in {v}", nameof(msg));
        }

        var payload = MessageCodec.EncodePayload(msg);
        if (payload.Length > maxPayload)
        {
            return new EncodeResult(null, payload.Length);
        }

        var output = new byte[ProtocolConstants.FrameHeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(output, ProtocolConstants.FrameMagic);
        output[2] = (byte)msg.Type;
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(3), (uint)payload.Length);
        payload.CopyTo(output, ProtocolConstants.FrameHeaderSize);
        return new EncodeResult(output, null);
    }

    /// <summary>编码成功时返回字节，否则抛出 <see cref="InvalidOperationException"/>（用于确定不会超限的小消息）。</summary>
    public static byte[] EncodeOrThrow(ProtocolMessage msg, ProtocolVersion? version = null) =>
        Encode(msg, version).Bytes ?? throw new InvalidOperationException("payload too large");
}

/// <summary>解码错误（规范第 8 节）。</summary>
public abstract record DecodeError
{
    public sealed record BadMagic : DecodeError;

    public sealed record UnknownType(int Type) : DecodeError;

    public sealed record PayloadTooLarge(long Length) : DecodeError;

    public sealed record MalformedPayload(int Type, string Detail) : DecodeError;
}

/// <summary>一次 <see cref="FrameDecoder.Feed"/> 的结果：按到达顺序的完整消息，以及（若出错）错误。</summary>
public readonly record struct DecodeBatch(IReadOnlyList<ProtocolMessage> Messages, DecodeError? Error);

/// <summary>
/// 流式 Frame 解码器（规范第 8 节，需求 22.3–22.6、22.10、22.13），与 Kotlin 侧 <c>FrameDecoder</c> 对应：
/// 帧头到齐即检查；出错后不再接受输入；<see cref="BufferedBytes"/> ≤ 7 + maxPayload；<see cref="Close"/> 丢弃残留字节。
/// </summary>
public sealed class FrameDecoder(ProtocolVersion? version = null, int maxPayload = ProtocolConstants.MaxPayload)
{
    private readonly ProtocolVersion _version = version ?? ProtocolVersion.Current;
    private readonly byte[] _header = new byte[ProtocolConstants.FrameHeaderSize];
    private int _headerFill;
    private MessageType _type;
    private byte[]? _payload;
    private int _payloadFill;

    /// <summary>第一次出现的解码错误；未出错时为 null。</summary>
    public DecodeError? Error { get; private set; }

    public bool IsClosed { get; private set; }

    public int BufferedBytes => _headerFill + _payloadFill;

    public DecodeBatch Feed(ReadOnlySpan<byte> bytes)
    {
        if (Error is not null || IsClosed)
        {
            return new DecodeBatch([], null);
        }

        var output = new List<ProtocolMessage>();
        var i = 0;
        while (true)
        {
            if (_payload is null)
            {
                if (i == bytes.Length)
                {
                    break;
                }

                var take = Math.Min(ProtocolConstants.FrameHeaderSize - _headerFill, bytes.Length - i);
                bytes.Slice(i, take).CopyTo(_header.AsSpan(_headerFill));
                _headerFill += take;
                i += take;
                if (_headerFill < ProtocolConstants.FrameHeaderSize)
                {
                    break;
                }

                if (StartFrame() is { } failure)
                {
                    return Fail(output, failure);
                }
            }

            var p = _payload!;
            if (_payloadFill < p.Length)
            {
                if (i == bytes.Length)
                {
                    break;
                }

                var take = Math.Min(p.Length - _payloadFill, bytes.Length - i);
                bytes.Slice(i, take).CopyTo(p.AsSpan(_payloadFill));
                _payloadFill += take;
                i += take;
                if (_payloadFill < p.Length)
                {
                    break;
                }
            }

            ProtocolMessage msg;
            try
            {
                msg = MessageCodec.DecodePayload(_type, p, 0, p.Length, _version);
            }
            catch (MalformedException e)
            {
                return Fail(output, new DecodeError.MalformedPayload((int)_type, e.Detail));
            }

            output.Add(msg);
            Reset();
        }

        return new DecodeBatch(output, null);
    }

    /// <summary>连接关闭时调用：丢弃未完整到达的残留字节，不输出任何消息。</summary>
    public void Close()
    {
        Reset();
        IsClosed = true;
    }

    private DecodeError? StartFrame()
    {
        if (BinaryPrimitives.ReadUInt16BigEndian(_header) != ProtocolConstants.FrameMagic)
        {
            return new DecodeError.BadMagic();
        }

        int code = _header[2];
        if (!Enum.IsDefined((MessageType)code) || !ProtocolEnums.Since((MessageType)code).DefinedIn(_version))
        {
            return new DecodeError.UnknownType(code);
        }

        var len = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(3));
        if (len > (uint)maxPayload)
        {
            return new DecodeError.PayloadTooLarge(len);
        }

        _type = (MessageType)code;
        _payload = new byte[len];
        _payloadFill = 0;
        return null;
    }

    private DecodeBatch Fail(List<ProtocolMessage> output, DecodeError e)
    {
        Error = e;
        Reset();
        return new DecodeBatch(output, e);
    }

    private void Reset()
    {
        _headerFill = 0;
        _payload = null;
        _payloadFill = 0;
    }
}
