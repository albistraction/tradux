using Tradux.Application.Testing;
using Tradux.Domain.Layers;

namespace Tradux.Application.Layers;

/// <summary>XSD de destino carregado pela camada base (nome relativo → bytes).</summary>
public sealed record LayerSchema(IReadOnlyDictionary<string, byte[]> Files, string EntryPoint, string? RootElementName = null)
{
    /// <exception cref="ArgumentException">Sem arquivo, sem ponto de entrada ou Base64 inválido.</exception>
    public static LayerSchema FromUpload(Contracts.Design.SchemaUpload upload) =>
        new(Snapshots.SchemaUploads.Decode(upload), upload.EntryPoint, upload.RootElementName);
}

/// <summary>
/// Versão imutável de uma camada (ADR-0006/ADR-0010). Base traz
/// <see cref="Schema"/> e nenhum <see cref="Parent"/>; produto aponta para
/// uma base; cliente aponta para um produto ou direto para uma base.
/// <see cref="Shared"/> permite que camadas de outras organizações a usem
/// como pai (ex.: produto do fornecedor de software usado pelos clientes).
/// </summary>
public sealed record TemplateLayer
{
    public required LayerRef Ref { get; init; }
    public required LayerKind Kind { get; init; }
    public LayerRef? Parent { get; init; }
    public bool Shared { get; init; }
    public LayerSchema? Schema { get; init; }

    /// <summary>XSD de origem (só na base, opcional — ADR-0015): o template resolvido aceita XML nesse formato.</summary>
    public LayerSchema? SourceSchema { get; init; }
    public IReadOnlyList<LayerOverride> Overrides { get; init; } = [];
    public IReadOnlyList<LookupOverride> LookupOverrides { get; init; } = [];
    public required string ContentHash { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string CreatedBy { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Dados para criar uma nova versão de camada (a versão é atribuída pelo repositório).</summary>
public sealed record NewLayerVersion
{
    public required string OrganizationId { get; init; }
    public required string LayerId { get; init; }
    public required LayerKind Kind { get; init; }
    public LayerRef? Parent { get; init; }
    public bool Shared { get; init; }
    public LayerSchema? Schema { get; init; }

    /// <summary>XSD de origem (só na base, opcional — ADR-0015).</summary>
    public LayerSchema? SourceSchema { get; init; }
    public IReadOnlyList<LayerOverride> Overrides { get; init; } = [];
    public IReadOnlyList<LookupOverride> LookupOverrides { get; init; } = [];
    public string? Comment { get; init; }
}

/// <summary>Um passo da história de um caminho: qual camada fez o quê.</summary>
public sealed record RuleTraceStep(LayerRef Layer, LayerKind Kind, OverrideOperation Operation, string? Comment = null);

/// <summary>
/// Explicação de um caminho resolvido (seção 16: "a resolução deve explicar
/// a origem de cada regra"): de onde veio a regra final e toda a história
/// (definida na base, substituída pelo produto, removida pelo cliente…).
/// <see cref="FinalSource"/> é nulo quando o caminho terminou removido.
/// </summary>
/// <param name="FinalSource">Camada da regra publicada; nulo se removida ou não gerada.</param>
/// <param name="History">Cada passo, da base ao topo.</param>
/// <param name="Note">Por que uma regra definida não foi gerada, quando for o caso.</param>
public sealed record RuleTrace(LayerRef? FinalSource, IReadOnlyList<RuleTraceStep> History, string? Note = null);

public sealed record ManifestLayer(LayerRef Ref, LayerKind Kind, string ContentHash);

/// <summary>
/// Manifesto gravado no snapshot publicado (ADR-0003 §3): camadas exatas
/// usadas e proveniência de cada caminho e tabela DE-PARA.
/// </summary>
/// <param name="Layers">Camadas exatas usadas (ref + hash), da base ao topo.</param>
/// <param name="Paths">Explicação de cada caminho de destino que alguma camada tocou.</param>
/// <param name="LookupTables">Explicação de cada tabela DE-PARA.</param>
/// <param name="Warnings">Avisos da resolução (não bloqueiam): remoção de regra inexistente, regra inativa.</param>
public sealed record SnapshotManifest(
    IReadOnlyList<ManifestLayer> Layers,
    IReadOnlyDictionary<string, RuleTrace> Paths,
    IReadOnlyDictionary<string, RuleTrace> LookupTables,
    IReadOnlyList<ResolutionConflict>? Warnings = null);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<UpdatePolicy>))]
public enum UpdatePolicy
{
    /// <summary>Nova versão de camada vira proposta com diferenças e conflitos, aplicada só por aprovação.</summary>
    Manual,

    /// <summary>Reaplica, testa e publica sozinha — só se tudo passar.</summary>
    Automatic,
}

/// <summary>Identidade de uma camada, sem versão.</summary>
public sealed record LayerIdentity(string OrganizationId, string LayerId)
{
    public static LayerIdentity Of(LayerRef reference) => new(reference.OrganizationId, reference.LayerId);
}

/// <summary>
/// Vínculo de um template (chave de runtime) à sua cadeia de camadas:
/// política de atualização, cenários funcionais e a linhagem (todas as
/// camadas da cadeia, sem versão — usada para achar quem é afetado quando
/// uma camada ganha nova versão).
/// </summary>
public sealed record TemplateBinding
{
    public required TemplateKey Key { get; init; }
    public required UpdatePolicy Policy { get; init; }
    public required IReadOnlyList<LayerIdentity> Lineage { get; init; }
    public IReadOnlyList<FunctionalScenario> Scenarios { get; init; } = [];
}
