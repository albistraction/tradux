using Tradux.Application.Caching;
using Tradux.Application.Generation;
using Tradux.Application.Layers;
using Tradux.Application.Packaging;
using Tradux.Application.Publishing;
using Tradux.Application.Snapshots;
using Tradux.Engine.Operators;
using Tradux.Application.Testing;

namespace Tradux.Application.Tests;

/// <summary>
/// Serviços de <c>Application</c> compostos como a API os compõe
/// (<c>ServiceRegistration</c>): o cache lê do repositório interno e quem
/// publica escreve pelo <see cref="CacheRefreshingSnapshotRepository"/>, então
/// publicar e gerar na mesma instância enxerga a versão nova na hora.
/// Só composição — nenhuma regra.
/// </summary>
internal sealed class TestServices
{
    public TestServices(InMemorySnapshotRepository? repository = null, RuleOperatorCatalog? operators = null, GenerationLimits? limits = null)
    {
        Repository = repository ?? new InMemorySnapshotRepository();
        Compiler = new SnapshotCompiler(operators);
        Cache = new CompiledSnapshotCache(Repository, Compiler);
        Snapshots = new CacheRefreshingSnapshotRepository(Repository, Cache);
        Generation = new XmlGenerationService(Cache, limits: limits);
        Publication = new ContentPublicationService(Snapshots, Compiler, Audit, limits);
        Layered = new LayeredTemplateService(Layers, Bindings, Updates, Snapshots, Audit, Compiler, limits);
        Packages = new PackageService(Snapshots, Layers, Bindings, Layered, Publication, Audit);
    }

    /// <summary>Repositório interno (contadores de leitura, ganchos de concorrência).</summary>
    public InMemorySnapshotRepository Repository { get; }

    /// <summary>Repositório que os serviços de escrita usam.</summary>
    public CacheRefreshingSnapshotRepository Snapshots { get; }

    public InMemoryLayerRepository Layers { get; } = new();

    public InMemoryBindingRepository Bindings { get; } = new();

    public InMemoryUpdateRepository Updates { get; } = new();

    public InMemoryAuditSink Audit { get; } = new();

    public SnapshotCompiler Compiler { get; }

    public CompiledSnapshotCache Cache { get; }

    public XmlGenerationService Generation { get; }

    public ContentPublicationService Publication { get; }

    public LayeredTemplateService Layered { get; }

    public PackageService Packages { get; }

    /// <summary>Publica e exige que os gates aprovem.</summary>
    public async Task<PublicationResult> PublishAsync(TemplateKey key, SnapshotContent content, IReadOnlyList<FunctionalScenario> scenarios, string actor = "teste")
    {
        var result = await Publication.PublishAsync(key, content, scenarios, actor);
        Assert.True(result.Success, string.Join(", ", result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        return result;
    }
}
