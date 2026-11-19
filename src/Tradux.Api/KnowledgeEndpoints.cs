using Microsoft.AspNetCore.Http.HttpResults;
using Tradux.Application.Security;
using Tradux.Knowledge;

namespace Tradux.Api;

/// <summary>Busca na documentação aprovada (RAG, ADR-0013): o serviço mora em <c>Tradux.Knowledge</c>.</summary>
public static class KnowledgeEndpoints
{
    public const int MaxLimit = 20;

    public static IEndpointRouteBuilder MapTraduxKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/knowledge/search", Search)
            .RequirePermission(Permissions.TemplateRead)
            .WithTags("Conhecimento")
            .WithName("SearchKnowledge")
            .WithSummary("Busca na documentação aprovada (RAG): trechos com fonte, versão e âncora. Apoio à engenharia — nunca regra executável.")
            .WithDescription("Escopo = organização da credencial (fontes globais + da própria organização); fontes restritas exigem audit.read. 503 se o índice não está configurado.");
        return app;
    }

    private static Results<Ok<IReadOnlyList<SearchHit>>, ProblemHttpResult> Search(
        KnowledgeService knowledge, HttpContext http, string? q = null, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length > 500)
        {
            return TypedResults.Problem("Informe 'q' (até 500 caracteres).", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!knowledge.Available)
        {
            return TypedResults.Problem("Busca de conhecimento indisponível: configure Tradux:Knowledge:CorpusRoot ou IndexPath.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var principal = http.Principal();
        var scope = new AccessScope(principal.OrganizationId, principal.Can(Permissions.AuditRead) ? AccessLevel.Restricted : AccessLevel.Internal);
        return TypedResults.Ok(knowledge.Search(q, scope, Math.Clamp(limit, 1, MaxLimit)));
    }
}
