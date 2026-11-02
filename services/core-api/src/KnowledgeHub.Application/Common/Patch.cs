using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeHub.Application.Common;

/// <summary>
/// A field in a PATCH body with JSON merge semantics (Doc 02 §8.3): absent means "leave unchanged", and
/// <c>null</c> means "clear". A plain nullable property cannot tell those two apart.
/// </summary>
[JsonConverter(typeof(PatchConverterFactory))]
public readonly record struct Patch<T>(bool IsSet, T? Value);

public sealed class PatchConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Patch<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(PatchConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

public sealed class PatchConverter<T> : JsonConverter<Patch<T>>
{
    // A JSON null reaches Read too (rather than being skipped), so "present but null" is distinguishable.
    public override bool HandleNull => true;

    public override Patch<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? new Patch<T>(true, default) : new Patch<T>(true, JsonSerializer.Deserialize<T>(ref reader, options));

    public override void Write(Utf8JsonWriter writer, Patch<T> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Value, options);
}
