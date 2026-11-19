using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tradux.Contracts;

/// <summary>
/// Serializa <see cref="TimeSpan"/> como número de milissegundos
/// (docs/api/README.md §4: <c>duracaoMs: number</c>), em vez do formato
/// textual <c>"00:00:00.123"</c> padrão do <c>System.Text.Json</c>.
/// </summary>
public sealed class MillisecondsTimeSpanJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TimeSpan.FromMilliseconds(reader.GetDouble());

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.TotalMilliseconds);
}
