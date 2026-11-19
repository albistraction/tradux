using System.Text.RegularExpressions;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Servers;
using Tradux.Application.Security;
using Tradux.Infrastructure.MongoDb;

namespace Tradux.Api.IntegrationTests;

/// <summary>
/// Configuração de produção do cliente MongoDB e classificação de falha de
/// dependência (ADR-0014 §4; revisão da Fase 11, avisos 7 e 8). Sem servidor.
/// </summary>
public sealed class MongoResilienceSettingsTests
{
    [Fact]
    public void Server_selection_timeout_defaults_to_5s_and_the_connection_string_wins()
    {
        var defaults = new MongoDbOptions { ConnectionString = "mongodb://localhost:27017/?replicaSet=rs0" }.CreateClientSettings();
        var configured = new MongoDbOptions { ConnectionString = "mongodb://localhost:27017/", ServerSelectionTimeout = TimeSpan.FromSeconds(2) }.CreateClientSettings();
        var fromUri = new MongoDbOptions { ConnectionString = "mongodb://localhost:27017/?serverSelectionTimeoutMS=12000" }.CreateClientSettings();

        Assert.Equal(TimeSpan.FromSeconds(5), defaults.ServerSelectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), configured.ServerSelectionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), fromUri.ServerSelectionTimeout);
    }

    [Fact]
    public void Only_availability_failures_become_503()
    {
        var connection = new ConnectionId(new ServerId(new ClusterId(), new System.Net.DnsEndPoint("localhost", 27017)));

        Assert.True(HealthAndResilience.IsDependencyUnavailable(new TimeoutException("seleção de servidor")));
        Assert.True(HealthAndResilience.IsDependencyUnavailable(new MongoConnectionException(connection, "queda")));
        Assert.True(HealthAndResilience.IsDependencyUnavailable(new CredentialStoreUnavailableException("fora")));
        Assert.True(HealthAndResilience.IsDependencyUnavailable(new InvalidOperationException("embrulho", new TimeoutException())));

        // Timeout de regex (é TimeoutException) e erros comuns continuam 500.
        Assert.False(HealthAndResilience.IsDependencyUnavailable(new RegexMatchTimeoutException("a", "b", TimeSpan.FromSeconds(1))));
        Assert.False(HealthAndResilience.IsDependencyUnavailable(new InvalidOperationException("bug")));
    }
}
