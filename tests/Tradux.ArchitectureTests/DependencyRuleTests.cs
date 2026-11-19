using System.Reflection;
using System.Xml.Linq;

namespace Tradux.ArchitectureTests;

/// <summary>
/// Limites de dependência de .claude/rules/dotnet.md e ADR-0007/ADR-0008,
/// verificados de duas formas: (1) referências de assembly efetivamente
/// compiladas, seguidas transitivamente entre os assemblies da solução; (2)
/// referências declaradas nos <c>.csproj</c> (pega também dependência
/// declarada mas ainda não usada). Falha aqui é bloqueio, não aviso
/// (.claude/rules/testing.md).
/// </summary>
public class DependencyRuleTests
{
    private static readonly string[] InfrastructureAndTransport =
    [
        "MongoDB.",
        "Microsoft.AspNetCore.",
        "Tradux.Infrastructure.",
        "Tradux.Api",
        "Tradux.Web",
    ];

    // Núcleo: além de infraestrutura/transporte, nada de rede, banco ou UI,
    // e nada das camadas acima dele.
    private static readonly string[] CoreForbidden =
    [
        .. InfrastructureAndTransport,
        "Tradux.Application",
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Data",
    ];

    // ADR-0013 §7: o RAG é apoio, fora da geração determinística — nem o
    // núcleo, nem Application o alcançam; e ele não alcança o motor.
    private const string Knowledge = "Tradux.Knowledge";

    // CLAUDE.md "Limites obrigatórios do núcleo" e ADR-0025: o microsserviço só
    // traduz — assinatura digital e criptografia de documento ficam com quem chama.
    // "Tradux.Connectors." não existe mais: a trava impede reintroduzir o conector de assinatura.
    private static readonly string[] SignatureConnector = ["Tradux.Connectors.", "System.Security.Cryptography.Xml"];

    public static TheoryData<string, string[]> Rules() => new()
    {
        { "Tradux.Domain", [.. CoreForbidden, Knowledge, .. SignatureConnector] },
        { "Tradux.Xsd", [.. CoreForbidden, Knowledge, .. SignatureConnector] },
        { "Tradux.Engine", [.. CoreForbidden, Knowledge, .. SignatureConnector] },
        { "Tradux.Contracts", [.. CoreForbidden, Knowledge, .. SignatureConnector] },
        // ADR-0007: Application conhece só portas, nunca tipos concretos de infraestrutura.
        { "Tradux.Application", [.. InfrastructureAndTransport, Knowledge, .. SignatureConnector] },
        { Knowledge, [.. InfrastructureAndTransport, "Tradux.Domain", "Tradux.Xsd", "Tradux.Engine", "Tradux.Contracts", "Tradux.Application"] },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Compiled_assembly_references_respect_the_layer_rules(string assemblyName, string[] forbiddenPrefixes)
    {
        var violations = TransitiveSolutionReferences(assemblyName)
            .Where(reference => forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(violations.Count == 0, $"{assemblyName} depende de: {string.Join(", ", violations)}");
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void Declared_project_and_package_references_respect_the_layer_rules(string projectName, string[] forbiddenPrefixes)
    {
        var project = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", projectName, projectName + ".csproj"));
        var declared = project.Descendants()
            .Where(e => e.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value))
            .ToList();

        var violations = declared
            .Where(reference => forbiddenPrefixes.Any(prefix => reference.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(violations.Count == 0, $"{projectName}.csproj declara: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Web_editor_talks_to_the_api_only_over_http()
    {
        // CONTAINERS.md: Tradux.Web não referencia nenhum projeto .NET
        // interno — só HTTP/JSON com a API.
        var project = XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", "Tradux.Web", "Tradux.Web.csproj"));

        Assert.DoesNotContain(project.Descendants(), e => e.Name.LocalName == "ProjectReference");
        Assert.DoesNotContain(project.Descendants(), e =>
            e.Name.LocalName == "PackageReference" && e.Attribute("Include")!.Value.StartsWith("Tradux.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_rules_actually_detect_a_violation()
    {
        // Controle negativo: a API depende de MongoDB e de Infrastructure —
        // se a varredura não enxergar isso, as regras acima são vazias.
        var apiReferences = TransitiveSolutionReferences("Tradux.Api");

        Assert.Contains(apiReferences, r => r.StartsWith("MongoDB.", StringComparison.Ordinal));
        Assert.Contains("Tradux.Infrastructure.MongoDb", apiReferences);
    }

    /// <summary>Nomes de todos os assemblies alcançáveis a partir de <paramref name="rootAssemblyName"/>, seguindo só assemblies da solução.</summary>
    private static HashSet<string> TransitiveSolutionReferences(string rootAssemblyName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([rootAssemblyName]);
        while (queue.Count > 0)
        {
            var assembly = Assembly.Load(new AssemblyName(queue.Dequeue()));
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name!;
                if (seen.Add(name) && name.StartsWith("Tradux.", StringComparison.Ordinal))
                {
                    queue.Enqueue(name);
                }
            }
        }

        return seen;
    }
}
