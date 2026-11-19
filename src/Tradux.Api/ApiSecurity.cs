using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Tradux.Application.Security;

namespace Tradux.Api;

/// <summary>
/// Autenticação por chave de API (ADR-0012 §1): <c>Authorization: ApiKey &lt;segredo&gt;</c>
/// ou <c>X-Api-Key</c>. O principal leva a organização e as permissões
/// efetivas como claims; nada do segredo vai para log.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CredentialService credentials) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string OrganizationClaim = "tradux:org";
    public const string PermissionClaim = "tradux:perm";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var secret = ReadSecret(Request);
        if (secret is null)
        {
            return AuthenticateResult.NoResult();
        }

        var principal = await credentials.AuthenticateAsync(secret, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.Fail("Credencial inválida ou revogada.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, principal.Actor),
            new(OrganizationClaim, principal.OrganizationId),
        };
        claims.AddRange(principal.Permissions.Select(p => new Claim(PermissionClaim, p)));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        return base.HandleChallengeAsync(properties);
    }

    private static string? ReadSecret(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(SchemeName + " ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization[(SchemeName.Length + 1)..].Trim();
        }

        var header = request.Headers["X-Api-Key"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
    }
}

/// <summary>Políticas por permissão e isolamento por organização (ADR-0012 §2–§4).</summary>
public static class ApiSecurity
{
    public const string PolicyPrefix = "perm:";

    public static string Policy(string permission) => PolicyPrefix + permission;

    public static IServiceCollection AddTraduxSecurity(this IServiceCollection services)
    {
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        var authorization = services.AddAuthorizationBuilder()
            // Rota nova sem política explícita não fica pública por engano.
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        foreach (var permission in Permissions.All)
        {
            authorization.AddPolicy(Policy(permission), policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(ApiKeyAuthenticationHandler.PermissionClaim, permission));
        }

        return services;
    }

    /// <summary>
    /// Exige a permissão. A organização não é checada aqui: rotas com
    /// <c>{organizationId}</c> usam <see cref="RequireSameOrganization"/>; as demais
    /// tiram a organização da credencial.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(Policy(permission));

    /// <summary>
    /// Filtro de organização: principal de outra organização recebe 404 — a
    /// resposta não revela se o recurso existe (ADR-0012 §3).
    /// </summary>
    public static RouteGroupBuilder RequireSameOrganization(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            var routeOrganization = context.HttpContext.GetRouteValue("organizationId") as string;
            var principal = context.HttpContext.User.FindFirst(ApiKeyAuthenticationHandler.OrganizationClaim)?.Value;
            if (routeOrganization is null || principal is null || !string.Equals(routeOrganization, principal, StringComparison.Ordinal))
            {
                return TypedResults.NotFound();
            }

            return await next(context);
        });
        return group;
    }

    /// <summary>O principal autenticado — fonte única do "ator" das operações de escrita.</summary>
    public static AccessPrincipal Principal(this HttpContext context)
    {
        var user = context.User;
        return new AccessPrincipal(
            user.FindFirst(ApiKeyAuthenticationHandler.OrganizationClaim)?.Value ?? throw new InvalidOperationException("Requisição sem organização autenticada."),
            user.Identity?.Name ?? "desconhecido",
            user.FindAll(ApiKeyAuthenticationHandler.PermissionClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal));
    }
}
