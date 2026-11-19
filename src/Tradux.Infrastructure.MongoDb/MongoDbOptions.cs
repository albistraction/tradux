namespace Tradux.Infrastructure.MongoDb;

/// <summary>Configuração do adaptador MongoDB (seção <c>Tradux:MongoDb</c> na API).</summary>
public sealed class MongoDbOptions
{
    public const string SectionName = "Tradux:MongoDb";

    /// <summary>
    /// Precisa apontar para um replica set: publicação usa transação
    /// multi-documento e a invalidação de cache usa Change Streams (ADR-0005).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    public string DatabaseName { get; set; } = "tradux-mapping";

    public string SnapshotCollectionName { get; set; } = "compiledSnapshots";

    /// <summary>
    /// Tempo máximo para achar um servidor disponível (ADR-0014 §4): com o
    /// MongoDB fora, chave fria falha em segundos em vez dos 30 s do driver.
    /// <c>serverSelectionTimeoutMS</c> na string de conexão prevalece.
    /// </summary>
    public TimeSpan ServerSelectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Configurações do cliente a partir de <see cref="ConnectionString"/> com os padrões do projeto.</summary>
    public MongoDB.Driver.MongoClientSettings CreateClientSettings()
    {
        var settings = MongoDB.Driver.MongoClientSettings.FromConnectionString(ConnectionString);
        if (!ConnectionString.Contains("serverSelectionTimeoutMS", StringComparison.OrdinalIgnoreCase))
        {
            settings.ServerSelectionTimeout = ServerSelectionTimeout;
        }

        return settings;
    }
}
