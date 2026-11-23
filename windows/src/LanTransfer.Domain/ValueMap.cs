using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace LanTransfer.Domain;

/// <summary>按键值对集合比较相等的不可变映射，使含映射字段的 record 具有值语义（对应 Kotlin 的 <c>Map</c>）。</summary>
public sealed class ValueMap<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>, IEquatable<ValueMap<TKey, TValue>>
    where TKey : notnull
{
    private readonly ImmutableDictionary<TKey, TValue> _items;

    private ValueMap(ImmutableDictionary<TKey, TValue> items) => _items = items;

    public ValueMap(IEnumerable<KeyValuePair<TKey, TValue>> items)
        : this(ImmutableDictionary.CreateRange(items))
    {
    }

    public static ValueMap<TKey, TValue> Empty { get; } = new(ImmutableDictionary<TKey, TValue>.Empty);

    public int Count => _items.Count;

    public IEnumerable<TKey> Keys => _items.Keys;

    public IEnumerable<TValue> Values => _items.Values;

    public TValue this[TKey key] => _items[key];

    public bool ContainsKey(TKey key) => _items.ContainsKey(key);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _items.TryGetValue(key, out value);

    /// <summary>加入或替换一项。</summary>
    public ValueMap<TKey, TValue> SetItem(TKey key, TValue value) => new(_items.SetItem(key, value));

    public ValueMap<TKey, TValue> Remove(TKey key) => new(_items.Remove(key));

    public ValueMap<TKey, TValue> RemoveRange(IEnumerable<TKey> keys) => new(_items.RemoveRange(keys));

    /// <summary>只保留满足条件的项。</summary>
    public ValueMap<TKey, TValue> Filter(Func<KeyValuePair<TKey, TValue>, bool> keep) => new(_items.Where(keep));

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ValueMap<TKey, TValue>? other)
    {
        if (other is null || other.Count != Count)
        {
            return false;
        }

        foreach (var (k, v) in _items)
        {
            if (!other._items.TryGetValue(k, out var ov) || !EqualityComparer<TValue>.Default.Equals(v, ov))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ValueMap<TKey, TValue> m && Equals(m);

    /// <summary>与元素顺序无关的哈希。</summary>
    public override int GetHashCode()
    {
        var h = _items.Count;
        foreach (var (k, v) in _items)
        {
            h ^= HashCode.Combine(k, v);
        }

        return h;
    }

    public override string ToString() => $"{{{string.Join(", ", _items.Select(kv => $"{kv.Key}={kv.Value}"))}}}";
}
