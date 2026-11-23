using System.Buffers.Binary;
using System.Collections;

namespace LanTransfer.Protocol;

/// <summary>不可变字节串，按内容比较相等（协议中 <c>Bytes</c> / <c>Bytes[N]</c> 字段的模型类型），与 Kotlin 侧 <c>Bytes</c> 对应。</summary>
public sealed class Bytes : IEquatable<Bytes>
{
    private readonly byte[] _data;

    private Bytes(byte[] data) => _data = data;

    public static Bytes Empty { get; } = new([]);

    public int Length => _data.Length;

    public byte this[int index] => _data[index];

    public ReadOnlySpan<byte> Span => _data;

    public static Bytes Of(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    public static Bytes OfHex(string hex) => new(Convert.FromHexString(hex));

    public byte[] ToArray() => (byte[])_data.Clone();

    public string ToHex() => Convert.ToHexStringLower(_data);

    public bool Equals(Bytes? other) => other is not null && _data.AsSpan().SequenceEqual(other._data);

    public override bool Equals(object? obj) => obj is Bytes b && Equals(b);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(_data);
        return h.ToHashCode();
    }

    public override string ToString() => $"Bytes({ToHex()})";

    public static bool operator ==(Bytes? a, Bytes? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(Bytes? a, Bytes? b) => !(a == b);
}

/// <summary>恰好 16 字节（大端存为两个 ulong），值语义；与 Kotlin 侧 <c>Bytes16</c> 对应。</summary>
public readonly record struct Bytes16(ulong Hi, ulong Lo) : IComparable<Bytes16>
{
    public const int Size = 16;

    public static Bytes16 Of(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"Bytes16 requires exactly 16 bytes, got {bytes.Length}", nameof(bytes));
        }

        return new(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }

    public static Bytes16 OfHex(string hex) => Of(Convert.FromHexString(hex));

    public void WriteTo(Span<byte> dest)
    {
        BinaryPrimitives.WriteUInt64BigEndian(dest, Hi);
        BinaryPrimitives.WriteUInt64BigEndian(dest[8..], Lo);
    }

    public byte[] ToArray()
    {
        var b = new byte[Size];
        WriteTo(b);
        return b;
    }

    public string ToHex() => Convert.ToHexStringLower(ToArray());

    /// <summary>按无符号字节逐个比较（同时互连时按发起方设备 ID 去重使用）。</summary>
    public int CompareTo(Bytes16 other) => Hi != other.Hi ? Hi.CompareTo(other.Hi) : Lo.CompareTo(other.Lo);

    public override string ToString() => ToHex();
}

/// <summary>设备 ID。</summary>
public readonly record struct DeviceId(Bytes16 Value)
{
    public override string ToString() => $"DeviceId({Value.ToHex()})";
}

/// <summary>文字消息 ID。</summary>
public readonly record struct MessageId(Bytes16 Value)
{
    public override string ToString() => $"MessageId({Value.ToHex()})";
}

/// <summary>文件传输任务 ID。</summary>
public readonly record struct TransferId(Bytes16 Value)
{
    public override string ToString() => $"TransferId({Value.ToHex()})";
}

/// <summary>按元素比较相等的只读列表，使含列表字段的 record 具有值语义。</summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    private readonly T[] _items;

    public ValueList(IEnumerable<T> items) => _items = items.ToArray();

    public static ValueList<T> Empty { get; } = new([]);

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ValueList<T>? other) => other is not null && _items.AsSpan().SequenceEqual(other._items, EqualityComparer<T>.Default);

    public override bool Equals(object? obj) => obj is ValueList<T> l && Equals(l);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var item in _items)
        {
            h.Add(item);
        }

        return h.ToHashCode();
    }

    public override string ToString() => $"[{string.Join(", ", _items)}]";
}
