using Tradux.Api;
using Tradux.Xsd.Security;

// Timeout global de regex (ADR-0012 §9): o validador XSD do .NET usa Regex
// sem timeout próprio; um pattern patológico não pode prender threads da API
// compartilhada. Precisa vir antes de qualquer Regex ser criada no processo.
RegexTimeout.Configure();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddTradux(builder.Configuration);
builder.Services.AddTraduxSecurity();
builder.Services.AddTraduxObservability(builder.Configuration);
builder.Services.AddTraduxHealth();

// A política de CORS vale só para o que o editor usa (/v1/design/* e /v1/templates);
// a credencial vai no cabeçalho (nunca cookie — sem CSRF).
var editorOrigins = builder.Configuration.Section<EditorCorsOptions>(EditorCorsOptions.SectionName).AllowedOrigins;
builder.Services.AddCors(options => options.AddPolicy(
    DesignEndpoints.CorsPolicy,
    policy => policy.WithOrigins(editorOrigins).WithMethods("GET", "POST", "PUT").WithHeaders("Content-Type", "Authorization", "X-Api-Key")));

var app = builder.Build();

// Corpo malformado (JSON inválido, tipo errado, aninhamento excessivo) é 400
// em qualquer ambiente — em Development o ASP.NET Core lança
// BadHttpRequestException, que sem isto viraria 500 (ADR-0008 §6).
// MongoDB indisponível (chave fria) é 503 com Retry-After, não 500 (ADR-0014 §4).
app.UseRetryAfterOnUnavailable();
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex switch
    {
        BadHttpRequestException badRequest => badRequest.StatusCode,
        _ when HealthAndResilience.IsDependencyUnavailable(ex) => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    },
});

app.UseBodyLimits(app.Services.GetRequiredService<DesignOptions>(), app.Services.GetRequiredService<GenerationOptions>());

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapOpenApi().AllowAnonymous();
app.MapTraduxGenerationEndpoints();
app.MapTraduxServiceEndpoints();
app.MapTraduxDesignEndpoints();
app.MapTraduxAdministrationEndpoints();
app.MapTraduxKnowledgeEndpoints();
app.MapTraduxSchemaChangeEndpoints();
app.MapTraduxHealth();

app.Run();

/// <summary>Exposto para <c>WebApplicationFactory&lt;Program&gt;</c> nos testes de integração.</summary>
public partial class Program;
