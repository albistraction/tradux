namespace Tradux.Contracts;

/// <summary>Resumo de uma versão de snapshot publicada (fachada "listar versões", seção 26).</summary>
public sealed record TemplateVersionInfo(
    int Version,
    string ContentHash,
    string Status,
    DateTimeOffset PublishedAtUtc);
