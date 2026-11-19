using System.Text.Json;

namespace Tradux.Knowledge;

/// <summary>Um operador do catálogo tipado, como o host o descreve (sem depender do motor).</summary>
public sealed record OperatorDoc(string Name, string Category, string Description, IReadOnlyList<OperatorParameterDoc> Parameters);

public sealed record OperatorParameterDoc(string Name, string Kind, string Description);

/// <summary>
/// Monta o corpus: fontes do registro + fontes geradas pelo host (catálogo
/// de operadores). O RAG nunca referencia o motor — o host passa os dados.
/// </summary>
public static class KnowledgeCorpus
{
    public const string OperatorCatalogId = "operator-catalog";

    public static KnowledgeSource OperatorCatalog(IEnumerable<OperatorDoc> operators, string version) => new()
    {
        SourceId = OperatorCatalogId,
        SourceType = SourceTypes.OperatorCatalog,
        Version = version,
        Content = JsonSerializer.Serialize(operators.OrderBy(o => o.Name, StringComparer.Ordinal), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        EffectiveDate = null,
    };

    /// <summary>Corpus global: registro de <c>docs/rag/sources.json</c> + extras (ex.: catálogo de operadores).</summary>
    public static IReadOnlyList<KnowledgeSource> Global(string repositoryRoot, IEnumerable<KnowledgeSource>? extra = null) =>
        [.. SourceRegistry.Read(repositoryRoot, SourceRegistry.Load(Path.Combine(repositoryRoot, "docs", "rag", "sources.json"))), .. extra ?? []];
}
