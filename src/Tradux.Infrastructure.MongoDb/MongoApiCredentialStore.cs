using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Application.Security;

namespace Tradux.Infrastructure.MongoDb;

/// <summary>Credenciais de API (ADR-0012 §1): só o hash do segredo, índice único nele.</summary>
public sealed class MongoApiCredentialStore : IApiCredentialStore
{
    private readonly IMongoCollection<BsonDocument> _credentials;
    private readonly IndexGuard _indexes = new();

    public MongoApiCredentialStore(IMongoClient client, MongoDbOptions options)
    {
        _credentials = client.GetDatabase(options.DatabaseName).GetCollection<BsonDocument>("apiCredentials");
    }

    private async Task CreateIndexesAsync(CancellationToken cancellationToken)
    {
        await _credentials.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("secretHash"), new CreateIndexOptions { Name = "ux_credential_hash", Unique = true }),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("organizationId"), new CreateIndexOptions { Name = "ix_credential_org" }),
        ], cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiCredential?> FindBySecretHashAsync(string secretHash, CancellationToken cancellationToken = default)
    {
        BsonDocument? document;
        try
        {
            document = await _credentials.Find(Builders<BsonDocument>.Filter.Eq("secretHash", secretHash)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (MongoAvailability.IsUnavailable(ex))
        {
            throw new CredentialStoreUnavailableException("Armazenamento de credenciais indisponível.", ex);
        }

        return document is null ? null : ToCredential(document);
    }

    public async Task AddAsync(ApiCredential credential, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        await _credentials.InsertOneAsync(ToDocument(credential), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureAsync(ApiCredential credential, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        try
        {
            await _credentials.UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("secretHash", credential.SecretHash),
                new BsonDocument("$setOnInsert", ToDocument(credential)),
                new UpdateOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MongoException ex) when (DuplicateKey.Is(ex))
        {
            // Outra instância inseriu ao mesmo tempo — é o estado desejado.
        }
    }

    public async Task<bool> RevokeAsync(string organizationId, string credentialId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken = default)
    {
        var result = await _credentials.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", credentialId)
            & Builders<BsonDocument>.Filter.Eq("organizationId", organizationId)
            & Builders<BsonDocument>.Filter.Eq("revokedAtUtc", BsonNull.Value),
            Builders<BsonDocument>.Update.Set("revokedAtUtc", revokedAtUtc.UtcDateTime),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.ModifiedCount == 1;
    }

    public async Task<IReadOnlyList<ApiCredential>> ListAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var documents = await _credentials.Find(Builders<BsonDocument>.Filter.Eq("organizationId", organizationId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(ToCredential).ToList();
    }

    private static BsonDocument ToDocument(ApiCredential c) => new()
    {
        { "_id", c.Id },
        { "organizationId", c.OrganizationId },
        { "name", c.Name },
        { "secretHash", c.SecretHash },
        { "roles", new BsonArray(c.Roles) },
        { "createdAtUtc", c.CreatedAtUtc.UtcDateTime },
        { "createdBy", c.CreatedBy },
        { "revokedAtUtc", c.RevokedAtUtc is { } r ? r.UtcDateTime : BsonNull.Value },
    };

    private static ApiCredential ToCredential(BsonDocument d) => new()
    {
        Id = d["_id"].AsString,
        OrganizationId = d["organizationId"].AsString,
        Name = d["name"].AsString,
        SecretHash = d["secretHash"].AsString,
        Roles = d["roles"].AsBsonArray.Select(r => r.AsString).ToList(),
        CreatedAtUtc = new DateTimeOffset(d["createdAtUtc"].ToUniversalTime(), TimeSpan.Zero),
        CreatedBy = d["createdBy"].AsString,
        RevokedAtUtc = d["revokedAtUtc"].IsBsonNull ? null : new DateTimeOffset(d["revokedAtUtc"].ToUniversalTime(), TimeSpan.Zero),
    };
}
