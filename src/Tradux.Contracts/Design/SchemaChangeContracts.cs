using System.Text.Json.Serialization;
using Tradux.Domain.Mapping;

namespace Tradux.Contracts.Design;

/// <summary>O que mudou num elemento entre duas versões do XSD de destino (ADR-0017).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SchemaChangeKind>))]
public enum SchemaChangeKind
{
    /// <summary>Elemento novo.</summary>
    Added,

    /// <summary>Elemento que não existe mais.</summary>
    Removed,

    /// <summary>Mesmo elemento (mesmo nome e tipo de nó) em outro lugar da árvore.</summary>
    Moved,

    /// <summary>Era valor simples e virou bloco (ou o contrário).</summary>
    KindChanged,

    /// <summary>Tipo do valor mudou (ex.: texto → decimal).</summary>
    TypeChanged,

    /// <summary>Passou a ser obrigatório (minOccurs 0 → 1).</summary>
    BecameRequired,

    /// <summary>Deixou de ser obrigatório.</summary>
    BecameOptional,

    /// <summary>Passou a repetir (maxOccurs &gt; 1).</summary>
    BecameRepeatable,

    /// <summary>Deixou de repetir.</summary>
    StoppedRepeating,

    /// <summary>Passou a aceitar (ou deixou de aceitar) <c>xsi:nil</c>.</summary>
    NillableChanged,

    /// <summary>Restrições do valor mudaram (tamanho, enumeração, pattern, limites, dígitos).</summary>
    RestrictionChanged,

    /// <summary>Valor padrão do XSD mudou.</summary>
    DefaultChanged,

    /// <summary>Ordem dos filhos (sequência de tags) mudou.</summary>
    OrderChanged,

    /// <summary>Modelo de conteúdo do bloco mudou (sequence/choice/all) ou o motor deixou de/passou a gerá-lo.</summary>
    ContentModelChanged,

    /// <summary>Elemento raiz mudou de nome.</summary>
    RootChanged,

    /// <summary>Namespace de destino mudou.</summary>
    NamespaceChanged,
}

/// <summary>O que o operador precisa fazer por causa da mudança.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SchemaChangeImpact>))]
public enum SchemaChangeImpact
{
    /// <summary>Um mapeamento existente deixou de valer: refazer (mover para o campo novo ou descartar).</summary>
    Remap,

    /// <summary>Campo obrigatório sem mapeamento: mapear antes de publicar.</summary>
    Map,

    /// <summary>Mapeamento continua, mas pode não servir mais (tipo, restrição, cardinalidade): revisar e testar.</summary>
    Review,

    /// <summary>Sem ação: o motor já acompanha (ex.: ordem das tags, campo opcional novo).</summary>
    Info,
}

/// <summary>
/// Uma mudança. <see cref="Path"/> é o caminho no XSD novo (mesma convenção da
/// árvore de destino) ou, se o elemento saiu, no antigo; <see cref="BeforePath"/>
/// é o caminho no XSD antigo quando difere. <see cref="Mapped"/>: o template
/// tinha mapeamento nesse elemento (ou dentro dele). <see cref="Suggestions"/>:
/// caminhos do XSD novo para onde o mapeamento provavelmente deve ir.
/// </summary>
public sealed record SchemaChange(
    string Path,
    SchemaChangeKind Kind,
    SchemaChangeImpact Impact,
    bool Mapped,
    string Message,
    string? Before = null,
    string? After = null,
    string? BeforePath = null,
    IReadOnlyList<string>? Suggestions = null);

public sealed record SchemaChangeSummary(int Remap, int Map, int Review, int Info);

/// <summary>Relatório de mudanças entre duas versões do XSD de destino, com o impacto no mapeamento.</summary>
public sealed record SchemaChangeReport(
    string BeforeRootElementName,
    string AfterRootElementName,
    string BeforeContentHash,
    string AfterContentHash,
    bool Identical,
    SchemaChangeSummary Summary,
    IReadOnlyList<SchemaChange> Changes);

/// <summary>
/// Comparação no editor: XSD atual do rascunho (<see cref="Before"/>), XSD novo
/// (<see cref="After"/>) e, opcionalmente, o mapeamento atual para calcular o impacto.
/// </summary>
public sealed record SchemaChangeRequest(SchemaUpload Before, SchemaUpload After, MappingTemplate? Mapping = null);

/// <summary>Comparação contra a versão ativa de um template publicado: só o XSD novo.</summary>
public sealed record TemplateSchemaChangeRequest(SchemaUpload After);
