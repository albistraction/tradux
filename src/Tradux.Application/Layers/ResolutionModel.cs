using System.Text.Json.Serialization;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Domain.Layers;

namespace Tradux.Application.Layers;

/// <summary>
/// Conflito de resolução: uma diferença de camada que não se aplica mais ao
/// schema/base atuais (caminho sumiu, mudou de tipo, virou não mapeável…).
/// Conflito crítico bloqueia publicação; aviso só informa.
/// </summary>
public sealed record ResolutionConflict(string Path, LayerRef IntroducedBy, string Code, DiagnosticSeverity Severity, string Message);

/// <summary>Resultado da resolução de uma cadeia de camadas.</summary>
public sealed record ResolvedTemplate(
    SnapshotContent Content,
    SnapshotManifest Manifest,
    IReadOnlyDictionary<string, Domain.Layers.MappingEntry> Entries,
    IReadOnlyList<ResolutionConflict> Conflicts)
{
    public bool HasBlockingConflicts => Conflicts.Any(c => c.Severity == DiagnosticSeverity.Critical);
}

[JsonConverter(typeof(JsonStringEnumConverter<ChangeKind>))]
public enum ChangeKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>Diferença rastreável entre a resolução ativa e uma nova (seção 16: "mostra diferenças").</summary>
public sealed record ResolutionChange(string Path, ChangeKind Kind, LayerRef? PreviousSource, LayerRef? NewSource);

[JsonConverter(typeof(JsonStringEnumConverter<UpdateStatus>))]
public enum UpdateStatus
{
    /// <summary>Política manual: aguardando aprovação.</summary>
    Proposed,

    /// <summary>Publicada (automática ou aprovada).</summary>
    Applied,

    /// <summary>Não publicada: conflito ou gate reprovado. A versão ativa não mudou.</summary>
    Failed,

    /// <summary>Proposta descartada por um usuário.</summary>
    Rejected,

    /// <summary>Proposta substituída por outra mais nova antes de ser aplicada.</summary>
    Superseded,

    /// <summary>Aprovação em andamento: a proposta foi reservada por quem aprovou (ninguém mais a decide).</summary>
    Applying,
}

/// <summary>
/// Registro de uma atualização disparada por nova versão de camada (seção
/// 16, políticas manual e automática). Guarda o que mudaria, os conflitos e
/// as falhas de gate — mesmo quando nada foi publicado.
/// </summary>
public sealed record TemplateUpdate
{
    public required string Id { get; init; }
    public required TemplateKey Key { get; init; }
    public required UpdatePolicy Policy { get; init; }
    public required LayerRef Trigger { get; init; }
    public required int? FromSnapshotVersion { get; init; }

    /// <summary>Camada topo ativa que será rebaseada sobre <see cref="Trigger"/> (nulo quando o topo é a própria camada nova).</summary>
    public LayerRef? RebasedFrom { get; init; }

    /// <summary>Cadeia proposta, da base ao topo; a camada topo rebaseada ainda não gravada tem versão 0.</summary>
    public required IReadOnlyList<LayerRef> ProposedChain { get; init; }

    public required UpdateStatus Status { get; init; }
    public IReadOnlyList<ResolutionChange> Changes { get; init; } = [];
    public IReadOnlyList<ResolutionConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<Diagnostic> GateFailures { get; init; } = [];

    /// <summary>Relatório dos testes estruturais da resolução proposta (Fase 8).</summary>
    public Contracts.Quality.QualityReport? Report { get; init; }

    /// <summary>Mudança de saída por cenário funcional entre a versão ativa e a proposta (Fase 8).</summary>
    public IReadOnlyList<ScenarioRegression> Regressions { get; init; } = [];
    public int? AppliedSnapshotVersion { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset? ResolvedAtUtc { get; init; }
    public string? ResolvedBy { get; init; }
}

/// <summary>
/// Regressão de um cenário funcional numa atualização: se a saída mudou em
/// relação à versão ativa e o XPath da primeira diferença (sem valores).
/// </summary>
/// <param name="Problem">
/// Por que o cenário não pôde ser comparado (só tipo, limite ou posição —
/// nunca dado do cenário); nesse caso <paramref name="Changed"/> é verdadeiro,
/// porque a mudança não pode ser descartada.
/// </param>
public sealed record ScenarioRegression(string Scenario, bool Changed, string? DestinationXPath, string? Problem = null)
{
    public static ScenarioRegression NotCompared(string scenario, string problem) => new(scenario, true, null, problem);
}
