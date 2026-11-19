using System.Text.Json.Serialization;

namespace Tradux.Domain.SourceData;

/// <summary>Tipo de valor de um campo de origem (os mesmos que a árvore interna distingue).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SourceValueType>))]
public enum SourceValueType
{
    Text,
    Number,
    Date,
}
