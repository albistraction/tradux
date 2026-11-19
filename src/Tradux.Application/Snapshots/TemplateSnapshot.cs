namespace Tradux.Application.Snapshots;

/// <summary>Estado de um snapshot publicado (ADR-0005). Nunca existem dois <see cref="Active"/> para a mesma chave.</summary>
public enum SnapshotStatus
{
    Active,
    Superseded,
}

/// <summary>
/// Snapshot publicado e imutável (ADR-0003/0004). <see cref="Version"/> é
/// sequencial por <see cref="TemplateKey"/>, atribuída pelo repositório no
/// momento da publicação; <see cref="ContentHash"/> identifica o conteúdo
/// (ver <see cref="SnapshotCompiler.ComputeContentHash"/>). Só
/// <see cref="Status"/> muda depois de publicado.
/// </summary>
public sealed record TemplateSnapshot
{
    public required TemplateKey Key { get; init; }
    public required int Version { get; init; }
    public required string ContentHash { get; init; }
    public required SnapshotStatus Status { get; init; }
    public required DateTimeOffset PublishedAtUtc { get; init; }
    public required SnapshotContent Content { get; init; }

    /// <summary>Camadas e proveniência (publicação por camadas, Fase 7); nulo em snapshot publicado sem camadas.</summary>
    public Layers.SnapshotManifest? Manifest { get; init; }

    /// <summary>Cenários funcionais que aprovaram esta versão (Fase 10; fora do hash). Vão no pacote portátil como testes.</summary>
    public IReadOnlyList<Testing.FunctionalScenario> Scenarios { get; init; } = [];
}

/// <summary>Snapshot pronto para publicar — ainda sem versão (o repositório atribui).</summary>
public sealed record SnapshotDraft(TemplateKey Key, string ContentHash, DateTimeOffset PublishedAtUtc, SnapshotContent Content)
{
    public Layers.SnapshotManifest? Manifest { get; init; }

    /// <summary>Cenários funcionais que aprovaram a publicação (fora do hash).</summary>
    public IReadOnlyList<Testing.FunctionalScenario> Scenarios { get; init; } = [];

    /// <summary>
    /// Concorrência otimista (revisão da Fase 7, C3): quando verdadeiro, o
    /// repositório só publica se a versão ativa ainda for
    /// <see cref="ExpectedActiveVersion"/> (nulo = nenhuma ativa) — senão lança
    /// <see cref="Ports.SnapshotConcurrencyException"/> sem gravar nada.
    /// </summary>
    public bool EnforceExpectedActive { get; init; }

    public int? ExpectedActiveVersion { get; init; }
}

/// <summary>Resumo de uma versão publicada, sem o conteúdo.</summary>
public sealed record SnapshotSummary(int Version, string ContentHash, SnapshotStatus Status, DateTimeOffset PublishedAtUtc);
