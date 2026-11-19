using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Tradux.Knowledge;

/// <summary>Nível de acesso de uma fonte (ADR-0013 §11). Ordem crescente de restrição.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccessLevel>))]
public enum AccessLevel
{
    Public,
    Internal,
    Restricted,
}

/// <summary>
/// Tipos de fonte com chunking próprio (docs/rag/CHUNKING_POLICY.md); os demais
/// tipos de <c>docs/rag/sources.json</c> (requirement, adr, …) são Markdown.
/// </summary>
public static class SourceTypes
{
    public const string Glossary = "glossary";
    public const string Xsd = "xsd";

    /// <summary>Gerado pelo host a partir do motor, não vem de arquivo.</summary>
    public const string OperatorCatalog = "operator-catalog";
}

/// <summary>
/// Fonte aprovada e versionada (seção 24). <see cref="TenantScope"/> é
/// <c>global</c> ou o id de uma organização.
/// </summary>
public sealed record KnowledgeSource
{
    public const string GlobalScope = "global";

    public required string SourceId { get; init; }
    public required string SourceType { get; init; }
    public required string Version { get; init; }
    public required string Content { get; init; }
    public string TenantScope { get; init; } = GlobalScope;
    public AccessLevel AccessLevel { get; init; } = AccessLevel.Internal;
    public string Language { get; init; } = "pt-BR";
    public string? Provider { get; init; }
    public string? Municipality { get; init; }
    public string? Operation { get; init; }
    public string? SchemaVersion { get; init; }
    public string? EffectiveDate { get; init; }
    public string? Path { get; init; }

    /// <summary>Hash do conteúdo + metadados que mudam a indexação (reindexação incremental).</summary>
    public string ContentHash => KnowledgeHash.Of($"{SourceType}|{Version}|{TenantScope}|{AccessLevel}|{Language}|{Content}");
}

/// <summary>Metadados de um chunk — os campos da seção 24 + nível de acesso e âncora.</summary>
public sealed record ChunkMetadata(
    string SourceId,
    string SourceType,
    string Version,
    string TenantScope,
    AccessLevel AccessLevel,
    string? Provider,
    string? Municipality,
    string? Operation,
    string? SchemaVersion,
    string Language,
    string ContentHash,
    string? EffectiveDate,
    string Anchor);

/// <summary>Trecho indexado.</summary>
public sealed record KnowledgeChunk(string Id, string Text, ChunkMetadata Metadata);

/// <summary>Quem pergunta: organização (nula = só fontes globais) e nível máximo de acesso.</summary>
public sealed record AccessScope(string? OrganizationId, AccessLevel MaxLevel)
{
    public bool Allows(ChunkMetadata metadata) =>
        metadata.AccessLevel <= MaxLevel
        && (metadata.TenantScope == KnowledgeSource.GlobalScope || (OrganizationId is not null && metadata.TenantScope == OrganizationId));
}

/// <summary>Resultado de busca: sempre com fonte, versão e âncora (seção 24: "referência à fonte e versão").</summary>
public sealed record SearchHit(string SourceId, string SourceType, string Version, string Anchor, string? EffectiveDate, double Score, string Snippet, string ChunkId);

/// <summary>O que a indexação fez.</summary>
public sealed record IndexReport(
    IReadOnlyList<string> AddedSources,
    IReadOnlyList<string> UpdatedSources,
    IReadOnlyList<string> RemovedSources,
    IReadOnlyList<string> UnchangedSources,
    int ChunksWritten,
    int ChunksRemoved);

/// <summary>
/// Armazenamento substituível do índice (seção 24; ADR-0013 §8). A
/// implementação inicial é lexical (<see cref="Bm25KnowledgeIndex"/>).
/// </summary>
public interface IKnowledgeIndex
{
    /// <summary>Hash da fonte indexada, ou nulo se ausente.</summary>
    string? SourceHash(string sourceId);

    IReadOnlyCollection<string> SourceIds { get; }

    /// <summary>Troca os chunks da fonte; devolve (gravados, removidos) — chunk de mesmo hash é mantido.</summary>
    (int Written, int Removed) ReplaceSource(string sourceId, string sourceHash, string tenantScope, IReadOnlyList<KnowledgeChunk> chunks);

    int RemoveSource(string sourceId);

    string? SourceTenantScope(string sourceId);

    IReadOnlyList<SearchHit> Search(string query, AccessScope scope, int limit = 5);
}

internal static class KnowledgeHash
{
    public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
