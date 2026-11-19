using Tradux.Application;
using Tradux.Application.Ports;
using Tradux.Application.Snapshots;

namespace Tradux.Application.Tests;

/// <summary>
/// Repositório em memória para testes de <c>Application</c>, com contadores
/// de leitura — é o que permite provar "cache aquecido não lê o
/// repositório" sem MongoDB. Semântica de publicação igual à do adaptador
/// real: versão sequencial por chave, um único ativo.
/// </summary>
internal sealed class InMemorySnapshotRepository : ITemplateSnapshotRepository
{
    private readonly object _gate = new();
    private readonly List<TemplateSnapshot> _snapshots = [];
    private int _contentReads;
    private int _pointerReads;

    /// <summary>Atraso artificial das leituras de conteúdo, para abrir janelas de concorrência em teste.</summary>
    public TimeSpan ReadDelay { get; set; }

    private Func<SnapshotDraft, Task>? _beforeGuardedPublish;

    /// <summary>
    /// Executado uma única vez, antes da próxima publicação condicionada à
    /// versão ativa: é onde um teste encaixa uma publicação "concorrente" de
    /// forma determinística.
    /// </summary>
    public Func<SnapshotDraft, Task>? BeforeGuardedPublish
    {
        get => Volatile.Read(ref _beforeGuardedPublish);
        set => Volatile.Write(ref _beforeGuardedPublish, value);
    }

    /// <summary>Leituras de conteúdo desta chave falham (simula erro de infraestrutura num template).</summary>
    public TemplateKey? FailReadsFor { get; set; }

    /// <summary>Leituras que trazem conteúdo (<see cref="GetActiveAsync"/>/<see cref="GetVersionAsync"/>).</summary>
    public int ContentReads => Volatile.Read(ref _contentReads);

    /// <summary>Leituras só do ponteiro de versão ativa (poll de segurança).</summary>
    public int PointerReads => Volatile.Read(ref _pointerReads);

    public async Task<TemplateSnapshot?> GetActiveAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        if (key == FailReadsFor)
        {
            throw new InvalidOperationException("Falha simulada de leitura.");
        }

        return await GetActiveCoreAsync(key, cancellationToken);
    }

    private async Task<TemplateSnapshot?> GetActiveCoreAsync(TemplateKey key, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _contentReads);
        await DelayAsync(cancellationToken);
        lock (_gate)
        {
            return _snapshots.SingleOrDefault(s => s.Key == key && s.Status == SnapshotStatus.Active);
        }
    }

    public async Task<TemplateSnapshot?> GetVersionAsync(TemplateKey key, int version, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _contentReads);
        await DelayAsync(cancellationToken);
        lock (_gate)
        {
            return _snapshots.SingleOrDefault(s => s.Key == key && s.Version == version);
        }
    }

    public Task<int?> GetActiveVersionAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _pointerReads);
        lock (_gate)
        {
            return Task.FromResult(_snapshots.SingleOrDefault(s => s.Key == key && s.Status == SnapshotStatus.Active)?.Version);
        }
    }

    public Task<IReadOnlyList<SnapshotSummary>> ListVersionsAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<SnapshotSummary> result = _snapshots
                .Where(s => s.Key == key)
                .OrderByDescending(s => s.Version)
                .Select(s => new SnapshotSummary(s.Version, s.ContentHash, s.Status, s.PublishedAtUtc))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public async Task<TemplateSnapshot> PublishAsync(SnapshotDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft.EnforceExpectedActive && Interlocked.Exchange(ref _beforeGuardedPublish, null) is { } hook)
        {
            await hook(draft);
        }

        lock (_gate)
        {
            var active = _snapshots.SingleOrDefault(s => s.Key == draft.Key && s.Status == SnapshotStatus.Active);
            if (active is not null && active.ContentHash == draft.ContentHash)
            {
                return active;
            }

            if (draft.EnforceExpectedActive && active?.Version != draft.ExpectedActiveVersion)
            {
                throw new SnapshotConcurrencyException(draft.Key, draft.ExpectedActiveVersion, active?.Version);
            }

            var nextVersion = _snapshots.Where(s => s.Key == draft.Key).Select(s => s.Version).DefaultIfEmpty(0).Max() + 1;
            for (var i = 0; i < _snapshots.Count; i++)
            {
                if (_snapshots[i].Key == draft.Key && _snapshots[i].Status == SnapshotStatus.Active)
                {
                    _snapshots[i] = _snapshots[i] with { Status = SnapshotStatus.Superseded };
                }
            }

            var snapshot = new TemplateSnapshot
            {
                Key = draft.Key,
                Version = nextVersion,
                ContentHash = draft.ContentHash,
                Status = SnapshotStatus.Active,
                PublishedAtUtc = draft.PublishedAtUtc,
                Content = draft.Content,
                Manifest = draft.Manifest,
                Scenarios = draft.Scenarios,
            };
            _snapshots.Add(snapshot);
            return snapshot;
        }
    }

    public Task<TemplateSnapshot?> ActivateVersionAsync(TemplateKey key, int version, int? expectedActiveVersion, CancellationToken cancellationToken = default) =>
        Activate(key, version, expectedActiveVersion);

    private Task<TemplateSnapshot?> Activate(TemplateKey key, int version, int? expected)
    {
        lock (_gate)
        {
            var active = _snapshots.SingleOrDefault(s => s.Key == key && s.Status == SnapshotStatus.Active)?.Version;
            if (_snapshots.Any(s => s.Key == key && s.Version == version) && active != expected)
            {
                throw new SnapshotConcurrencyException(key, expected, active);
            }

            var target = _snapshots.FindIndex(s => s.Key == key && s.Version == version);
            if (target < 0)
            {
                return Task.FromResult<TemplateSnapshot?>(null);
            }

            for (var i = 0; i < _snapshots.Count; i++)
            {
                if (_snapshots[i].Key == key)
                {
                    _snapshots[i] = _snapshots[i] with { Status = i == target ? SnapshotStatus.Active : SnapshotStatus.Superseded };
                }
            }

            return Task.FromResult<TemplateSnapshot?>(_snapshots[target]);
        }
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        ReadDelay > TimeSpan.Zero ? Task.Delay(ReadDelay, cancellationToken) : Task.CompletedTask;
}
