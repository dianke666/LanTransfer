using System.Collections;

namespace LanTransfer.Domain;

/// <summary>按元素集合比较相等的只读集合，使含集合字段的 record 具有值语义（对应 Kotlin 的 <c>Set</c>）。</summary>
public sealed class ValueSet<T> : IReadOnlySet<T>, IEquatable<ValueSet<T>>
{
    private readonly HashSet<T> _items;

    public ValueSet(IEnumerable<T> items) => _items = [.. items];

    public static ValueSet<T> Empty { get; } = new([]);

    public int Count => _items.Count;

    public bool Contains(T item) => _items.Contains(item);

    public bool IsProperSubsetOf(IEnumerable<T> other) => _items.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => _items.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => _items.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => _items.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => _items.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => _items.SetEquals(other);

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ValueSet<T>? other) => other is not null && _items.SetEquals(other._items);

    public override bool Equals(object? obj) => obj is ValueSet<T> s && Equals(s);

    /// <summary>与元素顺序无关的哈希。</summary>
    public override int GetHashCode()
    {
        var h = 0;
        foreach (var item in _items)
        {
            h ^= item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item);
        }

        return h ^ _items.Count;
    }

    public override string ToString() => $"{{{string.Join(", ", _items)}}}";
}
