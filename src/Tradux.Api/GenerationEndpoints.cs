using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Tradux.Application;
using Tradux.Application.Generation;
using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Security;
using Tradux.Contracts;

namespace Tradux.Api;

/// <summary>Corpo de <c>POST .../xml</c>: organização, template e operação vêm da rota.</summary>
/// <param name="Data">Dados de entrada em JSON (objeto; nomes sensíveis a maiúsculas/minúsculas).</param>
/// <param name="Version">Versão de snapshot fixada; omitida = versão ativa.</param>
/// <param name="CorrelationId">Ecoado no resultado; gerado quando ausente.</param>
/// <param name="Versions">Fallback: versões candidatas em ordem; a primeira válida é usada (seção 17).</param>
public sealed record GenerateXmlBody(JsonElement Data, int? Version = null, string? CorrelationId = null, IReadOnlyList<int>? Versions = null);

/// <summary>
/// Endpoints HTTP v1 (seção 26). Só convertem transporte ⇄ contrato e
/// delegam aos serviços de <c>Application</c> — nenhuma regra de geração vive
/// aqui (docs/api/README.md §0).
///
/// Autorização por ação (Fase 9, ADR-0012): gerar exige <c>template.use</c>,
/// ler versões/explicação/atualizações exige <c>template.read</c>, sempre na
/// mesma organização da credencial.
/// </summary>
public static class GenerationEndpoints
{
    public const string GenerateXmlName = "GenerateXml";
    public const string TransformXmlName = "TransformXml";

    public static IEndpointRouteBuilder MapTraduxGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/organizations/{organizationId}/templates/{templateId}/operations/{operation}")
            .WithTags("Templates")
            .RequireSameOrganization();

        group.MapPost("/xml", GenerateXmlAsync)
            .RequirePermission(Permissions.TemplateUse)
            .WithBodyLimit(BodyLimitKind.Generation)
            .WithName(GenerateXmlName)
            .WithSummary("Gera e valida XML a partir de um snapshot publicado (seção 26).")
            .WithDescription(
                "200: geração executada — consulte `success` e `diagnostics` (falha de regra ou de XSD também é 200). " +
                "404: não há snapshot publicado para a chave/versão. 400: pedido malformado (ex.: `data` não é objeto JSON).");

        // Sem .Accepts(): a restrição de tipo responderia 415 antes da autorização.
        group.MapPost("/transform", TransformXmlAsync)
            .RequirePermission(Permissions.TemplateUse)
            .WithBodyLimit(BodyLimitKind.Generation)
            .WithName(TransformXmlName)
            .WithSummary("Gera o XML de destino a partir de um XML de origem (no formato do XSD de origem do template) — ADR-0015.")
            .WithDescription(
                "Corpo: o XML de origem (application/xml; a codificação declarada no XML é respeitada). Query: `version` (fixa), " +
                "`versions` (fallback, ex.: 3,2), `correlationId`. 200: mesmo resultado de `POST …/xml` — XML fora do XSD de origem " +
                "vem como `SOURCE_XML_INVALID` (linha/coluna); template sem XSD de origem, `TEMPLATE_WITHOUT_SOURCE_SCHEMA`. " +
                "404: sem snapshot publicado. 400: pedido inválido ou acima dos limites; 413: corpo acima do limite da geração (`Tradux:Generation:MaxRequestBytes`).");

        group.MapGet("/versions/{version:int}/explanation", ExplainAsync)
            .RequirePermission(Permissions.TemplateRead)
            .WithName("ExplainTemplateVersion")
            .WithSummary("Explica a resolução de camadas de uma versão publicada: camadas usadas e a história de cada regra (seção 16).");

        group.MapGet("/updates", ListUpdatesAsync)
            .RequirePermission(Permissions.TemplateRead)
            .WithName("ListTemplateUpdates")
            .WithSummary("Atualizações propostas, aplicadas ou reprovadas deste template (mais recentes primeiro).");

        group.MapGet("/versions", ListVersionsAsync)
            .RequirePermission(Permissions.TemplateRead)
            .WithName("ListTemplateVersions")
            .WithSummary("Lista as versões de snapshot publicadas, da mais recente para a mais antiga.");

        return app;
    }

    private static async Task<Results<Ok<GenerationResult>, NotFound<GenerationResult>, ProblemHttpResult>> GenerateXmlAsync(
        string organizationId,
        string templateId,
        string operation,
        GenerateXmlBody body,
        XmlGenerationService generationService,
        ILoggerFactory loggers,
        Tradux.Application.Diagnostics.DiagnosticCaptureService capture,
        CancellationToken cancellationToken)
    {
        var request = new GenerateXmlRequest(organizationId, templateId, operation, body.Data)
        {
            Version = body.Version,
            CorrelationId = body.CorrelationId,
            CandidateVersions = body.Versions,
        };

        GenerationResult result;
        try
        {
            result = await generationService.GenerateAsync(request, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Pedido de geração inválido");
        }

        var key = new TemplateKey(organizationId, templateId, operation);
        var logger = loggers.GenerationLogger();
        logger.LogGeneration(key, result, fallback: body.Versions is { Count: > 0 });

        // Captura diagnóstica só se ligada (decisão em memória); falha nela nunca
        // derruba a geração — e o log da falha leva só o tipo do erro.
        if (capture.ShouldCapture(key))
        {
            try
            {
                await capture.CaptureAsync(key, body.Data, result, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Captura diagnóstica falhou para {Organization}/{Template}/{Operation} ({Error}).", key.OrganizationId, key.TemplateId, key.Operation, ex.GetType().Name);
            }
        }

        return TemplateRequests.IsMissingSnapshot(result) ? TypedResults.NotFound(result) : TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<GenerationResult>, NotFound<GenerationResult>, ProblemHttpResult>> TransformXmlAsync(
        string organizationId,
        string templateId,
        string operation,
        HttpRequest request,
        XmlGenerationService generationService,
        ILoggerFactory loggers,
        CancellationToken cancellationToken,
        int? version = null,
        string? versions = null,
        string? correlationId = null)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        var (result, invalid) = await TemplateRequests.TransformAsync(key, request, generationService, loggers.GenerationLogger(), version, versions, correlationId, cancellationToken);
        if (result is null)
        {
            return invalid!;
        }

        return TemplateRequests.IsMissingSnapshot(result) ? TypedResults.NotFound(result) : TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<ResolutionExplanation>, NotFound, ProblemHttpResult>> ExplainAsync(
        string organizationId, string templateId, string operation, int version, LayeredTemplateService service, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        var explanation = await service.ExplainAsync(key, version, cancellationToken);
        return explanation is null ? TypedResults.NotFound() : TypedResults.Ok(explanation);
    }

    private static async Task<Results<Ok<IReadOnlyList<TemplateUpdate>>, ProblemHttpResult>> ListUpdatesAsync(
        string organizationId, string templateId, string operation, LayeredTemplateService service, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await service.ListUpdatesAsync(key, cancellationToken));
    }

    private static async Task<Results<Ok<IReadOnlyList<TemplateVersionInfo>>, ProblemHttpResult>> ListVersionsAsync(
        string organizationId,
        string templateId,
        string operation,
        ITemplateSnapshotRepository repository,
        CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await TemplateRequests.ListVersionsAsync(key, repository, cancellationToken));
    }
}
