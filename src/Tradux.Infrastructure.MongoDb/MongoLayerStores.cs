using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Application;
using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Testing;
using Tradux.Domain.Layers;

namespace Tradux.Infrastructure.MongoDb;

// Adaptadores MongoDB de camadas, vínculos, atualizações e auditoria (ADR-0004/ADR-0010).

/// <summary>Camadas versionadas, append-only: não existe atualização de conteúdo (ADR-0003).</summary>
public sealed class MongoTemplateLayerRepository : ITemplateLayerRepository
{
    private readonly IMongoCollection<BsonDocument> _layers;
    private readonly IndexGuard _indexes = new();

    public MongoTemplateLayerRepository(IMongoClient client, MongoDbOptions options)
    {
        _layers = client.GetDatabase(options.DatabaseName).GetCollection<BsonDocument>("templateLayers");
    }

    private Task CreateIndexesAsync(CancellationToken cancellationToken) =>
        _layers.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("organizationId").Ascending("layerId").Descending("version"),
            new CreateIndexOptions { Name = "ux_layer_version", Unique = true }), cancellationToken: cancellationToken);

    public async Task<TemplateLayer?> GetAsync(LayerRef reference, CancellationToken cancellationToken = default)
    {
        var document = await _layers.Find(Filter(reference.OrganizationId, reference.LayerId) & Builders<BsonDocument>.Filter.Eq("version", reference.Version))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToLayer(document);
    }

    public async Task<TemplateLayer?> GetLatestAsync(string organizationId, string layerId, CancellationToken cancellationToken = default)
    {
        var document = await _layers.Find(Filter(organizationId, layerId)).Sort(Builders<BsonDocument>.Sort.Descending("version"))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToLayer(document);
    }

    public async Task<TemplateLayer> AddVersionAsync(TemplateLayer layerWithoutVersion, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);

        // Versão = última + 1; conflito concorrente bate no índice único e repete.
        return await DuplicateKey.RetryAsync(async ct =>
        {
            var latest = await GetLatestAsync(layerWithoutVersion.Ref.OrganizationId, layerWithoutVersion.Ref.LayerId, ct).ConfigureAwait(false);
            var layer = layerWithoutVersion with { Ref = layerWithoutVersion.Ref with { Version = (latest?.Ref.Version ?? 0) + 1 } };
            await _layers.InsertOneAsync(ToDocument(layer), cancellationToken: ct).ConfigureAwait(false);
            return layer;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static FilterDefinition<BsonDocument> Filter(string organizationId, string layerId) =>
        Builders<BsonDocument>.Filter.Eq("organizationId", organizationId) & Builders<BsonDocument>.Filter.Eq("layerId", layerId);

    public async Task<TemplateLayer> ImportVersionAsync(TemplateLayer layer, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        try
        {
            await _layers.InsertOneAsync(ToDocument(layer), cancellationToken: cancellationToken).ConfigureAwait(false);
            return layer;
        }
        catch (MongoException ex) when (DuplicateKey.Is(ex))
        {
            return await GetAsync(layer.Ref, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Camada '{layer.Ref}' em conflito e não encontrada.");
        }
    }

    private static BsonDocument ToDocument(TemplateLayer layer) => new()
    {
        { "organizationId", layer.Ref.OrganizationId },
        { "layerId", layer.Ref.LayerId },
        { "version", layer.Ref.Version },
        { "kind", layer.Kind.ToString() },
        { "shared", layer.Shared },
        { "contentHash", layer.ContentHash },
        { "createdAtUtc", layer.CreatedAtUtc.UtcDateTime },
        { "createdBy", layer.CreatedBy },
        { "parent", layer.Parent is null ? BsonNull.Value : new BsonDocument
            {
                { "organizationId", layer.Parent.OrganizationId }, { "layerId", layer.Parent.LayerId }, { "version", layer.Parent.Version },
            } },
        { "schema", SchemaDocument(layer.Schema) },
        { "sourceSchema", SchemaDocument(layer.SourceSchema) },
        { "overridesJson", JsonPayload.Write(layer.Overrides) },
        { "lookupOverridesJson", JsonPayload.Write(layer.LookupOverrides) },
        { "comment", (BsonValue?)layer.Comment ?? BsonNull.Value },
    };

    private static BsonValue SchemaDocument(LayerSchema? schema) => schema is null ? BsonNull.Value : new BsonDocument
    {
        { "entryPoint", schema.EntryPoint },
        { "rootElementName", (BsonValue?)schema.RootElementName ?? BsonNull.Value },
        { "files", new BsonArray(schema.Files.OrderBy(f => f.Key, StringComparer.Ordinal)
            .Select(f => new BsonDocument { { "name", f.Key }, { "content", new BsonBinaryData(f.Value) } })) },
    };

    private static LayerSchema ToSchema(BsonDocument schema) => new(
        schema["files"].AsBsonArray.Select(f => f.AsBsonDocument)
            .ToDictionary(f => f["name"].AsString, f => f["content"].AsBsonBinaryData.Bytes, StringComparer.Ordinal),
        schema["entryPoint"].AsString,
        schema["rootElementName"].IsBsonNull ? null : schema["rootElementName"].AsString);

    private static TemplateLayer ToLayer(BsonDocument d)
    {
        var parent = d["parent"].IsBsonNull ? null : d["parent"].AsBsonDocument;
        return new TemplateLayer
        {
            Ref = new LayerRef(d["organizationId"].AsString, d["layerId"].AsString, d["version"].AsInt32),
            Kind = Enum.Parse<LayerKind>(d["kind"].AsString),
            Shared = d["shared"].AsBoolean,
            ContentHash = d["contentHash"].AsString,
            CreatedAtUtc = new DateTimeOffset(d["createdAtUtc"].ToUniversalTime(), TimeSpan.Zero),
            CreatedBy = d["createdBy"].AsString,
            Parent = parent is null ? null : new LayerRef(parent["organizationId"].AsString, parent["layerId"].AsString, parent["version"].AsInt32),
            Schema = d["schema"].IsBsonNull ? null : ToSchema(d["schema"].AsBsonDocument),
            SourceSchema = d.TryGetValue("sourceSchema", out var source) && !source.IsBsonNull ? ToSchema(source.AsBsonDocument) : null,
            Overrides = JsonPayload.Read<List<LayerOverride>>(d["overridesJson"]),
            LookupOverrides = JsonPayload.Read<List<LookupOverride>>(d["lookupOverridesJson"]),
            Comment = d["comment"].IsBsonNull ? null : d["comment"].AsString,
        };
    }
}

/// <summary>Vínculos template → cadeia de camadas; linhagem indexada para a propagação de atualizações.</summary>
public sealed class MongoTemplateBindingRepository : ITemplateBindingRepository
{
    private readonly IMongoCollection<BsonDocument> _bindings;
    private readonly IndexGuard _indexes = new();

    public MongoTemplateBindingRepository(IMongoClient client, MongoDbOptions options)
    {
        _bindings = client.GetDatabase(options.DatabaseName).GetCollection<BsonDocument>("templateBindings");
    }

    private Task CreateIndexesAsync(CancellationToken cancellationToken) =>
        _bindings.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("organizationId").Ascending("templateId").Ascending("operation"),
                new CreateIndexOptions { Name = "ux_binding_key", Unique = true }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("lineage"), new CreateIndexOptions { Name = "ix_lineage" }),
        ], cancellationToken);

    public async Task<TemplateBinding?> GetAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        var document = await _bindings.Find(TemplateKeyFields.Filter<BsonDocument>(key)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : ToBinding(document);
    }

    public async Task UpsertAsync(TemplateBinding binding, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        await _bindings.ReplaceOneAsync(TemplateKeyFields.Filter<BsonDocument>(binding.Key), ToDocument(binding), new ReplaceOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TemplateBinding>> FindByLineageAsync(LayerIdentity layer, CancellationToken cancellationToken = default)
    {
        var documents = await _bindings.Find(Builders<BsonDocument>.Filter.AnyEq("lineage", Lineage(layer))).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(ToBinding).ToList();
    }

    private static string Lineage(LayerIdentity layer) => $"{layer.OrganizationId}/{layer.LayerId}";

    private static BsonDocument ToDocument(TemplateBinding binding) => new()
    {
        { "organizationId", binding.Key.OrganizationId },
        { "templateId", binding.Key.TemplateId },
        { "operation", binding.Key.Operation },
        { "policy", binding.Policy.ToString() },
        { "lineage", new BsonArray(binding.Lineage.Select(Lineage)) },
        { "lineageJson", JsonPayload.Write(binding.Lineage) },
        { "scenariosJson", JsonPayload.Write(binding.Scenarios) },
    };

    private static TemplateBinding ToBinding(BsonDocument d) => new()
    {
        Key = TemplateKeyFields.Read(d),
        Policy = Enum.Parse<UpdatePolicy>(d["policy"].AsString),
        Lineage = JsonPayload.Read<List<LayerIdentity>>(d["lineageJson"]),
        Scenarios = JsonPayload.Read<List<FunctionalScenario>>(d["scenariosJson"]),
    };
}

/// <summary>Atualizações (propostas, aplicadas, falhas) por template.</summary>
public sealed class MongoTemplateUpdateRepository : ITemplateUpdateRepository
{
    private readonly IMongoCollection<BsonDocument> _updates;
    private readonly IndexGuard _indexes = new();

    public MongoTemplateUpdateRepository(IMongoClient client, MongoDbOptions options)
    {
        _updates = client.GetDatabase(options.DatabaseName).GetCollection<BsonDocument>("templateUpdates");
    }

    private Task CreateIndexesAsync(CancellationToken cancellationToken) =>
        _updates.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("organizationId").Ascending("templateId").Ascending("operation").Descending("createdAtUtc"),
            new CreateIndexOptions { Name = "ix_update_key" }), cancellationToken: cancellationToken);

    public async Task AddAsync(TemplateUpdate update, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        await _updates.InsertOneAsync(ToDocument(update), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<TemplateUpdate?> GetAsync(TemplateKey key, string updateId, CancellationToken cancellationToken = default)
    {
        var document = await _updates.Find(TemplateKeyFields.Filter<BsonDocument>(key) & Builders<BsonDocument>.Filter.Eq("_id", updateId)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return document is null ? null : JsonPayload.Read<TemplateUpdate>(document["payloadJson"]);
    }

    public async Task<bool> TransitionAsync(TemplateUpdate update, UpdateStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        // O filtro inclui o status esperado: a troca é atômica no documento.
        var result = await _updates.ReplaceOneAsync(
            TemplateKeyFields.Filter<BsonDocument>(update.Key) & Builders<BsonDocument>.Filter.Eq("_id", update.Id) & Builders<BsonDocument>.Filter.Eq("status", expectedStatus.ToString()),
            ToDocument(update), cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.MatchedCount == 1;
    }

    public async Task<IReadOnlyList<TemplateUpdate>> ListAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        var documents = await _updates.Find(TemplateKeyFields.Filter<BsonDocument>(key)).Sort(Builders<BsonDocument>.Sort.Descending("createdAtUtc")).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => JsonPayload.Read<TemplateUpdate>(d["payloadJson"])).ToList();
    }

    private static BsonDocument ToDocument(TemplateUpdate update) => new()
    {
        { "_id", update.Id },
        { "organizationId", update.Key.OrganizationId },
        { "templateId", update.Key.TemplateId },
        { "operation", update.Key.Operation },
        { "status", update.Status.ToString() },
        { "createdAtUtc", update.CreatedAtUtc.UtcDateTime },
        { "payloadJson", JsonPayload.Write(update) },
    };
}

/// <summary>Trilha de auditoria (seção 20): só metadados da ação — nunca payload fiscal.</summary>
public sealed class MongoAuditSink : IAuditSink
{
    private readonly IMongoCollection<BsonDocument> _events;
    private readonly IndexGuard _indexes = new();

    public MongoAuditSink(IMongoClient client, MongoDbOptions options)
    {
        _events = client.GetDatabase(options.DatabaseName).GetCollection<BsonDocument>("auditEvents");
    }

    private Task CreateIndexesAsync(CancellationToken cancellationToken) =>
        _events.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            Builders<BsonDocument>.IndexKeys.Ascending("organizationId").Descending("atUtc"),
            new CreateIndexOptions { Name = "ix_audit_org_time" }), cancellationToken: cancellationToken);

    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        await _events.InsertOneAsync(new BsonDocument
        {
            { "atUtc", auditEvent.AtUtc.UtcDateTime },
            { "organizationId", auditEvent.OrganizationId },
            { "actor", auditEvent.Actor },
            { "action", auditEvent.Action },
            { "target", auditEvent.Target },
            { "outcome", auditEvent.Outcome },
            { "details", new BsonDocument(auditEvent.Details.ToDictionary(d => d.Key, d => (object)d.Value)) },
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEvent>> ListAsync(string organizationId, int limit = 100, CancellationToken cancellationToken = default)
    {
        var documents = await _events.Find(Builders<BsonDocument>.Filter.Eq("organizationId", organizationId))
            .Sort(Builders<BsonDocument>.Sort.Descending("atUtc")).Limit(limit).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => new AuditEvent(
            new DateTimeOffset(d["atUtc"].ToUniversalTime(), TimeSpan.Zero),
            d["organizationId"].AsString,
            d["actor"].AsString,
            d["action"].AsString,
            d["target"].AsString,
            d["outcome"].AsString,
            d["details"].AsBsonDocument.ToDictionary(e => e.Name, e => e.Value.AsString))).ToList();
    }
}
