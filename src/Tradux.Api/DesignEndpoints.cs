using Microsoft.AspNetCore.Http.HttpResults;
using Tradux.Application.Security;
using Tradux.Application.Design;
using Tradux.Contracts.Design;

namespace Tradux.Api;

/// <summary>
/// Endpoints de desenho usados pelo editor visual, via <see cref="DraftDesignService"/>.
/// Sem estado: o rascunho não é gravado (só o evento de auditoria do teste).
///
/// Autorização por ação (Fase 9, ADR-0012): operadores/schema exigem
/// <c>template.edit</c>; teste e qualidade, <c>template.test</c>.
/// </summary>
public static class DesignEndpoints
{
    public const string CorsPolicy = "editor";

    public static IEndpointRouteBuilder MapTraduxDesignEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/design").WithTags("Design").RequireCors(CorsPolicy).WithBodyLimit(BodyLimitKind.Design);

        group.MapGet("/operators", (DraftDesignService service) => TypedResults.Ok(service.ListOperators()))
            .RequirePermission(Permissions.TemplateEdit)
            .WithName("ListOperators")
            .WithSummary("Catálogo tipado de operadores (seção 13) com metadados para o editor.");

        group.MapPost("/schema", DescribeSchema)
            .RequirePermission(Permissions.TemplateEdit)
            .WithName("DescribeDestinationSchema")
            .WithSummary("Importa XSD(s) em memória e devolve a árvore de destino (nada é gravado).");

        group.MapPost("/source-schema", DescribeSourceSchema)
            .RequirePermission(Permissions.TemplateEdit)
            .WithName("DescribeSourceSchema")
            .WithSummary("Importa o XSD de ORIGEM em memória e devolve a árvore de campos que o XML de entrada terá (ADR-0015); nada é gravado.");

        group.MapPost("/test", TestDraft)
            .RequirePermission(Permissions.TemplateTest)
            .WithName("TestDraft")
            .WithSummary("Compila e executa um rascunho de template com a entrada de teste (XML de origem ou JSON); compara com XML esperado, se enviado.")
            .WithDescription(
                "200: teste executado — `publishable` resume; `compileDiagnostics` lista obrigatórios sem mapeamento e " +
                "erros de compilação; `generation` traz o XML/diagnósticos da execução. 400: schema, mapeamento ou dados malformados.");

        group.MapPost("/quality", CheckDraftQuality)
            .RequirePermission(Permissions.TemplateTest)
            .WithName("CheckDraftQuality")
            .WithSummary("Testes estruturais automáticos do rascunho (dados sintéticos gerados do XSD + mapeamento); nada é gravado.")
            .WithDescription(
                "200: suíte executada — `passed` resume; `report.cases` traz nome, categoria, expectativa e, se reprovado, código e XPath " +
                "(nunca os dados gerados); `report.notCovered` lista o que não pôde ser exercitado. 400: schema ou mapeamento malformados.");

        return app;
    }

    private static Results<Ok<DestinationTree>, ProblemHttpResult> DescribeSchema(SchemaUpload upload, DraftDesignService service)
    {
        try
        {
            return TypedResults.Ok(service.DescribeSchema(upload));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "XSD inválido");
        }
    }

    private static Results<Ok<SourceFieldTree>, ProblemHttpResult> DescribeSourceSchema(SchemaUpload upload, DraftDesignService service)
    {
        try
        {
            return TypedResults.Ok(service.DescribeSource(upload));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "XSD de origem inválido");
        }
    }

    private static async Task<Results<Ok<DraftQualityResult>, ProblemHttpResult>> CheckDraftQuality(
        DraftQualityRequest request, DraftDesignService service, HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await service.QualityAsync(request, http.Principal(), cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Rascunho inválido");
        }
    }

    private static async Task<Results<Ok<DraftTestResult>, ProblemHttpResult>> TestDraft(
        DraftTestRequest request, DraftDesignService service, HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await service.TestAsync(request, http.Principal(), cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Rascunho inválido");
        }
    }
}
