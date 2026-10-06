using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KodizSignage.Core.Models;

/// <summary>
/// Immutable list with value equality, so records containing it (settings, playlist items) keep
/// structural equality – used for "did anything change?" checks and tests.
/// Serialized as a plain JSON array.
/// </summary>
[JsonConverter(typeof(EquatableListConverterFactory))]
public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    private readonly T[] _items;

    public EquatableList(IEnumerable<T> items)
    {
        _items = items.ToArray();
    }

    public static EquatableList<T> Empty { get; } = new(Array.Empty<T>());

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(EquatableList<T>? other) =>
        other is not null && (ReferenceEquals(this, other) || _items.SequenceEqual(other._items));

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(EquatableList<T>? a, EquatableList<T>? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(EquatableList<T>? a, EquatableList<T>? b) => !(a == b);

    public override string ToString() => "[" + string.Join(", ", _items) + "]";
}

public static class EquatableList
{
    public static EquatableList<T> ToEquatableList<T>(this IEnumerable<T> items) => new(items);
}

internal sealed class EquatableListConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(EquatableList<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<EquatableList<T>>
    {
        public override EquatableList<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var items = JsonSerializer.Deserialize<List<T>>(ref reader, options);
            return items is null ? null : new EquatableList<T>(items.Where(i => i is not null));
        }

        public override void Write(Utf8JsonWriter writer, EquatableList<T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
