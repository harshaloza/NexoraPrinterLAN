using System.Text.Json;
using System.Text.Json.Serialization;
using Nexora.Shared.Jobs;

namespace Nexora.Shared;

/// <summary>
/// Single JSON configuration for config files, metadata and wire messages. Only concrete, known types are
/// deserialized (no polymorphic type names), so payloads cannot instantiate arbitrary types.
/// </summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = Create(indented: false);
    public static readonly JsonSerializerOptions Indented = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            MaxDepth = 16,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        o.Converters.Add(new JobStateConverter());
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static byte[] ToBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static string ToText<T>(T value, bool indented = false) => JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static T Parse<T>(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Options) ?? throw new JsonException($"Empty {typeof(T).Name}");

    public static T Parse<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Options) ?? throw new JsonException($"Empty {typeof(T).Name}");

    private sealed class JobStateConverter : JsonConverter<JobState>
    {
        public override JobState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            JobStateExtensions.FromWire(reader.GetString() ?? "");

        public override void Write(Utf8JsonWriter writer, JobState value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToWire());
    }
}
