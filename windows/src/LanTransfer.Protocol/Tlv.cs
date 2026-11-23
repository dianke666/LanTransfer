using System.Buffers.Binary;

namespace LanTransfer.Protocol;

/// <summary>载荷不符合 Struct 规则（规范 2.2、2.3）。只在协议层内部使用。</summary>
internal sealed class MalformedException(string detail) : Exception(detail)
{
    public string Detail { get; } = detail;
}

/// <summary>各协议枚举取值的引入版本。当前全部在 1.0 中引入；以后新增取值时在此登记。</summary>
public static class ProtocolEnums
{
    public static ProtocolVersion Since<TEnum>(TEnum value)
        where TEnum : struct, Enum => ProtocolVersion.V1_0;

    /// <summary><paramref name="code"/> 在 <paramref name="version"/> 中是否是 <typeparamref name="TEnum"/> 的已定义取值。</summary>
    public static bool TryDecode<TEnum>(byte code, ProtocolVersion version, out TEnum value)
        where TEnum : struct, Enum
    {
        value = (TEnum)Enum.ToObject(typeof(TEnum), code);
        return Enum.IsDefined(value) && Since(value).DefinedIn(version);
    }
}

/// <summary>TLV Struct 编码器：字段必须按 tag 升序写入（规范 2.2 第 6 条）。</summary>
internal sealed class TlvWriter
{
    private readonly MemoryStream _out = new();
    private int _lastTag = -1;

    private void Header(int tag, int length)
    {
        if (tag is < 0 or > 255 || tag <= _lastTag)
        {
            throw new ArgumentException($"TLV tags must be strictly increasing: {tag} after {_lastTag}");
        }

        _lastTag = tag;
        Span<byte> h = stackalloc byte[5];
        h[0] = (byte)tag;
        BinaryPrimitives.WriteUInt32BigEndian(h[1..], (uint)length);
        _out.Write(h);
    }

    public void U8(int tag, byte v)
    {
        Header(tag, 1);
        _out.WriteByte(v);
    }

    public void U16(int tag, ushort v)
    {
        Header(tag, 2);
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        _out.Write(b);
    }

    public void U32(int tag, uint v)
    {
        Header(tag, 4);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        _out.Write(b);
    }

    public void U64(int tag, ulong v)
    {
        Header(tag, 8);
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        _out.Write(b);
    }

    public void I64(int tag, long v) => U64(tag, unchecked((ulong)v));

    public void Enum<TEnum>(int tag, TEnum v)
        where TEnum : struct, Enum => U8(tag, Convert.ToByte(v, System.Globalization.CultureInfo.InvariantCulture));

    public void Bytes(int tag, ReadOnlySpan<byte> v)
    {
        Header(tag, v.Length);
        _out.Write(v);
    }

    public void Bytes16(int tag, Bytes16 v)
    {
        Span<byte> b = stackalloc byte[Protocol.Bytes16.Size];
        v.WriteTo(b);
        Bytes(tag, b);
    }

    /// <summary>前置条件：<paramref name="v"/> 不含 Unpaired_Surrogate（StrictUtf8.Encode 会拒绝）。</summary>
    public void String(int tag, string v) => Bytes(tag, StrictUtf8.Encode(v));

    public void List<T>(int tag, IReadOnlyList<T> items, Action<TlvWriter, T> element)
    {
        var body = new MemoryStream();
        Span<byte> n = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(n, (uint)items.Count);
        body.Write(n);
        foreach (var item in items)
        {
            var w = new TlvWriter();
            element(w, item);
            var bytes = w.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(n, (uint)bytes.Length);
            body.Write(n);
            body.Write(bytes);
        }

        Bytes(tag, body.ToArray());
    }

    public byte[] ToArray() => _out.ToArray();
}

/// <summary>
/// 已解析的 TLV Struct：构造时检查 tag 严格递增、每个字段不越界、字段恰好填满缓冲区（规范 2.2 第 1、5 条）。
/// 读取已知字段时检查类型长度；未知 tag 不会被读取，因而被跳过（第 4 条）。
/// </summary>
internal sealed class TlvStruct
{
    private readonly byte[] _buf;
    private readonly int[] _starts = new int[256];
    private readonly int[] _lengths = new int[256];

    public TlvStruct(byte[] buf, int offset, int length)
    {
        _buf = buf;
        Array.Fill(_starts, -1);
        var pos = offset;
        var end = offset + length;
        var lastTag = -1;
        while (pos < end)
        {
            if (end - pos < 5)
            {
                throw new MalformedException("truncated field header");
            }

            int tag = buf[pos];
            var len = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(pos + 1, 4));
            if (len > (uint)(end - pos - 5))
            {
                throw new MalformedException($"field {tag} exceeds buffer");
            }

            if (tag <= lastTag)
            {
                throw new MalformedException($"tag {tag} not strictly increasing");
            }

            lastTag = tag;
            _starts[tag] = pos + 5;
            _lengths[tag] = (int)len;
            pos += 5 + (int)len;
        }
    }

    public bool Has(int tag) => _starts[tag] >= 0;

    public int Length(int tag) => _lengths[tag];

    private int StartOf(int tag, string name) =>
        Has(tag) ? _starts[tag] : throw new MalformedException($"missing field {tag} ({name})");

    private ReadOnlySpan<byte> Fixed(int tag, string name, int size)
    {
        var start = StartOf(tag, name);
        if (_lengths[tag] != size)
        {
            throw new MalformedException($"field {tag} ({name}) must be {size} bytes, got {_lengths[tag]}");
        }

        return _buf.AsSpan(start, size);
    }

    public byte U8(int tag, string name) => Fixed(tag, name, 1)[0];

    public ushort U16(int tag, string name) => BinaryPrimitives.ReadUInt16BigEndian(Fixed(tag, name, 2));

    public uint U32(int tag, string name) => BinaryPrimitives.ReadUInt32BigEndian(Fixed(tag, name, 4));

    public ulong U64(int tag, string name) => BinaryPrimitives.ReadUInt64BigEndian(Fixed(tag, name, 8));

    public long I64(int tag, string name) => BinaryPrimitives.ReadInt64BigEndian(Fixed(tag, name, 8));

    public ulong? OptU64(int tag, string name) => Has(tag) ? U64(tag, name) : null;

    public long? OptI64(int tag, string name) => Has(tag) ? I64(tag, name) : null;

    public Bytes Bytes(int tag, string name) => Protocol.Bytes.Of(_buf.AsSpan(StartOf(tag, name), _lengths[tag]));

    public Bytes BytesFixed(int tag, string name, int size) => Protocol.Bytes.Of(Fixed(tag, name, size));

    public Bytes16 Bytes16(int tag, string name) => Protocol.Bytes16.Of(Fixed(tag, name, Protocol.Bytes16.Size));

    public string String(int tag, string name) =>
        StrictUtf8.Decode(_buf.AsSpan(StartOf(tag, name), _lengths[tag])) ?? throw new MalformedException($"field {tag} ({name}) is not valid UTF-8");

    public string? OptString(int tag, string name) => Has(tag) ? String(tag, name) : null;

    public TEnum Enum<TEnum>(int tag, string name, ProtocolVersion version)
        where TEnum : struct, Enum
    {
        var code = U8(tag, name);
        return ProtocolEnums.TryDecode<TEnum>(code, version, out var value)
            ? value
            : throw new MalformedException($"field {tag} ({name}) has undefined value {code}");
    }

    public List<TlvStruct> List(int tag, string name)
    {
        var p = StartOf(tag, name);
        var end = p + _lengths[tag];
        if (_lengths[tag] < 4)
        {
            throw new MalformedException($"list {tag} ({name}) truncated");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(_buf.AsSpan(p, 4));
        var pos = p + 4;
        var items = new List<TlvStruct>();
        for (var i = 0L; i < count; i++)
        {
            if (end - pos < 4)
            {
                throw new MalformedException($"list {tag} ({name}) element header truncated");
            }

            var len = BinaryPrimitives.ReadUInt32BigEndian(_buf.AsSpan(pos, 4));
            if (len > (uint)(end - pos - 4))
            {
                throw new MalformedException($"list {tag} ({name}) element exceeds buffer");
            }

            items.Add(new TlvStruct(_buf, pos + 4, (int)len));
            pos += 4 + (int)len;
        }

        if (pos != end)
        {
            throw new MalformedException($"list {tag} ({name}) has trailing bytes");
        }

        return items;
    }
}
