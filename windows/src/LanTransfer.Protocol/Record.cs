namespace LanTransfer.Protocol;

/// <summary>
/// 安全记录层的一个方向（规范第 7 节，需求 10.1、10.5），与 Kotlin 侧 <c>RecordCipher</c> 对应：
/// <c>Record := len:u16 ‖ AES‑256‑GCM(k, nonce = 0 ‖ n, ad = 空, plaintext)</c>，len 17–65535，n 从 0 开始逐条加 1。
/// </summary>
public sealed class RecordCipher
{
    /// <summary>Noise 保留 2⁶⁴ − 1，可用的最大计数器为 2⁶⁴ − 2。</summary>
    internal const ulong LastNonce = ulong.MaxValue;

    private readonly byte[] _key;
    private ulong _n;
    private RecordReader? _reader;

    internal RecordCipher(byte[] key) => _key = key;

    /// <summary>
    /// 加密 <paramref name="plain"/>，按每条最多 65,519 字节拆成若干条 record 并依次拼接；为空时输出为空。
    /// 计数器将要用尽时抛出 <see cref="InvalidOperationException"/>（调用方应终止 Session）。
    /// </summary>
    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        var output = new MemoryStream(plain.Length + (((plain.Length / ProtocolConstants.RecordMaxPlaintext) + 1) * 18));
        var p = 0;
        Span<byte> len = stackalloc byte[2];
        while (p < plain.Length)
        {
            var chunk = Math.Min(ProtocolConstants.RecordMaxPlaintext, plain.Length - p);
            if (_n == LastNonce)
            {
                throw new InvalidOperationException("record counter exhausted");
            }

            var ct = Primitives.AesGcmEncrypt(_key, _n, [], plain.Slice(p, chunk));
            _n++;
            len[0] = (byte)(ct.Length >> 8);
            len[1] = (byte)ct.Length;
            output.Write(len);
            output.Write(ct);
            p += chunk;
        }

        return output.ToArray();
    }

    /// <summary>接收方向的流式读取器（同一 RecordCipher 只能有一个）。</summary>
    public RecordReader OpenStream()
    {
        if (_reader is not null)
        {
            throw new InvalidOperationException("stream already opened");
        }

        return _reader = new RecordReader(_key);
    }
}

/// <summary><see cref="RecordReader.Feed"/> 的结果：本次输入中完整到达并通过校验的明文；<see cref="IntegrityError"/> 为 true 时之后不再输出任何明文。</summary>
public readonly record struct RecordFeedResult(IReadOnlyList<Bytes> Chunks, bool IntegrityError);

/// <summary>把 TCP 字节流切成 record 并解密；出错后不再接受输入（需求 10.5）。</summary>
public sealed class RecordReader
{
    private readonly byte[] _key;
    private readonly byte[] _lenBuf = new byte[2];
    private ulong _n;
    private int _lenFill;
    private byte[]? _body;
    private int _bodyFill;

    internal RecordReader(byte[] key) => _key = key;

    public bool IsFailed { get; private set; }

    /// <summary>缓存的未完成 record 字节数（≤ 2 + 65535）。</summary>
    public int BufferedBytes => _lenFill + _bodyFill;

    public RecordFeedResult Feed(ReadOnlySpan<byte> bytes)
    {
        if (IsFailed)
        {
            return new RecordFeedResult([], true);
        }

        var output = new List<Bytes>();
        var i = 0;
        while (i < bytes.Length)
        {
            if (_body is null)
            {
                var take = Math.Min(2 - _lenFill, bytes.Length - i);
                bytes.Slice(i, take).CopyTo(_lenBuf.AsSpan(_lenFill));
                _lenFill += take;
                i += take;
                if (_lenFill < 2)
                {
                    break;
                }

                var recLen = (_lenBuf[0] << 8) | _lenBuf[1];
                if (recLen < ProtocolConstants.RecordMinCiphertext)
                {
                    return Fail(output);
                }

                _body = new byte[recLen];
                _bodyFill = 0;
                continue;
            }

            var n = Math.Min(_body.Length - _bodyFill, bytes.Length - i);
            bytes.Slice(i, n).CopyTo(_body.AsSpan(_bodyFill));
            _bodyFill += n;
            i += n;
            if (_bodyFill < _body.Length)
            {
                break;
            }

            if (_n == RecordCipher.LastNonce || Primitives.AesGcmDecrypt(_key, _n, [], _body) is not { } plain)
            {
                return Fail(output);
            }

            _n++;
            output.Add(Bytes.Of(plain));
            _body = null;
            _bodyFill = 0;
            _lenFill = 0;
        }

        return new RecordFeedResult(output, false);
    }

    private RecordFeedResult Fail(List<Bytes> output)
    {
        IsFailed = true;
        _body = null;
        _bodyFill = 0;
        _lenFill = 0;
        return new RecordFeedResult(output, true);
    }
}
