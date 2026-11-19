using Microsoft.AspNetCore.Http.HttpResults;
using Tradux.Application;
using Tradux.Application.Design;
using Tradux.Application.Ports;
using Tradux.Application.Security;
using Tradux.Contracts.Design;

namespace Tradux.Api;

/// <summary>
/// Mudanças entre versões do XSD de destino (ADR-0017): o que mudou
/// (elemento novo, removido, movido, tipo, cardinalidade, restrições, ordem) e
/// o que o mapeamento precisa (remapear, mapear, revisar). Só leitura: nada é
/// gravado.
/// </summary>
public static class SchemaChangeEndpoints
{
    public static IEndpointRouteBuilder MapTraduxSchemaChangeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/design/schema-changes", CompareDraft)
            .WithTags("Design")
            .WithBodyLimit(BodyLimitKind.Design)
            .RequireCors(DesignEndpoints.CorsPolicy)
            .RequirePermission(Permissions.TemplateEdit)
            .WithName("CompareDestinationSchemas")
            .WithSummary("XSD de destino atual × XSD novo (+ mapeamento opcional): mudanças e impacto no mapeamento; nada é gravado.");

        app.MapGroup("/v1/organizations/{organizationId}/templates/{templateId}/operations/{operation}")
            .RequireSameOrganization()
            .MapPost("/schema-changes", CompareTemplate)
            .WithTags("Templates")
            .WithBodyLimit(BodyLimitKind.Design)
            .RequirePermission(Permissions.TemplateRead)
            .WithName("CompareTemplateSchema")
            .WithSummary("XSD da versão ativa do template × XSD novo: mudanças e impacto no mapeamento publicado; nada é gravado.")
            .WithDescription("404: template sem versão ativa (ou de outra organização). 400: XSD novo inválido.");

        return app;
    }

    private static Results<Ok<SchemaChangeReport>, ProblemHttpResult> CompareDraft(SchemaChangeRequest request, DraftDesignService service)
    {
        try
        {
            return TypedResults.Ok(service.CompareSchemas(request));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "XSD inválido");
        }
    }

    private static async Task<Results<Ok<SchemaChangeReport>, NotFound, ProblemHttpResult>> CompareTemplate(
        string organizationId, string templateId, string operation, TemplateSchemaChangeRequest request,
        ITemplateSnapshotRepository snapshots, DraftDesignService service, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        var active = await snapshots.GetActiveAsync(key, cancellationToken);
        if (active is null)
        {
            return TypedResults.NotFound();
        }

        try
        {
            return TypedResults.Ok(service.CompareWithContent(active.Content, request?.After));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "XSD inválido");
        }
    }
}
