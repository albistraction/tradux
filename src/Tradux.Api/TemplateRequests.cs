using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Tradux.Application;
using Tradux.Application.Generation;
using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Publishing;
using Tradux.Application.Security;
using Tradux.Application.Snapshots;
using Tradux.Contracts;

namespace Tradux.Api;

/// <summary>
/// O que as rotas do microsserviço (<c>/v1/templates/{nome}</c>, organização da
/// credencial) e as rotas detalhadas (<c>/v1/organizations/{org}/templates/{t}/operations/{op}</c>)
/// fazem igual: ler a chave, converter, listar versões, cadastrar e reativar.
/// As duas formas de rota só diferem em de onde vem a chave.
/// </summary>
internal static class TemplateRequests
{
    public static ProblemHttpResult Bad(string message) =>
        TypedResults.Problem(message, statusCode: StatusCodes.Status400BadRequest, title: "Pedido inválido");

    public static bool TryKey(string organizationId, string templateId, string operation, out TemplateKey key, out ProblemHttpResult problem)
    {
        try
        {
            key = new TemplateKey(organizationId, templateId, operation);
            problem = null!;
            return true;
        }
        catch (ArgumentException ex)
        {
            key = null!;
            problem = Bad(ex.Message);
            return false;
        }
    }

    /// <summary>Template sem a versão pedida (ou sem versão ativa): 404 nas duas formas de rota.</summary>
    public static bool IsMissingSnapshot(GenerationResult result) =>
        result.Diagnostics.Any(d => d.Code == XmlGenerationService.SnapshotNotFoundCode);

    /// <summary>Corpo = XML de origem; resultado da conversão, ou o 400 do pedido malformado.</summary>
    public static async Task<(GenerationResult? Result, ProblemHttpResult? Problem)> TransformAsync(
        TemplateKey key, HttpRequest request, XmlGenerationService generation, ILogger logger,
        int? version, string? versions, string? correlationId, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = ParseVersions(versions);
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length == 0)
            {
                return (null, Bad("Envie o XML de origem no corpo."));
            }

            var result = await generation.TransformAsync(key, buffer.ToArray(), version, candidates, correlationId, cancellationToken);
            logger.LogGeneration(key, result, fallback: candidates is not null);
            return (result, null);
        }
        catch (ArgumentException ex)
        {
            return (null, Bad(ex.Message));
        }
    }

    public static async Task<IReadOnlyList<TemplateVersionInfo>> ListVersionsAsync(
        TemplateKey key, ITemplateSnapshotRepository repository, CancellationToken cancellationToken) =>
        (await repository.ListVersionsAsync(key, cancellationToken))
            .Select(v => new TemplateVersionInfo(v.Version, v.ContentHash, v.Status.ToString(), v.PublishedAtUtc))
            .ToList();

    /// <summary>Recusa pelos gates é 422 com o resultado completo (diagnósticos + relatório).</summary>
    public static Results<Ok<PublicationResult>, UnprocessableEntity<PublicationResult>, ProblemHttpResult> Publication(PublicationResult result) =>
        result.Success ? TypedResults.Ok(result) : TypedResults.UnprocessableEntity(result);

    public static async Task<Results<Ok<PublicationResult>, UnprocessableEntity<PublicationResult>, ProblemHttpResult>> PublishContentAsync(
        TemplateKey key, ContentPublicationBody body, ContentPublicationService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (body.Schema is null || body.Mapping is null)
        {
            return Bad("Informe 'schema' e 'mapping'.");
        }

        SnapshotContent content;
        try
        {
            content = SnapshotContent.FromUpload(body.Schema, body.Mapping, body.LookupTables, body.SourceSchema);
        }
        catch (ArgumentException ex)
        {
            return Bad(ex.Message);
        }

        return Publication(await service.PublishAsync(key, content, body.Scenarios ?? [], http.Principal().Actor, cancellationToken));
    }

    public static async Task<Results<Ok<RollbackResult>, Conflict<RollbackResult>, ProblemHttpResult>> RollbackAsync(
        TemplateKey key, RollbackBody? body, LayeredTemplateService service, HttpContext http, CancellationToken cancellationToken)
    {
        var result = await service.RollbackAsync(key, body?.Version, http.Principal().Actor, cancellationToken);
        return result.Success ? TypedResults.Ok(result) : TypedResults.Conflict(result);
    }

    /// <exception cref="ArgumentException">Lista malformada.</exception>
    private static IReadOnlyList<int>? ParseVersions(string? versions)
    {
        if (string.IsNullOrWhiteSpace(versions))
        {
            return null;
        }

        return versions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
                ? value
                : throw new ArgumentException("versions: lista de números de versão separados por vírgula (ex.: 3,2).", nameof(versions)))
            .ToList();
    }
}
