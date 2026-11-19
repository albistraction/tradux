using System.Text.Json;

namespace Tradux.Application.Testing;

/// <summary>
/// Cenário funcional do autor (seção 19): entrada real de negócio + XML
/// esperado (comparação semântica) e/ou asserções estruturais. Cenário só
/// com "deve gerar" é aceito, mas não conta para o mínimo obrigatório.
/// </summary>
/// <remarks>
/// Entrada em JSON (<see cref="Data"/>) ou em XML de origem
/// (<see cref="SourceXml"/>, validado pelo XSD de origem do template —
/// ADR-0015). Com <see cref="SourceXml"/>, <see cref="Data"/> é ignorado
/// (vazio = <c>{}</c>).
/// </remarks>
public sealed record FunctionalScenario(
    string Name, JsonElement Data, string? ExpectedXml = null, IReadOnlyList<StructuralAssertion>? Assertions = null, string? SourceXml = null)
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    // Cenário só com XML chega sem "data": vira {} (JsonElement vazio não serializa).
    public JsonElement Data { get; init; } = Data.ValueKind == JsonValueKind.Undefined ? EmptyObject : Data;
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AssertionKind>))]
public enum AssertionKind
{
    /// <summary>Ao menos um nó no caminho.</summary>
    Exists,

    /// <summary>Nenhum nó no caminho.</summary>
    Absent,

    /// <summary>O primeiro nó no caminho tem exatamente o texto esperado.</summary>
    Equals,

    /// <summary>Quantidade de nós no caminho igual ao número esperado.</summary>
    Count,
}

/// <summary>
/// Asserção estrutural sobre o XML gerado, pelo caminho lógico do editor
/// (<c>/DPS/numero</c>, <c>/Pedido/itens/item</c>, <c>/DPS/@versao</c>) —
/// nomes locais, sem prefixo de namespace.
/// </summary>
public sealed record StructuralAssertion(string Path, AssertionKind Kind, string? Expected = null);
