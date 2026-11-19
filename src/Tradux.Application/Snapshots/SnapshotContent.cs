using Tradux.Contracts.Design;
using Tradux.Domain.Mapping;

namespace Tradux.Application.Snapshots;

/// <summary>
/// Conteúdo autocontido de um snapshot (ADR-0004): tudo o que é preciso para
/// recompilá-lo a partir de uma única leitura — os próprios arquivos XSD de
/// destino (nome relativo → bytes), o mapeamento já resolvido e as tabelas
/// de lookup. Nenhuma referência a outra coleção é seguida no cache-miss.
/// </summary>
public sealed record SnapshotContent
{
    public required IReadOnlyDictionary<string, byte[]> SchemaFiles { get; init; }
    public required string SchemaEntryPoint { get; init; }
    public string? RootElementName { get; init; }
    public required MappingTemplate Mapping { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LookupTables { get; init; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>();

    /// <summary>
    /// XSD de origem (ADR-0015): com ele o template aceita XML de entrada nesse
    /// formato, validado antes do mapeamento. Nulo = entrada só por JSON.
    /// Faz parte do hash do snapshot (só quando presente — hashes antigos não mudam).
    /// </summary>
    public SourceSchemaContent? SourceSchema { get; init; }

    /// <summary>Conteúdo a partir do que o editor envia (XSDs em Base64).</summary>
    /// <exception cref="ArgumentException">Sem arquivo, sem ponto de entrada ou Base64 inválido.</exception>
    public static SnapshotContent FromUpload(
        SchemaUpload schema,
        MappingTemplate mapping,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? lookupTables = null,
        SchemaUpload? sourceSchema = null) => new()
    {
        SchemaFiles = SchemaUploads.Decode(schema),
        SchemaEntryPoint = schema.EntryPoint,
        RootElementName = schema.RootElementName,
        Mapping = mapping,
        LookupTables = lookupTables ?? new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        SourceSchema = sourceSchema is null ? null : new SourceSchemaContent(SchemaUploads.Decode(sourceSchema), sourceSchema.EntryPoint, sourceSchema.RootElementName),
    };
}

/// <summary>Arquivos do XSD de origem (nome relativo → bytes), ponto de entrada e elemento raiz opcional.</summary>
public sealed record SourceSchemaContent(IReadOnlyDictionary<string, byte[]> Files, string EntryPoint, string? RootElementName = null)
{
    /// <summary>Extensões de origem de snapshots antigos (ADR-0016): o motor ainda as lê, nada novo as grava.</summary>
    public IReadOnlyList<Domain.SourceContracts.SourceExtension> Extensions { get; init; } = [];

    /// <summary>
    /// Hash estável do conjunto (arquivos, ponto de entrada, raiz) e, só quando houver, das
    /// extensões na forma canônica — conteúdos sem extensões mantêm o hash anterior à Fase 13.
    /// </summary>
    public string Hash() => SchemaFilesHash.Of(Files, EntryPoint, RootElementName,
        Extensions.Count > 0 ? "|ext:" + System.Text.Json.JsonSerializer.Serialize(Xsd.SourceExtensionMerger.Normalize(Extensions), SnapshotJson.Options) : "");
}
