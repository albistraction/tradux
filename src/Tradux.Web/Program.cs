using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Único dado dinâmico do editor: onde fica a API (configuração
// "Editor:ApiBaseUrl"). Todo o resto é arquivo estático.
var apiBaseUrl = app.Configuration["Editor:ApiBaseUrl"] ?? "http://localhost:5220";
var configScript = $"window.TRADUX_EDITOR_CONFIG = {JsonSerializer.Serialize(new { apiBaseUrl })};";
app.MapGet("/config.js", () => Results.Text(configScript, "text/javascript"));

app.Use(async (context, next) =>
{
    // O editor não executa código do usuário nem carrega recursos externos.
    context.Response.Headers["Content-Security-Policy"] =
        $"default-src 'self'; connect-src 'self' {apiBaseUrl}; style-src 'self'; script-src 'self'; img-src 'self' data:; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Editor sempre atualizado: o navegador revalida cada arquivo (ETag → 304 quando nada mudou).
    // Sem isso, módulos JS antigos ficavam em cache e geravam templates com regras velhas.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

app.Run();
