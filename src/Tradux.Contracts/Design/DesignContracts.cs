using System.Text.Json;
using System.Text.Json.Serialization;
using Tradux.Domain.Mapping;

namespace Tradux.Contracts.Design;

// Contratos das rotas de desenho do editor (/v1/design). Nenhuma persiste
// nada — são o "validar rascunho" e "executar testes" da seção 26 sem publicação.

/// <summary>Um arquivo XSD enviado pelo editor (conteúdo em Base64).</summary>
public sealed record SchemaFileUpload(string Name, string ContentBase64);

/// <summary>Conjunto de XSDs (destino ou origem): arquivos, ponto de entrada e (opcional) elemento raiz.</summary>
public sealed record SchemaUpload(IReadOnlyList<SchemaFileUpload> Files, string EntryPoint, string? RootElementName = null);

[JsonConverter(typeof(JsonStringEnumConverter<DestinationNodeKind>))]
public enum DestinationNodeKind
{
    /// <summary>Elemento com valor simples — recebe um mapeamento de valor.</summary>
    Scalar,

    /// <summary>Elemento com filhos — agrupa mapeamentos; se repetível, vira bloco "for each".</summary>
    Complex,

    /// <summary>Grupo sem elemento próprio (xs:choice/xs:all/sequence aninhada) — só leitura nesta fase.</summary>
    Group,
}

/// <summary>
/// Nó da árvore de destino (seção 12: "navegar nas árvores de destino").
/// <see cref="Path"/> segue a mesma convenção de XPath dos diagnósticos
/// (<c>/DPS/tomador/cnpj</c>, sem índice), para o editor casar diagnóstico
/// e nó. <see cref="Mappable"/> é falso para conteúdo opaco (xs:any, conteúdo
/// simples com atributos, vazio) — o editor mostra, mas não deixa mapear.
/// <see cref="SignedByCaller"/> marca <c>ds:Signature</c>: quem chama o serviço assina (ADR-0025).
/// </summary>
public sealed record DestinationNode(
    string Name,
    string Path,
    DestinationNodeKind Kind,
    int MinOccurs,
    int? MaxOccurs,
    bool Nillable,
    string? ScalarType,
    string? DefaultValue,
    string? ContentModel,
    bool Mappable,
    IReadOnlyList<DestinationNode> Children,
    bool ContentRepeatable = false,
    bool SignedByCaller = false)
{
    [JsonIgnore]
    public bool IsRepeatable => MaxOccurs is null or > 1;
}

public sealed record DestinationTree(string RootElementName, string? TargetNamespace, string ContentHash, DestinationNode Root);

public sealed record OperatorParameterInfo(string Name, string Description, string Kind);

/// <summary>Operador do catálogo tipado (seção 13) como o editor o apresenta.</summary>
public sealed record OperatorInfo(
    string Name,
    string Category,
    string Description,
    int MinArguments,
    int? MaxArguments,
    string Result,
    bool RepeatsLastParameter,
    IReadOnlyList<OperatorParameterInfo> Parameters);

/// <summary>
/// Pedido de teste de rascunho: schema + mapeamento montado no editor + a
/// entrada do teste — XML de origem (<see cref="SourceXml"/>, com
/// <see cref="SourceSchema"/>) ou, sem ele, <c>Data</c> em JSON, com os mesmos
/// limites da geração — e XML esperado opcional, comparado semanticamente.
/// </summary>
public sealed record DraftTestRequest(SchemaUpload Schema, MappingTemplate Mapping, JsonElement Data)
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LookupTables { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    public string? ExpectedXml { get; init; }

    /// <summary>XSD de origem (ADR-0015): com <see cref="SourceXml"/>, a entrada do teste é XML nesse formato.</summary>
    public SchemaUpload? SourceSchema { get; init; }

    /// <summary>XML de entrada no formato do XSD de origem; quando informado, <c>Data</c> é ignorado.</summary>
    public string? SourceXml { get; init; }
}

/// <summary>
/// Campo do XSD de origem como o editor o navega (ADR-0015 §2): <see cref="Type"/> é
/// <c>text</c>, <c>number</c>, <c>date</c>, <c>object</c> ou <c>list</c>;
/// <see cref="Path"/> é o caminho com ponto que o motor resolve (relativo ao
/// escopo; dentro de lista, relativo ao item); <see cref="Item"/> descreve um
/// item de lista (com <c>$index</c> e, para lista de valores, <c>$value</c>).
/// </summary>
public sealed record SourceFieldNode(
    string Name,
    string Path,
    string Type,
    bool Required = false,
    bool Nillable = false,
    IReadOnlyList<SourceFieldNode>? Children = null,
    SourceFieldNode? Item = null);

/// <summary>Árvore de campos do XSD de origem (raiz = "(dados)", objeto).</summary>
public sealed record SourceFieldTree(string RootElementName, string? TargetNamespace, string ContentHash, SourceFieldNode Root);

/// <summary>
/// Resultado do teste de rascunho. <see cref="CompileDiagnostics"/> são os
/// achados que não dependem dos dados (obrigatório sem mapeamento, operador
/// inválido, conteúdo não suportado) — o editor marca os nós por XPath.
/// <see cref="Generation"/> é nulo quando a compilação já falhou.
/// <see cref="ExpectedXmlMatches"/> é nulo quando não foi enviado XML esperado.
/// </summary>
public sealed record DraftTestResult(
    bool Publishable,
    IReadOnlyList<Diagnostic> CompileDiagnostics,
    GenerationResult? Generation,
    bool? ExpectedXmlMatches,
    string? ContentHash);

/// <summary>
/// Pedido de testes estruturais automáticos de um rascunho (Fase 8, seção
/// 19): schema + mapeamento (+ tabelas DE-PARA). Os dados são gerados pela
/// suíte; nada é gravado.
/// </summary>
public sealed record DraftQualityRequest(SchemaUpload Schema, MappingTemplate Mapping)
{
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LookupTables { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();
}

/// <summary>
/// Resultado dos testes estruturais do rascunho. Com erro crítico de
/// compilação, <see cref="Report"/> vem vazio e <see cref="CompileDiagnostics"/>
/// explica. O relatório nunca traz os dados gerados nem o XML.
/// </summary>
public sealed record DraftQualityResult(bool Passed, IReadOnlyList<Diagnostic> CompileDiagnostics, Quality.QualityReport Report);

