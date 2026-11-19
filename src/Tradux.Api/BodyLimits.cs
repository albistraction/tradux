using Microsoft.AspNetCore.Http.Features;

namespace Tradux.Api;

/// <summary>Tipo de limite de corpo que a rota declara; o valor vem da configuração.</summary>
public enum BodyLimitKind
{
    /// <summary>Rotas que recebem XSD em Base64 (<see cref="DesignOptions.MaxRequestBytes"/>).</summary>
    Design,

    /// <summary>Geração/conversão (<see cref="GenerationOptions.MaxRequestBytes"/>).</summary>
    Generation,

    /// <summary>Pacote portátil (ZIP).</summary>
    Package,
}

/// <summary>Metadado de endpoint com o tipo de limite de corpo.</summary>
public sealed record BodyLimitMetadata(BodyLimitKind Kind);

/// <summary>
/// Limite de corpo por endpoint (ADR-0014 §3): a rota declara o tipo
/// (<see cref="WithBodyLimit"/>) e um só middleware aplica — pelo endpoint
/// roteado, não pelo texto do caminho (o roteamento ignora maiúsculas e barra
/// final; revisão da Fase 11, B1).
/// </summary>
public static class BodyLimits
{
    /// <summary>Pacote: XSDs + camadas + cenários, compactados.</summary>
    public const long PackageMaxBytes = 40L * 1024 * 1024;

    public static TBuilder WithBodyLimit<TBuilder>(this TBuilder builder, BodyLimitKind kind)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new BodyLimitMetadata(kind));

    public static IApplicationBuilder UseBodyLimits(this IApplicationBuilder app, DesignOptions design, GenerationOptions generation) =>
        app.Use(async (context, next) =>
        {
            var kind = context.GetEndpoint()?.Metadata.GetMetadata<BodyLimitMetadata>()?.Kind;
            if (kind is null)
            {
                await next(context);
                return;
            }

            var (maxBytes, what) = kind switch
            {
                BodyLimitKind.Design => (design.MaxRequestBytes, "rotas que recebem XSD"),
                BodyLimitKind.Generation => (generation.MaxRequestBytes, "a geração de XML"),
                _ => (PackageMaxBytes, "o pacote"),
            };

            if (context.Request.ContentLength > maxBytes)
            {
                await Results.Problem(
                    $"Corpo acima do limite de {maxBytes} bytes para {what}.",
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Pedido grande demais").ExecuteAsync(context);
                return;
            }

            // Sem Content-Length (chunked): o servidor corta a leitura no limite.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = maxBytes;
            }

            await next(context);
        });
}
