using System.Collections.Concurrent;
using Tradux.Application.Ports;
using Tradux.Application.Snapshots;

namespace Tradux.Application.Caching;

/// <summary>
/// Cache local ao processo de snapshots compilados (ADR-0005).
///
/// <list type="bullet">
/// <item>Caminho quente: <see cref="GetActiveAsync"/>/<see cref="GetVersionAsync"/>
/// devolvem a entrada em memória sem nenhuma chamada ao repositório.</item>
/// <item>Cache-miss: uma única leitura do repositório por chave, mesmo com
/// requisições concorrentes (<see cref="Lazy{T}"/> por chave), seguida de
/// compilação. Falha ou "não encontrado" nunca ficam em cache — a próxima
/// requisição tenta de novo.</item>
/// <item>Invalidação: <see cref="RefreshActiveAsync"/> carrega e compila a
/// nova versão ativa <i>antes</i> de trocar a referência — requisições em
/// andamento continuam com a entrada antiga completa; nunca há estado
/// parcial nem janela sem entrada.</item>
/// <item>Versões específicas (<see cref="GetVersionAsync"/>) são imutáveis
/// (ADR-0003) e nunca precisam de invalidação.</item>
/// </list>
/// </summary>
public sealed class CompiledSnapshotCache
{
    private readonly ITemplateSnapshotRepository _repository;
    private readonly SnapshotCompiler _compiler;
    private readonly ConcurrentDictionary<TemplateKey, Lazy<Task<CompiledSnapshot?>>> _active = new();
    private readonly ConcurrentDictionary<(TemplateKey Key, int Version), Lazy<Task<CompiledSnapshot?>>> _versions = new();

    // Serializa as trocas de versão ativa: duas notificações quase
    // simultâneas nunca deixam uma leitura mais antiga sobrescrever uma mais
    // nova. Trocas são raras e ficam fora do caminho de geração.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public CompiledSnapshotCache(ITemplateSnapshotRepository repository, SnapshotCompiler compiler)
    {
        _repository = repository;
        _compiler = compiler;
    }

    /// <summary>Chaves com versão ativa em cache e a versão correspondente (entradas ainda carregando ficam de fora).</summary>
    public IReadOnlyDictionary<TemplateKey, int> CachedActiveVersions =>
        _active
            .Where(e => e.Value.IsValueCreated && e.Value.Value.IsCompletedSuccessfully && e.Value.Value.Result is not null)
            .ToDictionary(e => e.Key, e => e.Value.Value.Result!.Version);

    public Task<CompiledSnapshot?> GetActiveAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
        GetOrLoadAsync(_active, key, LoadActiveAsync, cancellationToken);

    public Task<CompiledSnapshot?> GetVersionAsync(TemplateKey key, int version, CancellationToken cancellationToken = default) =>
        GetOrLoadAsync(_versions, (key, version), k => LoadVersionAsync(k.Item1, k.Item2), cancellationToken);

    /// <summary>
    /// Relê a versão ativa de <paramref name="key"/> e troca a entrada em
    /// cache atomicamente. Se a chave não está em cache, não faz nada — a
    /// próxima requisição já vai ler o estado atual.
    /// </summary>
    public async Task RefreshActiveAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_active.ContainsKey(key))
            {
                return;
            }

            var fresh = await LoadActiveAsync(key).ConfigureAwait(false);
            if (fresh is null)
            {
                _active.TryRemove(key, out _);
                return;
            }

            _active[key] = Completed(fresh);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<CompiledSnapshot?> GetOrLoadAsync<TKey>(
        ConcurrentDictionary<TKey, Lazy<Task<CompiledSnapshot?>>> entries,
        TKey key,
        Func<TKey, Task<CompiledSnapshot?>> load,
        CancellationToken cancellationToken)
        where TKey : notnull
    {
        // Acerto = já havia carga concluída com sucesso para a chave (só metadado na telemetria).
        var hit = entries.TryGetValue(key, out var existing) && existing.IsValueCreated && existing.Value.IsCompletedSuccessfully;
        Observability.TraduxTelemetry.CacheLookup(hit);
        var lazy = entries.GetOrAdd(key, k => new Lazy<Task<CompiledSnapshot?>>(() => load(k)));
        CompiledSnapshot? result;
        try
        {
            // A carga é compartilhada entre requisições e não herda o token
            // de nenhuma delas: cancelar uma requisição só abandona a espera.
            result = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            entries.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }

        if (result is null)
        {
            entries.TryRemove(KeyValuePair.Create(key, lazy));
        }

        return result;
    }

    private async Task<CompiledSnapshot?> LoadActiveAsync(TemplateKey key)
    {
        var snapshot = await _repository.GetActiveAsync(key, CancellationToken.None).ConfigureAwait(false);
        return snapshot is null ? null : CompileAndRemember(snapshot);
    }

    private async Task<CompiledSnapshot?> LoadVersionAsync(TemplateKey key, int version)
    {
        var snapshot = await _repository.GetVersionAsync(key, version, CancellationToken.None).ConfigureAwait(false);
        return snapshot is null ? null : _compiler.Compile(snapshot);
    }

    private CompiledSnapshot CompileAndRemember(TemplateSnapshot snapshot)
    {
        var compiled = _compiler.Compile(snapshot);
        // A versão ativa também passa a responder por versão fixada, sem
        // recompilar — o conteúdo de uma versão nunca muda (ADR-0003).
        _versions.TryAdd(
            (snapshot.Key, snapshot.Version),
            Completed(compiled));
        return compiled;
    }

    private static Lazy<Task<CompiledSnapshot?>> Completed(CompiledSnapshot? value) => new(Task.FromResult(value));
}
