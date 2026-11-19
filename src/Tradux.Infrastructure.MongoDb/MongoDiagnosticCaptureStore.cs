using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Tradux.Application;
using Tradux.Application.Diagnostics;

namespace Tradux.Infrastructure.MongoDb;

/// <summary>
/// Armazenamento da captura diagnóstica (seção 23, ADR-0012 §8): janelas em
/// <c>diagnosticCaptures</c> e registros em <c>diagnosticRecords</c>. O
/// conteúdo (entrada e XML, já mascarados) é criptografado com AES-GCM antes
/// de ir ao banco; a chave e o registro ficam ligados pelos dados associados
/// (id + chave do template). Índices TTL apagam registros e janelas
/// expirados sozinhos. Sem chave de 32 bytes configurada, nada é gravado.
/// Não é criptografia de documento fiscal emitido — é proteção de dado de
/// suporte em repouso, e fica na infraestrutura, nunca no núcleo.
/// </summary>
public sealed class MongoDiagnosticCaptureStore : IDiagnosticCaptureStore
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly IMongoCollection<BsonDocument> _windows;
    private readonly IMongoCollection<BsonDocument> _records;
    private readonly byte[]? _key;
    private readonly IndexGuard _indexes = new();

    /// <param name="client">Cliente MongoDB.</param>
    /// <param name="options">Banco.</param>
    /// <param name="encryptionKey">Chave AES de 32 bytes; nula = captura indisponível.</param>
    public MongoDiagnosticCaptureStore(IMongoClient client, MongoDbOptions options, byte[]? encryptionKey)
    {
        if (encryptionKey is not null && encryptionKey.Length != 32)
        {
            throw new ArgumentException("A chave de criptografia da captura diagnóstica precisa ter 32 bytes (AES-256).", nameof(encryptionKey));
        }

        var database = client.GetDatabase(options.DatabaseName);
        _windows = database.GetCollection<BsonDocument>("diagnosticCaptures");
        _records = database.GetCollection<BsonDocument>("diagnosticRecords");
        _key = encryptionKey;
        _keyId = encryptionKey is null ? null : Convert.ToHexStringLower(SHA256.HashData(encryptionKey))[..16];
    }

    // Identifica a chave sem revelá-la: após rotação, registros de outra chave
    // são ignorados na leitura (expiram sozinhos) em vez de falhar (revisão W8).
    private readonly string? _keyId;

    public bool CanProtect => _key is not null;

    private async Task CreateIndexesAsync(CancellationToken cancellationToken)
    {
        var expire = new CreateIndexOptions { Name = "ttl_expires", ExpireAfter = TimeSpan.Zero };
        await _records.Indexes.CreateManyAsync(
        [
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("expiresAtUtc"), expire),
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("templateKey").Descending("capturedAtUtc"), new CreateIndexOptions { Name = "ix_record_key" }),
        ], cancellationToken).ConfigureAwait(false);
        await _windows.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("untilUtc"), new CreateIndexOptions { Name = "ttl_until", ExpireAfter = TimeSpan.Zero }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task SetWindowAsync(CaptureWindow window, CancellationToken cancellationToken = default)
    {
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);
        await _windows.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", window.Key.ToString()),
            new BsonDocument
            {
                { "_id", window.Key.ToString() },
                { "organizationId", window.Key.OrganizationId },
                { "templateId", window.Key.TemplateId },
                { "operation", window.Key.Operation },
                { "untilUtc", window.UntilUtc.UtcDateTime },
                { "enabledBy", window.EnabledBy },
            },
            new ReplaceOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveWindowAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
        _windows.DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", key.ToString()), cancellationToken);

    public async Task<IReadOnlyList<CaptureWindow>> ListActiveWindowsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var documents = await _windows.Find(Builders<BsonDocument>.Filter.Gt("untilUtc", nowUtc.UtcDateTime)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return documents.Select(d => new CaptureWindow(
            TemplateKeyFields.Read(d),
            new DateTimeOffset(d["untilUtc"].ToUniversalTime(), TimeSpan.Zero),
            d["enabledBy"].AsString)).ToList();
    }

    public async Task AddRecordAsync(DiagnosticRecord record, CancellationToken cancellationToken = default)
    {
        var key = _key ?? throw new InvalidOperationException("Captura diagnóstica sem chave de criptografia.");
        await _indexes.EnsureAsync(CreateIndexesAsync, cancellationToken).ConfigureAwait(false);

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new SensitivePart(record.MaskedInputJson, record.MaskedXml));
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, cipher, tag, AssociatedData(record.Id, record.Key));
        }

        CryptographicOperations.ZeroMemory(plaintext);
        await _records.InsertOneAsync(new BsonDocument
        {
            { "_id", record.Id },
            { "templateKey", record.Key.ToString() },
            { "organizationId", record.Key.OrganizationId },
            { "templateId", record.Key.TemplateId },
            { "operation", record.Key.Operation },
            { "capturedAtUtc", record.CapturedAtUtc.UtcDateTime },
            { "expiresAtUtc", record.ExpiresAtUtc.UtcDateTime },
            { "correlationId", (BsonValue?)record.CorrelationId ?? BsonNull.Value },
            { "versionUsed", (BsonValue?)record.VersionUsed ?? BsonNull.Value },
            { "success", record.Success },
            { "errorCodes", record.ErrorCodes },
            { "keyId", _keyId },
            { "nonce", new BsonBinaryData(nonce) },
            { "tag", new BsonBinaryData(tag) },
            { "cipher", new BsonBinaryData(cipher) },
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiagnosticRecord>> ListRecordsAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        var aesKey = _key ?? throw new InvalidOperationException("Captura diagnóstica sem chave de criptografia.");
        var documents = await _records.Find(Builders<BsonDocument>.Filter.Eq("templateKey", key.ToString())
                & Builders<BsonDocument>.Filter.Eq("keyId", _keyId)
                & Builders<BsonDocument>.Filter.Gt("expiresAtUtc", DateTime.UtcNow))
            .Sort(Builders<BsonDocument>.Sort.Descending("capturedAtUtc")).ToListAsync(cancellationToken).ConfigureAwait(false);

        var records = new List<DiagnosticRecord>(documents.Count);
        using var aes = new AesGcm(aesKey, TagBytes);
        foreach (var d in documents)
        {
            var cipher = d["cipher"].AsByteArray;
            var plaintext = new byte[cipher.Length];
            aes.Decrypt(d["nonce"].AsByteArray, cipher, d["tag"].AsByteArray, plaintext, AssociatedData(d["_id"].AsString, key));
            var part = JsonSerializer.Deserialize<SensitivePart>(plaintext)!;
            CryptographicOperations.ZeroMemory(plaintext);
            records.Add(new DiagnosticRecord(
                d["_id"].AsString,
                key,
                new DateTimeOffset(d["capturedAtUtc"].ToUniversalTime(), TimeSpan.Zero),
                new DateTimeOffset(d["expiresAtUtc"].ToUniversalTime(), TimeSpan.Zero),
                d["correlationId"].IsBsonNull ? null : d["correlationId"].AsString,
                d["versionUsed"].IsBsonNull ? null : d["versionUsed"].AsString,
                d["success"].AsBoolean,
                d["errorCodes"].AsString,
                part.Input,
                part.Xml));
        }

        return records;
    }

    public async Task<long> DeleteRecordsAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
        (await _records.DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("templateKey", key.ToString()), cancellationToken).ConfigureAwait(false)).DeletedCount;

    private static byte[] AssociatedData(string id, TemplateKey key) => Encoding.UTF8.GetBytes($"{id}|{key}");

    private sealed record SensitivePart(string Input, string? Xml);
}
