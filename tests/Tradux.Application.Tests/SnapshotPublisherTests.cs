using System.Linq.Expressions;
using System.Text;
using Tradux.Application.Publishing;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Domain.Mapping;
using Tradux.Engine.Operators;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>
/// Núcleo de publicação: conteúdo que não importa, não compila ou tem
/// diagnóstico crítico de compilação nunca chega a ser gravado; hash de
/// conteúdo determinístico; gravação idempotente por conteúdo; cada
/// publicação compila uma vez só.
/// </summary>
public class SnapshotPublisherTests
{
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation);

    [Fact]
    public async Task Publishing_assigns_sequential_versions_and_keeps_a_single_active()
    {
        var repository = new InMemorySnapshotRepository();

        var v1 = await WriteAsync(repository, SnapshotFixtures.SimpleDps());
        var v2 = await WriteAsync(repository, SnapshotFixtures.SimpleDps("V2-"));

        Assert.Equal(1, v1.Version);
        Assert.Equal(2, v2.Version);
        var versions = await repository.ListVersionsAsync(Key);
        Assert.Equal([SnapshotStatus.Superseded, SnapshotStatus.Active], versions.OrderBy(v => v.Version).Select(v => v.Status));
    }

    [Fact]
    public async Task Publishing_identical_content_again_does_not_create_a_new_version()
    {
        var repository = new InMemorySnapshotRepository();

        var first = await WriteAsync(repository, SnapshotFixtures.SimpleDps());
        var second = await WriteAsync(repository, SnapshotFixtures.SimpleDps());

        Assert.Equal(first.Version, second.Version);
        Assert.Single(await repository.ListVersionsAsync(Key));
    }

    [Fact]
    public void Content_hash_is_deterministic_and_changes_with_content()
    {
        var compiler = new SnapshotCompiler();
        string Hash(SnapshotContent c) => SnapshotCompiler.ComputeContentHash(compiler.CompileContent(c).XsdContentHash, c);

        Assert.Equal(Hash(SnapshotFixtures.SimpleDps()), Hash(SnapshotFixtures.SimpleDps()));
        Assert.NotEqual(Hash(SnapshotFixtures.SimpleDps()), Hash(SnapshotFixtures.SimpleDps("V2-")));

        var otherLookup = SnapshotFixtures.SimpleDps() with
        {
            LookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["situacoes"] = new Dictionary<string, string> { ["1"] = "Outro texto" },
            },
        };
        Assert.NotEqual(Hash(SnapshotFixtures.SimpleDps()), Hash(otherLookup));
    }

    [Fact]
    public void Template_with_required_field_unmapped_is_refused_by_the_compilation_gate()
    {
        var content = SnapshotFixtures.SimpleDps();
        var incomplete = content with
        {
            Mapping = new MappingTemplate { Fields = content.Mapping.Fields.Where(f => f.DestinationElementName != "numero").ToList() },
        };

        Assert.Contains(Refusal(incomplete), d => d.Severity == DiagnosticSeverity.Critical && d.DestinationXPath == "/DPS/numero");
    }

    [Fact]
    public void Invalid_xsd_is_refused()
    {
        var content = SnapshotFixtures.SimpleDps() with
        {
            SchemaFiles = new Dictionary<string, byte[]> { ["exemplo-simples.xsd"] = Encoding.UTF8.GetBytes("<nao-e-xsd") },
        };

        Assert.Contains(Refusal(content), d => d.Code == Generation.XmlGenerationService.SnapshotCompilationFailedCode);
    }

    [Fact]
    public void Schema_file_name_with_path_traversal_is_refused()
    {
        var original = SnapshotFixtures.SimpleDps();
        var content = original with
        {
            SchemaFiles = new Dictionary<string, byte[]> { ["../exemplo-simples.xsd"] = original.SchemaFiles["exemplo-simples.xsd"] },
            SchemaEntryPoint = "../exemplo-simples.xsd",
        };

        AssertRefusedBeforeTests(Refusal(content));
    }

    [Fact]
    public void Operator_unknown_to_this_process_is_refused_at_publication()
    {
        var content = SnapshotFixtures.SimpleDps() with
        {
            Mapping = new MappingTemplate
            {
                Fields = SnapshotFixtures.SimpleDps().Mapping.Fields
                    .Select(f => f.DestinationElementName == "numero" ? new FieldMapping("numero", OperatorNode.Of("naoExiste", new FieldReferenceNode("Numero"))) : f)
                    .ToList(),
            },
        };

        AssertRefusedBeforeTests(Refusal(content));
    }

    [Fact]
    public async Task Content_publication_compiles_the_content_once_for_gates_and_write()
    {
        var builds = 0;
        var catalog = RuleOperatorCatalog.CreateDefault();
        catalog.Register(new RuleOperatorDefinition("contado", 1, 1, (_, args, _) =>
        {
            Interlocked.Increment(ref builds);
            return Expression.Convert(args[0], typeof(object));
        }));
        var content = SnapshotFixtures.SimpleDps() with
        {
            Mapping = new MappingTemplate
            {
                Fields = SnapshotFixtures.SimpleDps().Mapping.Fields
                    .Select(f => f.DestinationElementName == "numero" ? new FieldMapping("numero", OperatorNode.Of("contado", new FieldReferenceNode("Numero"))) : f)
                    .ToList(),
            },
        };
        var service = new ContentPublicationService(new InMemorySnapshotRepository(), new SnapshotCompiler(catalog), new InMemoryAuditSink());

        var result = await service.PublishAsync(Key, content, SnapshotFixtures.ScenariosFor(content), "teste");

        Assert.True(result.Success, string.Join(", ", result.Diagnostics.Select(d => d.Code)));
        Assert.Equal(1, builds);
    }

    /// <summary>Catálogo com um operador customizado (seção 13: extensão por novos operadores C#).</summary>
    internal static RuleOperatorCatalog CatalogWithIdentityOperator()
    {
        var catalog = RuleOperatorCatalog.CreateDefault();
        catalog.Register(new RuleOperatorDefinition("identidade", 1, 1, (_, args, _) => Expression.Convert(args[0], typeof(object))));
        return catalog;
    }

    private static async Task<SnapshotSummary> WriteAsync(InMemorySnapshotRepository repository, SnapshotContent content)
    {
        var publisher = new SnapshotPublisher(repository, new SnapshotCompiler(), Generation.GenerationLimits.Default);
        var candidate = publisher.Prepare(content, out var failure) ?? throw new Xunit.Sdk.XunitException(failure!.Message);
        return await publisher.WriteAsync(Key, candidate, manifest: null, await repository.GetActiveVersionAsync(Key), [], CancellationToken.None);
    }

    /// <summary>
    /// Por que o conteúdo não pode ser publicado. Sem cenários, só a recusa na
    /// preparação ou no gate de compilação deixa de trazer <see cref="FunctionalScenarioGate.ScenarioRequiredCode"/>
    /// (a compilação reprovada interrompe os demais gates).
    /// </summary>
    private static IReadOnlyList<Diagnostic> Refusal(SnapshotContent content)
    {
        var publisher = new SnapshotPublisher(new InMemorySnapshotRepository(), new SnapshotCompiler(), Generation.GenerationLimits.Default);
        return publisher.Prepare(content, out var failure) is { } candidate
            ? publisher.Gate(candidate, []).Failures
            : [failure!];
    }

    private static void AssertRefusedBeforeTests(IReadOnlyList<Diagnostic> refusal)
    {
        Assert.NotEmpty(refusal);
        Assert.DoesNotContain(refusal, d => d.Code == FunctionalScenarioGate.ScenarioRequiredCode);
    }
}
