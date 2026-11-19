using Tradux.Application.Ports;
using Tradux.Application.Snapshots;

namespace Tradux.Application.Caching;

/// <summary>
/// Repositório de snapshots que atualiza o cache desta instância a cada
/// gravação: toda publicação ou troca de versão ativa — conteúdo, camadas,
/// aprovação de atualização, pacote importado, rollback — passa a valer aqui
/// na hora (leitura da própria escrita, ADR-0025 §3), sem que o chamador
/// precise lembrar. As outras instâncias seguem pelo Change Stream/poll (ADR-0005).
/// O cache lê pelo repositório interno, nunca por este.
/// </summary>
public sealed class CacheRefreshingSnapshotRepository(ITemplateSnapshotRepository inner, CompiledSnapshotCache cache) : ITemplateSnapshotRepository
{
    public Task<TemplateSnapshot?> GetActiveAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
        inner.GetActiveAsync(key, cancellationToken);

    public Task<TemplateSnapshot?> GetVersionAsync(TemplateKey key, int version, CancellationToken cancellationToken = default) =>
        inner.GetVersionAsync(key, version, cancellationToken);

    public Task<int?> GetActiveVersionAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
        inner.GetActiveVersionAsync(key, cancellationToken);

    public Task<IReadOnlyList<SnapshotSummary>> ListVersionsAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
        inner.ListVersionsAsync(key, cancellationToken);

    public async Task<TemplateSnapshot> PublishAsync(SnapshotDraft draft, CancellationToken cancellationToken = default)
    {
        var published = await inner.PublishAsync(draft, cancellationToken).ConfigureAwait(false);
        await cache.RefreshActiveAsync(published.Key, cancellationToken).ConfigureAwait(false);
        return published;
    }

    public async Task<TemplateSnapshot?> ActivateVersionAsync(
        TemplateKey key, int version, int? expectedActiveVersion, CancellationToken cancellationToken = default)
    {
        var activated = await inner.ActivateVersionAsync(key, version, expectedActiveVersion, cancellationToken).ConfigureAwait(false);
        if (activated is not null)
        {
            await cache.RefreshActiveAsync(key, cancellationToken).ConfigureAwait(false);
        }

        return activated;
    }
}
