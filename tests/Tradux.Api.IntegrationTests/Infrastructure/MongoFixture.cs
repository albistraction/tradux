using System.Collections.Concurrent;
using EphemeralMongo;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Tradux.Infrastructure.MongoDb;

namespace Tradux.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Um <c>mongod</c> real por execução de testes, como replica set de um nó
/// (transações e Change Streams exigem replica set, ADR-0005). Cada teste
/// usa um banco próprio (<see cref="NewOptions"/>) para isolamento.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private IMongoRunner? _runner;

    public string ConnectionString => _runner?.ConnectionString ?? throw new InvalidOperationException("MongoDB não iniciado.");

    public async Task InitializeAsync()
    {
        _runner = await MongoRunner.RunAsync(new MongoRunnerOptions { UseSingleNodeReplicaSet = true });
    }

    public Task DisposeAsync()
    {
        _runner?.Dispose();
        return Task.CompletedTask;
    }

    public MongoDbOptions NewOptions() => new()
    {
        ConnectionString = ConnectionString,
        DatabaseName = "tradux-test-" + Guid.NewGuid().ToString("N"),
    };

    public IMongoClient CreateClient(MongoCommandRecorder? recorder = null, TimeSpan? serverSelectionTimeout = null)
    {
        var settings = MongoClientSettings.FromConnectionString(ConnectionString);
        if (serverSelectionTimeout is { } timeout)
        {
            settings.ServerSelectionTimeout = timeout;
        }

        if (recorder is not null)
        {
            settings.ClusterConfigurator = cluster => cluster.Subscribe<CommandStartedEvent>(recorder.Record);
        }

        return new MongoClient(settings);
    }
}

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "mongo";
}

/// <summary>
/// Registra todo comando que o driver envia ao servidor — a prova direta de
/// "cache aquecido não acessa o MongoDB" (gate da Fase 5), independente de
/// qualquer contador dentro do nosso próprio código.
/// </summary>
public sealed class MongoCommandRecorder
{
    private static readonly HashSet<string> HandshakeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "hello", "isMaster", "ismaster", "buildInfo", "saslStart", "saslContinue", "ping", "endSessions",
    };

    private readonly ConcurrentQueue<string> _commands = new();
    private readonly ConcurrentQueue<string> _collections = new();

    /// <summary>Comandos de dados/negócio registrados (exclui handshake/monitoramento de conexão).</summary>
    public IReadOnlyList<string> Commands => _commands.ToList();

    public void Record(CommandStartedEvent e)
    {
        if (!HandshakeCommands.Contains(e.CommandName))
        {
            _commands.Enqueue($"{e.DatabaseNamespace.DatabaseName}.{e.CommandName}");
            // Coleção alvo (1º campo do comando: { find: "compiledSnapshots", ... }).
            _collections.Enqueue(e.Command.ElementCount > 0 && e.Command.GetElement(0).Value.IsString ? e.Command.GetElement(0).Value.AsString : "");
        }
    }

    /// <summary>Comandos de dados cuja coleção alvo é <paramref name="collection"/>.</summary>
    public int CountOn(string collection) => _collections.Count(c => c == collection);

    public void Clear()
    {
        _commands.Clear();
        _collections.Clear();
    }
}
