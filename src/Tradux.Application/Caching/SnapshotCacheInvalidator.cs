using Tradux.Application.Ports;

namespace Tradux.Application.Caching;

/// <summary>
/// Mantém o <see cref="CompiledSnapshotCache"/> em dia com as publicações
/// (ADR-0005), sempre fora do caminho de geração:
/// <see cref="RunChangeFeedAsync"/> reage ao feed de mudanças (Change
/// Streams) e <see cref="PollOnceAsync"/> é a rede de segurança periódica,
/// que compara só o ponteiro de versão ativa das chaves em cache.
/// </summary>
public sealed class SnapshotCacheInvalidator
{
    private readonly CompiledSnapshotCache _cache;
    private readonly ITemplateSnapshotRepository _repository;
    private readonly Action<Exception>? _onError;

    public SnapshotCacheInvalidator(
        CompiledSnapshotCache cache, ITemplateSnapshotRepository repository, Action<Exception>? onError = null)
    {
        _cache = cache;
        _repository = repository;
        _onError = onError;
    }

    /// <summary>
    /// Consome o feed até o cancelamento. Se o feed cair, espera
    /// <paramref name="retryDelay"/>, reconcilia com um poll (cobre eventos
    /// perdidos enquanto desconectado) e reconecta.
    /// </summary>
    public async Task RunChangeFeedAsync(ISnapshotChangeFeed feed, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var key in feed.WatchAsync(cancellationToken).ConfigureAwait(false))
                {
                    await _cache.RefreshActiveAsync(key, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }

            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Executa o poll de segurança periodicamente até o cancelamento.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Intervalo não positivo (seria um laço sem pausa).</exception>
    public async Task RunPollingAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "O intervalo de poll precisa ser positivo.");
        }

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Compara a versão ativa em cache de cada chave com o ponteiro no repositório e atualiza as que divergem.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (key, cachedVersion) in _cache.CachedActiveVersions)
        {
            try
            {
                var currentVersion = await _repository.GetActiveVersionAsync(key, cancellationToken).ConfigureAwait(false);
                if (currentVersion != cachedVersion)
                {
                    await _cache.RefreshActiveAsync(key, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _onError?.Invoke(ex);
            }
        }
    }
}
