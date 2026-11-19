using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Tradux.Application.Security;
using Tradux.Infrastructure.MongoDb;

namespace Tradux.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Sobe a API real (mesmo <c>Program</c> de produção) contra o MongoDB do
/// <see cref="MongoFixture"/>, num banco exclusivo. O cliente MongoDB da API
/// é instrumentado com <see cref="Recorder"/>; publicações de teste feitas
/// por <see cref="CreateDirectPublisher"/> usam outro cliente — como outra
/// instância do serviço faria.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly MongoFixture _mongo;
    private readonly bool _invalidationEnabled;
    private readonly bool _diagnosticsKey;
    private readonly TelemetryCapture? _telemetry;
    private readonly TimeSpan? _serverSelectionTimeout;
    private readonly IReadOnlyDictionary<string, string>? _settings;

    /// <summary>Chave AES de teste da captura diagnóstica (32 bytes fixos).</summary>
    public static readonly byte[] DiagnosticsKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    /// <param name="kestrel">Serve por Kestrel com sockets reais (carga da Fase 11) em vez do TestServer em memória.</param>
    /// <param name="serverSelectionTimeout">Timeout curto do driver (testes com MongoDB derrubado).</param>
    public ApiFactory(
        MongoFixture mongo, bool invalidationEnabled = true, bool diagnosticsKey = true, TelemetryCapture? telemetry = null,
        bool kestrel = false, TimeSpan? serverSelectionTimeout = null, MongoDbOptions? sharedDatabase = null,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        _mongo = mongo;
        _invalidationEnabled = invalidationEnabled;
        _diagnosticsKey = diagnosticsKey;
        _telemetry = telemetry;
        _serverSelectionTimeout = serverSelectionTimeout;
        _settings = settings;
        // Mesmo banco = outra instância da mesma implantação (testes multi-instância).
        MongoOptions = sharedDatabase ?? mongo.NewOptions();
        if (kestrel)
        {
            UseKestrel(0);
            StartServer();
        }
    }

    public MongoDbOptions MongoOptions { get; }

    // Credenciais de teste (Fase 9): configuradas como credenciais iniciais —
    // a API recebe só o hash, como em produção.
    public const string AdminA = "tradux_test-admin-org-a";
    public const string AdminB = "tradux_test-admin-org-b";
    public const string RuntimeA = "tradux_test-runtime-org-a";
    public const string EditorA = "tradux_test-editor-org-a";
    public const string AuditorA = "tradux_test-auditor-org-a";

    private static readonly (string Secret, string Organization, string Role)[] Credentials =
    [
        (AdminA, "org-a", Roles.Admin),
        (AdminB, "org-b", Roles.Admin),
        (RuntimeA, "org-a", Roles.Runtime),
        (EditorA, "org-a", Roles.Editor),
        (AuditorA, "org-a", Roles.Auditor),
    ];

    /// <summary>Cliente HTTP com a credencial informada (nula = sem credencial).</summary>
    public HttpClient CreateClient(string? secret)
    {
        var client = CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = null;
        if (secret is not null)
        {
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", secret);
        }

        return client;
    }

    /// <summary><see cref="WebApplicationFactory{T}.CreateClient()"/> usa a credencial admin da org-a.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("ApiKey", AdminA);
    }

    public MongoCommandRecorder Recorder { get; } = new();

    /// <summary>Publicador direto no mesmo banco, com cliente MongoDB próprio (não instrumentado).</summary>
    public DirectPublisher CreateDirectPublisher() =>
        new(new MongoTemplateSnapshotRepository(_mongo.CreateClient(), MongoOptions), new MongoAuditSink(_mongo.CreateClient(), MongoOptions));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Tradux:MongoDb:ConnectionString", MongoOptions.ConnectionString);
        builder.UseSetting("Tradux:Knowledge:CorpusRoot", Tradux.Tests.Shared.RepositoryPaths.Root);
        foreach (var (key, value) in _settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
        if (_diagnosticsKey)
        {
            builder.UseSetting("Tradux:Diagnostics:EncryptionKey", Convert.ToBase64String(DiagnosticsKey));
        }

        if (_telemetry is { } telemetry)
        {
            // appsettings fixam Information/Warning; o gate varre Debug de verdade (revisão W2).
            builder.UseSetting("Logging:LogLevel:Default", "Debug");
            builder.UseSetting("Logging:LogLevel:Microsoft", "Debug");
            builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Debug");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug).AddProvider(telemetry));
        }

        for (var i = 0; i < Credentials.Length; i++)
        {
            var (secret, organization, role) = Credentials[i];
            builder.UseSetting($"Tradux:Security:BootstrapCredentials:{i}:OrganizationId", organization);
            builder.UseSetting($"Tradux:Security:BootstrapCredentials:{i}:Name", $"teste-{role}-{organization}");
            builder.UseSetting($"Tradux:Security:BootstrapCredentials:{i}:SecretSha256", CredentialSecrets.Hash(secret));
            builder.UseSetting($"Tradux:Security:BootstrapCredentials:{i}:Roles:0", role);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<MongoDbOptions>();
            services.AddSingleton(MongoOptions);

            services.RemoveAll<SnapshotCacheOptions>();
            services.AddSingleton(new SnapshotCacheOptions
            {
                InvalidationEnabled = _invalidationEnabled,
                // Poll de segurança longo: nos testes de invalidação, quem
                // precisa agir é o Change Stream.
                PollInterval = TimeSpan.FromHours(1),
                ChangeFeedRetryDelay = TimeSpan.FromMilliseconds(200),
            });

            services.RemoveAll<IMongoClient>();
            services.AddSingleton(_mongo.CreateClient(Recorder, _serverSelectionTimeout));
        });
    }
}
