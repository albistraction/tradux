namespace Tradux.Application.Ports;

/// <summary>Template com versão ativa publicada numa organização (ADR-0025).</summary>
public sealed record TemplateCatalogEntry(string TemplateId, string Operation, int ActiveVersion, string ContentHash, DateTimeOffset PublishedAtUtc);

/// <summary>
/// Catálogo de templates de uma organização — a tela inicial do editor e
/// <c>GET /v1/templates</c>. Só lê o ponteiro ativo; nunca o conteúdo.
/// </summary>
public interface ITemplateCatalog
{
    Task<IReadOnlyList<TemplateCatalogEntry>> ListAsync(string organizationId, CancellationToken cancellationToken = default);
}
