using MongoDB.Driver;
using Tradux.Application.Caching;
using Tradux.Application.Design;
using Tradux.Application.Diagnostics;
using Tradux.Application.Generation;
using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Publishing;
using Tradux.Application.Security;
using Tradux.Application.Snapshots;
using Tradux.Engine.Operators;
using Tradux.Infrastructure.MongoDb;
using Tradux.Knowledge;

namespace Tradux.Api;

/// <summary>
/// Raiz de composição da API: liga as portas de <c>Application</c> aos
/// adaptadores MongoDB. É o único lugar da API que conhece infraestrutura —
/// endpoints só falam com os serviços de <c>Application</c>.
/// </summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddTradux(this IServiceCollection services, IConfiguration configuration)
    {
        var mongoOptions = configuration.Section<MongoDbOptions>(MongoDbOptions.SectionName);
        var securityOptions = configuration.Section<SecurityOptions>(SecurityOptions.SectionName);
        var generationOptions = configuration.Section<GenerationOptions>(GenerationOptions.SectionName);

        services.AddSingleton(mongoOptions);
        services.AddSingleton(configuration.Section<SnapshotCacheOptions>(SnapshotCacheOptions.SectionName));
        services.AddSingleton(securityOptions);
        services.AddSingleton(generationOptions);
        services.AddSingleton(configuration.Section<DesignOptions>(DesignOptions.SectionName));
        var knowledgeOptions = configuration.Section<KnowledgeOptions>(KnowledgeOptions.SectionName);
        services.AddSingleton(sp =>
        {
            var design = sp.GetRequiredService<DraftDesignService>();
            var logger = sp.GetRequiredService<ILogger<KnowledgeService>>();
            return new KnowledgeService(
                knowledgeOptions,
                () => design.ListOperators().Select(o => new OperatorDoc(o.Name, o.Category, o.Description,
                    o.Parameters.Select(p => new OperatorParameterDoc(p.Name, p.Kind, p.Description)).ToList())),
                typeof(DraftDesignService).Assembly.GetName().Version?.ToString() ?? "0",
                ex => logger.LogWarning("Indexação do conhecimento falhou ({Error}); nova tentativa na próxima busca.", ex.GetType().Name));
        });
        var diagnosticOptions = configuration.Section<DiagnosticCaptureOptions>(DiagnosticCaptureOptions.SectionName);
        var diagnosticKey = configuration.Section<DiagnosticStoreOptions>(DiagnosticCaptureOptions.SectionName).KeyBytes();
        services.AddSingleton(diagnosticOptions);
        services.AddSingleton<IDiagnosticCaptureStore>(sp => new MongoDiagnosticCaptureStore(sp.GetRequiredService<IMongoClient>(), sp.GetRequiredService<MongoDbOptions>(), diagnosticKey));
        services.AddSingleton(sp => new DiagnosticCaptureService(sp.GetRequiredService<IDiagnosticCaptureStore>(), sp.GetRequiredService<IAuditSink>(), diagnosticOptions));
        services.AddHostedService<DiagnosticCaptureRefreshService>();
        services.AddSingleton<IMongoClient>(_ =>
        {
            if (string.IsNullOrWhiteSpace(mongoOptions.ConnectionString))
            {
                throw new InvalidOperationException(
                    $"Configuração '{MongoDbOptions.SectionName}:ConnectionString' é obrigatória (replica set, ver ADR-0005).");
            }

            return new MongoClient(mongoOptions.CreateClientSettings());
        });

        services.AddSingleton<MongoTemplateSnapshotRepository>();
        // Toda gravação de snapshot atualiza o cache desta instância; o cache lê direto do Mongo.
        services.AddSingleton<ITemplateSnapshotRepository>(sp => new CacheRefreshingSnapshotRepository(
            sp.GetRequiredService<MongoTemplateSnapshotRepository>(), sp.GetRequiredService<CompiledSnapshotCache>()));
        services.AddSingleton<ISnapshotChangeFeed, MongoSnapshotChangeFeed>();
        services.AddSingleton<ITemplateCatalog>(sp => sp.GetRequiredService<MongoTemplateSnapshotRepository>());
        services.AddSingleton<ITemplateLayerRepository, MongoTemplateLayerRepository>();
        services.AddSingleton<ITemplateBindingRepository, MongoTemplateBindingRepository>();
        services.AddSingleton<ITemplateUpdateRepository, MongoTemplateUpdateRepository>();
        services.AddSingleton<IAuditSink, MongoAuditSink>();
        services.AddSingleton<IApiCredentialStore, MongoApiCredentialStore>();
        services.AddSingleton(sp => new CredentialService(
            sp.GetRequiredService<IApiCredentialStore>(), sp.GetRequiredService<IAuditSink>(), cacheTtl: securityOptions.CredentialCacheTtl, staleGrace: securityOptions.CredentialStaleGrace));
        services.AddSingleton(sp => new Tradux.Application.Packaging.PackageService(
            sp.GetRequiredService<ITemplateSnapshotRepository>(),
            sp.GetRequiredService<ITemplateLayerRepository>(),
            sp.GetRequiredService<ITemplateBindingRepository>(),
            sp.GetRequiredService<LayeredTemplateService>(),
            sp.GetRequiredService<ContentPublicationService>(),
            sp.GetRequiredService<IAuditSink>()));
        services.AddSingleton(sp => new ContentPublicationService(
            sp.GetRequiredService<ITemplateSnapshotRepository>(), sp.GetRequiredService<SnapshotCompiler>(), sp.GetRequiredService<IAuditSink>(),
            sp.GetRequiredService<GenerationLimits>()));

        services.AddSingleton(_ => RuleOperatorCatalog.CreateDefault());
        services.AddSingleton(sp => new SnapshotCompiler(sp.GetRequiredService<RuleOperatorCatalog>()));
        services.AddSingleton(sp => new CompiledSnapshotCache(
            sp.GetRequiredService<MongoTemplateSnapshotRepository>(), sp.GetRequiredService<SnapshotCompiler>()));
        services.AddSingleton(sp => new XmlGenerationService(
            sp.GetRequiredService<CompiledSnapshotCache>(),
            generationOptions.MaxCandidateVersions,
            sp.GetRequiredService<GenerationLimits>()));
        services.AddSingleton(configuration.Section<GenerationLimits>(GenerationLimits.SectionName));
        services.AddSingleton(sp => new DraftDesignService(
            sp.GetRequiredService<RuleOperatorCatalog>(), limits: sp.GetRequiredService<GenerationLimits>(), audit: sp.GetRequiredService<IAuditSink>()));
        services.AddSingleton(sp => new LayeredTemplateService(
            sp.GetRequiredService<ITemplateLayerRepository>(),
            sp.GetRequiredService<ITemplateBindingRepository>(),
            sp.GetRequiredService<ITemplateUpdateRepository>(),
            sp.GetRequiredService<ITemplateSnapshotRepository>(),
            sp.GetRequiredService<IAuditSink>(),
            sp.GetRequiredService<SnapshotCompiler>(),
            sp.GetRequiredService<GenerationLimits>()));
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SnapshotCacheInvalidator>>();
            return new SnapshotCacheInvalidator(
                sp.GetRequiredService<CompiledSnapshotCache>(),
                sp.GetRequiredService<ITemplateSnapshotRepository>(),
                ex => logger.LogWarning(ex, "Falha na invalidação do cache de snapshots; nova tentativa em seguida."));
        });

        services.AddHostedService<BootstrapCredentialsInitializer>();
        services.AddHostedService<SnapshotCacheInvalidationService>();

        return services;
    }
}

/// <summary>Grava as credenciais iniciais da configuração (idempotente).</summary>
internal sealed class BootstrapCredentialsInitializer(CredentialService credentials, SecurityOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var bootstrap in options.BootstrapCredentials)
        {
            if (bootstrap.SecretSha256.Length != 64 || !bootstrap.SecretSha256.All(Uri.IsHexDigit))
            {
                throw new InvalidOperationException($"Credencial inicial '{bootstrap.Name}': SecretSha256 precisa ser o SHA-256 hexadecimal do segredo (nunca o segredo).");
            }

            await credentials.EnsureBootstrapAsync(bootstrap.OrganizationId, bootstrap.Name, bootstrap.SecretSha256, bootstrap.Roles, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Renova, em memória, quais chaves têm captura diagnóstica ligada (outras instâncias podem ligar/desligar).</summary>
internal sealed class DiagnosticCaptureRefreshService(DiagnosticCaptureService capture, ILogger<DiagnosticCaptureRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            try
            {
                await capture.RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Falha ao renovar o estado da captura diagnóstica ({Error}); nova tentativa em seguida.", ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Executa o feed de Change Streams e o poll de segurança em segundo plano (ADR-0005).</summary>
internal sealed class SnapshotCacheInvalidationService(
    SnapshotCacheInvalidator invalidator, ISnapshotChangeFeed changeFeed, SnapshotCacheOptions options) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.InvalidationEnabled)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(
            invalidator.RunChangeFeedAsync(changeFeed, options.ChangeFeedRetryDelay, stoppingToken),
            invalidator.RunPollingAsync(options.PollInterval, stoppingToken));
    }
}
