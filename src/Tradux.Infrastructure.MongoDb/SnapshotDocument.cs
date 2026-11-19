using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Tradux.Infrastructure.MongoDb;

/// <summary>
/// Documento da coleção <c>compiledSnapshots</c> (ADR-0004): autocontido e
/// denormalizado — chave lógica, versão, hash, status e todo o conteúdo
/// necessário para recompilar (XSDs em binário, mapeamento e lookups em
/// JSON canônico, ver <c>SnapshotJson</c>). O mapeamento fica como texto
/// JSON, não como sub-documento BSON: a conversão JSON→BSON transformaria
/// números decimais em double e mudaria a escala de literais (ex.:
/// <c>10.50</c> → <c>10.5</c>), quebrando o round-trip exato.
/// </summary>
internal sealed class SnapshotDocument
{
    public const string ActiveStatus = "active";
    public const string SupersededStatus = "superseded";

    /// <summary>Nomes BSON usados fora dos atributos (projeções e Change Stream).</summary>
    public static class Fields
    {
        public const string SnapshotVersion = "snapshotVersion";
        public const string ContentHash = "contentHash";
        public const string Status = "status";
        public const string PublishedAtUtc = "publishedAtUtc";
    }

    [BsonId]
    public ObjectId Id { get; set; }

    [BsonElement(TemplateKeyFields.OrganizationId)]
    public string OrganizationId { get; set; } = string.Empty;

    [BsonElement(TemplateKeyFields.TemplateId)]
    public string TemplateId { get; set; } = string.Empty;

    [BsonElement(TemplateKeyFields.Operation)]
    public string Operation { get; set; } = string.Empty;

    [BsonElement(Fields.SnapshotVersion)]
    public int SnapshotVersion { get; set; }

    [BsonElement(Fields.ContentHash)]
    public string ContentHash { get; set; } = string.Empty;

    [BsonElement(Fields.Status)]
    public string Status { get; set; } = ActiveStatus;

    [BsonElement(Fields.PublishedAtUtc)]
    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime PublishedAtUtc { get; set; }

    [BsonElement("schemaEntryPoint")]
    public string SchemaEntryPoint { get; set; } = string.Empty;

    [BsonElement("rootElementName")]
    [BsonIgnoreIfNull]
    public string? RootElementName { get; set; }

    [BsonElement("schemaFiles")]
    public List<SchemaFileDocument> SchemaFiles { get; set; } = [];

    [BsonElement("mappingJson")]
    public string MappingJson { get; set; } = string.Empty;

    [BsonElement("lookupTablesJson")]
    public string LookupTablesJson { get; set; } = "{}";

    /// <summary>Manifesto de camadas e proveniência (JSON canônico); ausente em snapshot publicado sem camadas.</summary>
    [BsonElement("manifestJson")]
    [BsonIgnoreIfNull]
    public string? ManifestJson { get; set; }

    /// <summary>Cenários funcionais que aprovaram a versão (JSON canônico; Fase 10).</summary>
    [BsonElement("scenariosJson")]
    [BsonIgnoreIfNull]
    public string? ScenariosJson { get; set; }

    /// <summary>XSD de origem (Fase 12, ADR-0015); ausente = template sem entrada XML.</summary>
    [BsonElement("sourceSchema")]
    [BsonIgnoreIfNull]
    public SourceSchemaDocument? SourceSchema { get; set; }
}

/// <remarks>Snapshots antigos podem trazer <c>contractId</c>/<c>contractVersion</c> (ADR-0025): ignorados na leitura.</remarks>
[BsonIgnoreExtraElements]
internal sealed class SourceSchemaDocument
{
    [BsonElement("entryPoint")]
    public string EntryPoint { get; set; } = string.Empty;

    [BsonElement("rootElementName")]
    [BsonIgnoreIfNull]
    public string? RootElementName { get; set; }

    [BsonElement("files")]
    public List<SchemaFileDocument> Files { get; set; } = [];

    /// <summary>Extensões de origem de snapshots antigos (ADR-0016, JSON canônico); ausente = só o XSD.</summary>
    [BsonElement("extensionsJson")]
    [BsonIgnoreIfNull]
    public string? ExtensionsJson { get; set; }
}

internal sealed class SchemaFileDocument
{
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    [BsonElement("content")]
    public byte[] Content { get; set; } = [];
}
