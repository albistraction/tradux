using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tradux.Domain.Mapping;

/// <summary>
/// Converte <see cref="LiteralNode.Value"/> (<c>object?</c>) para/de JSON
/// preservando o tipo natural (texto/<see cref="decimal"/>/booleano/nulo), em vez do
/// <see cref="JsonElement"/> genérico que o <c>System.Text.Json</c>
/// produziria por padrão para uma propriedade <c>object</c> — os operadores
/// (<c>RuleOperatorMethods</c>) fazem <c>is string</c>/<c>is decimal</c> e
/// quebrariam contra um <see cref="JsonElement"/> boxeado.
/// </summary>
internal sealed class LiteralValueJsonConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.GetDecimal(),
            _ => throw new JsonException($"Valor de LiteralNode com tipo JSON não suportado: {reader.TokenType}."),
        };

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case decimal d:
                writer.WriteNumberValue(d);
                break;
            default:
                throw new JsonException(
                    $"Valor de LiteralNode com tipo CLR '{value.GetType()}' não suportado para serialização.");
        }
    }
}
