using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Tradux.Application;
using Tradux.Application.Generation;
using Tradux.Application.Layers;
using Tradux.Application.Testing;
using Tradux.Application.Ports;
using Tradux.Application.Publishing;
using Tradux.Application.Security;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Contracts.Design;
using Tradux.Domain.Mapping;

namespace Tradux.Api;

/// <summary>Template com versão ativa publicada, como a tela inicial do editor lista.</summary>
public sealed record TemplateSummary(string TemplateId, string Operation, int ActiveVersion, string ContentHash, DateTimeOffset PublishedAtUtc);

/// <summary>
/// Conteúdo completo de uma versão publicada, no mesmo formato do corpo de
/// cadastro (<see cref="ContentPublicationBody"/>): o editor abre a versão,
/// altera e cadastra a próxima.
/// </summary>
public sealed record TemplateVersionContent(
    string TemplateId,
    string Operation,
    int Version,
    string Status,
    string ContentHash,
    DateTimeOffset PublishedAtUtc,
    SchemaUpload Schema,
    SchemaUpload? SourceSchema,
    MappingTemplate Mapping,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LookupTables,
    IReadOnlyList<FunctionalScenario> Scenarios);

/// <summary>
/// Microsserviço tradutor de XML (ADR-0025). O integrador só conhece o NOME
/// do template: a organização vem da credencial (nunca da rota) e a operação é
/// <see cref="DefaultOperation"/>, salvo <c>?operation=</c>. As versões são do
/// próprio serviço (snapshots imutáveis no MongoDB): cada cadastro que passa
/// nos gates vira a próxima versão ativa; rollback reativa uma anterior.
/// Nenhuma regra de geração vive aqui — tudo delega a <c>Application</c>.
/// </summary>
public static partial class ServiceEndpoints
{
    public const string DefaultOperation = "default";
    public const string ConvertName = "ConvertXml";

    public static IEndpointRouteBuilder MapTraduxServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/templates").WithTags("Microsserviço").RequireCors(DesignEndpoints.CorsPolicy);

        group.MapGet("", ListAsync)
            .RequirePermission(Permissions.TemplateRead)
            .WithName("ListTemplates")
            .WithSummary("Templates da organização com a versão ativa de cada um.");

        group.MapPut("/{templateId}", RegisterAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithBodyLimit(BodyLimitKind.Design)
            .WithName("RegisterTemplate")
            .WithSummary("Cadastra a próxima versão do template: XSD de destino, XSD de origem, mapeamento e cenários.")
            .WithDescription(
                "Passa pelos gates de publicação (compilação, testes estruturais, cenários). 200: nova versão ativa em `snapshotVersion`. " +
                "422: gate reprovado — a versão ativa não muda. O primeiro cadastro cria o template.");

        // Sem .Accepts(): a restrição de tipo responderia 415 antes da autorização.
        group.MapPost("/{templateId}/convert", ConvertAsync)
            .RequirePermission(Permissions.TemplateUse)
            .WithBodyLimit(BodyLimitKind.Generation)
            .WithName(ConvertName)
            .WithSummary("Converte o XML de origem (corpo) no XML de destino do template.")
            .WithDescription(
                "200: o XML de destino (application/xml), com os cabeçalhos X-Template-Version e X-Correlation-Id. " +
                "422: XML de origem fora do XSD de origem ou regra que falhou — JSON com `diagnostics`. 404: template sem versão publicada. " +
                "Query: `version` (fixa), `versions` (fallback, ex.: 3,2), `correlationId`, `operation`, `format=json` (resultado completo sempre em JSON).");

        group.MapGet("/{templateId}/versions", ListVersionsAsync)
            .RequirePermission(Permissions.TemplateRead)
            .WithName("ListTemplateVersionHistory")
            .WithSummary("Histórico de versões do template, da mais recente para a mais antiga.");

        group.MapGet("/{templateId}/versions/{version:int}", GetVersionAsync)
            .RequirePermission(Permissions.TemplateRead)
            .WithName("GetTemplateVersion")
            .WithSummary("Conteúdo de uma versão publicada (XSDs, mapeamento, tabelas e cenários) — o editor abre por aqui.");

        group.MapPost("/{templateId}/rollback", RollbackAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithName("RollbackTemplate")
            .WithSummary("Reativa uma versão publicada (`version` informada ou a anterior à ativa).");

        return app;
    }

    private static async Task<Ok<IReadOnlyList<TemplateSummary>>> ListAsync(HttpContext http, ITemplateCatalog catalog, CancellationToken cancellationToken)
    {
        var entries = await catalog.ListAsync(http.Principal().OrganizationId, cancellationToken);
        IReadOnlyList<TemplateSummary> response = entries
            .Select(e => new TemplateSummary(e.TemplateId, e.Operation, e.ActiveVersion, e.ContentHash, e.PublishedAtUtc))
            .ToList();
        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<PublicationResult>, UnprocessableEntity<PublicationResult>, ProblemHttpResult>> RegisterAsync(
        string templateId,
        ContentPublicationBody body,
        ContentPublicationService service,
        HttpContext http,
        CancellationToken cancellationToken,
        string operation = DefaultOperation)
    {
        // O nome do template vai na URL de conversão dos integradores: só caracteres seguros.
        if (!TemplateName().IsMatch(templateId) || !TemplateName().IsMatch(operation))
        {
            return Bad("Nome do template (e da operação): letras, dígitos, '.', '-' ou '_', começando por letra ou dígito, até 100 caracteres.");
        }

        return await TemplateRequests.PublishContentAsync(
            new TemplateKey(http.Principal().OrganizationId, templateId, operation), body, service, http, cancellationToken);
    }

    private static async Task<IResult> ConvertAsync(
        string templateId,
        HttpContext http,
        XmlGenerationService generationService,
        ILoggerFactory loggers,
        CancellationToken cancellationToken,
        string operation = DefaultOperation,
        int? version = null,
        string? versions = null,
        string? correlationId = null,
        string? format = null)
    {
        if (!TryKey(http, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        var (result, invalid) = await TemplateRequests.TransformAsync(key, http.Request, generationService, loggers.GenerationLogger(), version, versions, correlationId, cancellationToken);
        if (result is null)
        {
            return invalid!;
        }

        if (TemplateRequests.IsMissingSnapshot(result))
        {
            return TypedResults.NotFound(result);
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Ok(result);
        }

        if (!result.Success || result.Xml is null)
        {
            return TypedResults.UnprocessableEntity(result);
        }

        http.Response.Headers["X-Template-Version"] = result.VersionUsed ?? string.Empty;
        http.Response.Headers["X-Correlation-Id"] = result.CorrelationId ?? string.Empty;
        return TypedResults.Text(result.Xml, "application/xml", Encoding.UTF8);
    }

    private static async Task<Results<Ok<IReadOnlyList<TemplateVersionInfo>>, ProblemHttpResult>> ListVersionsAsync(
        string templateId, HttpContext http, ITemplateSnapshotRepository repository, CancellationToken cancellationToken, string operation = DefaultOperation)
    {
        if (!TryKey(http, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await TemplateRequests.ListVersionsAsync(key, repository, cancellationToken));
    }

    private static async Task<Results<Ok<TemplateVersionContent>, NotFound, ProblemHttpResult>> GetVersionAsync(
        string templateId, int version, HttpContext http, ITemplateSnapshotRepository repository, CancellationToken cancellationToken, string operation = DefaultOperation)
    {
        if (!TryKey(http, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        if (await repository.GetVersionAsync(key, version, cancellationToken) is not { } snapshot)
        {
            return TypedResults.NotFound();
        }

        var content = snapshot.Content;
        return TypedResults.Ok(new TemplateVersionContent(
            key.TemplateId,
            key.Operation,
            snapshot.Version,
            snapshot.Status.ToString(),
            snapshot.ContentHash,
            snapshot.PublishedAtUtc,
            SchemaUploads.Encode(content.SchemaFiles, content.SchemaEntryPoint, content.RootElementName),
            content.SourceSchema is { } source ? SchemaUploads.Encode(source.Files, source.EntryPoint, source.RootElementName) : null,
            content.Mapping,
            content.LookupTables,
            snapshot.Scenarios));
    }

    private static async Task<Results<Ok<RollbackResult>, Conflict<RollbackResult>, ProblemHttpResult>> RollbackAsync(
        string templateId, RollbackBody? body, HttpContext http, LayeredTemplateService service,
        CancellationToken cancellationToken, string operation = DefaultOperation)
    {
        if (!TryKey(http, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        return await TemplateRequests.RollbackAsync(key, body, service, http, cancellationToken);
    }

    private static bool TryKey(HttpContext http, string templateId, string operation, out TemplateKey key, out ProblemHttpResult problem) =>
        TemplateRequests.TryKey(http.Principal().OrganizationId, templateId, operation, out key, out problem);

    private static ProblemHttpResult Bad(string message) => TemplateRequests.Bad(message);

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial System.Text.RegularExpressions.Regex TemplateName();

}
