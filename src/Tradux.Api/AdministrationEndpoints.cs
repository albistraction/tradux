using Microsoft.AspNetCore.Http.HttpResults;
using Tradux.Application;
using Tradux.Application.Diagnostics;
using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Testing;
using Tradux.Application.Publishing;
using Tradux.Application.Security;
using Tradux.Application.Snapshots;
using Tradux.Contracts.Design;
using Tradux.Domain.Layers;
using Tradux.Domain.Mapping;

namespace Tradux.Api;

/// <summary>Nova versão de camada. A organização vem da rota; o autor, da credencial.</summary>
public sealed record NewLayerBody(
    string LayerId,
    LayerKind Kind,
    LayerRef? Parent = null,
    bool Shared = false,
    SchemaUpload? Schema = null,
    IReadOnlyList<LayerOverride>? Overrides = null,
    IReadOnlyList<LookupOverride>? LookupOverrides = null,
    string? Comment = null,
    SchemaUpload? SourceSchema = null);

/// <summary>Camada criada (sem o XSD de volta) e as atualizações que ela disparou nesta organização.</summary>
public sealed record LayerCreated(LayerRef Ref, string ContentHash, IReadOnlyList<TemplateUpdate> Updates);

public sealed record BindBody(LayerRef Top, UpdatePolicy Policy, IReadOnlyList<FunctionalScenario> Scenarios);

public sealed record PublishTopBody(LayerRef Top);

public sealed record ConfigureBindingBody(UpdatePolicy? Policy = null, IReadOnlyList<FunctionalScenario>? Scenarios = null);

public sealed record RollbackBody(int? Version = null);

/// <summary>Publicação de conteúdo completo (editor visual): passa pelos mesmos gates de qualquer publicação.</summary>
public sealed record ContentPublicationBody(
    SchemaUpload Schema,
    MappingTemplate Mapping,
    IReadOnlyList<FunctionalScenario> Scenarios,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? LookupTables = null,
    SchemaUpload? SourceSchema = null);

/// <summary>
/// Escrita por HTTP (ADR-0012 §6): camadas, vínculo, publicação, rollback,
/// atualização e leitura da auditoria. Toda rota exige permissão
/// específica e a mesma organização da credencial; o ator é sempre o
/// principal autenticado.
/// </summary>
public static class AdministrationEndpoints
{
    public static IEndpointRouteBuilder MapTraduxAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var organization = app.MapGroup("/v1/organizations/{organizationId}").WithTags("Administração").RequireSameOrganization();

        organization.MapPost("/layers", CreateLayerAsync)
            .RequirePermission(Permissions.TemplateEdit)
            .WithBodyLimit(BodyLimitKind.Design)
            .WithName("CreateLayerVersion")
            .WithSummary("Grava nova versão imutável de camada (base/produto/cliente) e propaga para os vínculos afetados. Camada nova exige também template.create; camada compartilhada, template.publish.");

        organization.MapPost("/packages", ImportPackageAsync)
            .RequirePermission(Permissions.TemplateImport)
            .WithBodyLimit(BodyLimitKind.Package)
            .WithName("ImportPackage")
            .WithSummary("Importa pacote .traduxpkg (corpo = ZIP) para ?templateId=&operation= desta organização — validado e publicado pelos gates; informa se o hash foi preservado.");

        organization.MapGet("/audit", ListAuditAsync)
            .RequirePermission(Permissions.AuditRead)
            .WithName("ListAuditEvents")
            .WithSummary("Eventos de auditoria da organização (mais recentes primeiro; sem payload fiscal).");

        var template = app.MapGroup("/v1/organizations/{organizationId}/templates/{templateId}/operations/{operation}")
            .WithTags("Administração")
            .RequireSameOrganization();

        template.MapGet("/package", ExportPackageAsync)
            .RequirePermission(Permissions.TemplateExport)
            .WithName("ExportPackage")
            .WithSummary("Exporta o pacote portátil (.traduxpkg) da versão ativa (ou ?version=). Nunca contém segredos, auditoria ou captura diagnóstica.");

        template.MapPut("/binding", BindAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithName("BindAndPublish")
            .WithSummary("Vincula o template a uma cadeia de camadas e publica (só se passar em todos os gates).");

        template.MapPatch("/binding", ConfigureBindingAsync)
            .RequirePermission(Permissions.TemplateEdit)
            .WithName("ConfigureBinding")
            .WithSummary("Troca política de atualização e/ou cenários funcionais do vínculo.");

        template.MapPost("/binding/publication", PublishTopAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithName("PublishLayers")
            .WithSummary("Publica nova cadeia (ex.: nova versão da camada cliente) para um template já vinculado.");

        template.MapPost("/publications", PublishContentAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithBodyLimit(BodyLimitKind.Design)
            .WithName("PublishContent")
            .WithSummary("Publica conteúdo completo (editor visual) — só se compilação, testes estruturais e cenários funcionais passarem.");

        template.MapPost("/rollback", RollbackAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithName("Rollback")
            .WithSummary("Reativa uma versão publicada (a informada ou a anterior à ativa).");

        template.MapPost("/updates/{updateId}/approve", ApproveAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithName("ApproveUpdate")
            .WithSummary("Aplica uma proposta manual — só sobre a versão ativa em que foi feita.");

        template.MapPost("/diagnostics/capture", EnableCaptureAsync)
            .RequirePermission(Permissions.DiagnosticCapture)
            .WithName("EnableDiagnosticCapture")
            .WithSummary("Liga a captura diagnóstica temporária (entrada e XML mascarados, criptografados, com expiração automática).");

        template.MapDelete("/diagnostics/capture", DisableCaptureAsync)
            .RequirePermission(Permissions.DiagnosticCapture)
            .WithName("DisableDiagnosticCapture")
            .WithSummary("Desliga a captura diagnóstica.");

        template.MapGet("/diagnostics/records", ReadCaptureAsync)
            .RequirePermission(Permissions.DiagnosticCapture)
            .WithName("ReadDiagnosticRecords")
            .WithSummary("Registros capturados (mascarados). Toda leitura é auditada.");

        template.MapDelete("/diagnostics/records", DeleteCaptureAsync)
            .RequirePermission(Permissions.DiagnosticCapture)
            .WithName("DeleteDiagnosticRecords")
            .WithSummary("Apaga os registros capturados antes da expiração.");

        template.MapPost("/updates/{updateId}/reject", RejectAsync)
            .RequirePermission(Permissions.TemplatePublish)
            .WithName("RejectUpdate")
            .WithSummary("Descarta uma proposta manual.");

        return app;
    }

    private static async Task<Results<Ok<LayerCreated>, ProblemHttpResult, ForbidHttpResult>> CreateLayerAsync(
        string organizationId, NewLayerBody body, LayeredTemplateService service, ITemplateLayerRepository layers, HttpContext http, CancellationToken cancellationToken)
    {
        var principal = http.Principal();
        if (string.IsNullOrWhiteSpace(body.LayerId))
        {
            return Bad("Informe 'layerId'.");
        }

        if (await layers.GetLatestAsync(organizationId, body.LayerId, cancellationToken) is null && !principal.Can(Permissions.TemplateCreate))
        {
            return TypedResults.Forbid();
        }

        // Compartilhar expõe a camada a todas as organizações: é publicação (revisão W5).
        if (body.Shared && !principal.Can(Permissions.TemplatePublish))
        {
            return TypedResults.Forbid();
        }

        try
        {
            var result = await service.CreateLayerVersionAsync(new NewLayerVersion
            {
                OrganizationId = organizationId,
                LayerId = body.LayerId,
                Kind = body.Kind,
                Parent = body.Parent,
                Shared = body.Shared,
                Schema = body.Schema is { } schema ? LayerSchema.FromUpload(schema) : null,
                SourceSchema = body.SourceSchema is { } source ? LayerSchema.FromUpload(source) : null,
                Overrides = body.Overrides ?? [],
                LookupOverrides = body.LookupOverrides ?? [],
                Comment = body.Comment,
            }, principal.Actor, cancellationToken);
            return TypedResults.Ok(new LayerCreated(result.Layer.Ref, result.Layer.ContentHash, result.Updates));
        }
        catch (ArgumentException ex)
        {
            return Bad(ex.Message);
        }
    }

    private static async Task<Ok<IReadOnlyList<AuditEvent>>> ListAuditAsync(
        string organizationId, IAuditSink audit, CancellationToken cancellationToken, int limit = 100) =>
        TypedResults.Ok(await audit.ListAsync(organizationId, Math.Clamp(limit, 1, 500), cancellationToken));

    private static async Task<Results<Ok<PublicationResult>, UnprocessableEntity<PublicationResult>, ProblemHttpResult>> BindAsync(
        string organizationId, string templateId, string operation, BindBody body, LayeredTemplateService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        if (body.Top is null)
        {
            return Bad("Informe 'top' (camada do topo da cadeia).");
        }

        return Publication(await service.BindAndPublishAsync(key, body.Top, body.Policy, body.Scenarios ?? [], http.Principal().Actor, cancellationToken));
    }

    private static async Task<Results<Ok<TemplateBinding>, ProblemHttpResult>> ConfigureBindingAsync(
        string organizationId, string templateId, string operation, ConfigureBindingBody body, LayeredTemplateService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        try
        {
            return TypedResults.Ok(await service.ConfigureBindingAsync(key, body.Policy, body.Scenarios, http.Principal().Actor, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Bad(ex.Message);
        }
    }

    private static async Task<Results<Ok<PublicationResult>, UnprocessableEntity<PublicationResult>, ProblemHttpResult>> PublishTopAsync(
        string organizationId, string templateId, string operation, PublishTopBody body, LayeredTemplateService service, HttpContext http, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        if (body.Top is null)
        {
            return Bad("Informe 'top'.");
        }

        return Publication(await service.PublishAsync(key, body.Top, http.Principal().Actor, cancellationToken));
    }

    private static async Task<Results<Ok<PublicationResult>, UnprocessableEntity<PublicationResult>, ProblemHttpResult>> PublishContentAsync(
        string organizationId, string templateId, string operation, ContentPublicationBody body, ContentPublicationService service,
        HttpContext http, CancellationToken cancellationToken) =>
        TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem)
            ? await TemplateRequests.PublishContentAsync(key, body, service, http, cancellationToken)
            : problem;

    private static async Task<Results<Ok<RollbackResult>, Conflict<RollbackResult>, ProblemHttpResult>> RollbackAsync(
        string organizationId, string templateId, string operation, RollbackBody? body, LayeredTemplateService service,
        HttpContext http, CancellationToken cancellationToken) =>
        TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem)
            ? await TemplateRequests.RollbackAsync(key, body, service, http, cancellationToken)
            : problem;

    private static Task<Results<Ok<TemplateUpdate>, NotFound, ProblemHttpResult>> ApproveAsync(
        string organizationId, string templateId, string operation, string updateId, LayeredTemplateService service, HttpContext http, CancellationToken cancellationToken) =>
        Decide(organizationId, templateId, operation, (key, actor) => service.ApproveUpdateAsync(key, updateId, actor, cancellationToken), http);

    private static Task<Results<Ok<TemplateUpdate>, NotFound, ProblemHttpResult>> RejectAsync(
        string organizationId, string templateId, string operation, string updateId, LayeredTemplateService service, HttpContext http, CancellationToken cancellationToken) =>
        Decide(organizationId, templateId, operation, (key, actor) => service.RejectUpdateAsync(key, updateId, actor, cancellationToken), http);

    private static async Task<Results<Ok<TemplateUpdate>, NotFound, ProblemHttpResult>> Decide(
        string organizationId, string templateId, string operation, Func<TemplateKey, string, Task<TemplateUpdate>> action, HttpContext http)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        try
        {
            return TypedResults.Ok(await action(key, http.Principal().Actor));
        }
        catch (ArgumentException)
        {
            return TypedResults.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Atualização não pode ser decidida");
        }
    }

    private static async Task<Results<Ok<CaptureWindow>, ProblemHttpResult>> EnableCaptureAsync(
        string organizationId, string templateId, string operation, CaptureBody? body, DiagnosticCaptureService capture, HttpContext http, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        try
        {
            var duration = body?.Minutes is { } minutes ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
            return TypedResults.Ok(await capture.EnableAsync(key, duration, http.Principal().Actor, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return Bad(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Captura diagnóstica indisponível");
        }
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DisableCaptureAsync(
        string organizationId, string templateId, string operation, DiagnosticCaptureService capture, HttpContext http, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        await capture.DisableAsync(key, http.Principal().Actor, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<DiagnosticRecord>>, ProblemHttpResult>> ReadCaptureAsync(
        string organizationId, string templateId, string operation, DiagnosticCaptureService capture, HttpContext http, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        if (!capture.Available)
        {
            return TypedResults.Problem("Captura diagnóstica indisponível neste ambiente.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.Ok(await capture.ReadAsync(key, http.Principal().Actor, cancellationToken));
    }

    private static async Task<Results<Ok<long>, ProblemHttpResult>> DeleteCaptureAsync(
        string organizationId, string templateId, string operation, DiagnosticCaptureService capture, HttpContext http, CancellationToken cancellationToken)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await capture.DeleteAsync(key, http.Principal().Actor, cancellationToken));
    }

    private static async Task<IResult> ExportPackageAsync(
        string organizationId, string templateId, string operation, Tradux.Application.Packaging.PackageService packages, HttpContext http, CancellationToken cancellationToken, int? version = null)
    {
        if (!TemplateRequests.TryKey(organizationId, templateId, operation, out var key, out var problem))
        {
            return problem;
        }

        try
        {
            var package = await packages.ExportAsync(key, version, http.Principal().Actor, cancellationToken);
            return package is null
                ? TypedResults.NotFound()
                : TypedResults.File(package, "application/zip", $"{templateId}-{operation}.traduxpkg");
        }
        catch (Tradux.Application.Packaging.PackageValidationException ex)
        {
            return TypedResults.Problem(string.Join(" | ", ex.Problems), statusCode: StatusCodes.Status422UnprocessableEntity, title: "Exportação recusada");
        }
    }

    private static async Task<IResult> ImportPackageAsync(
        string organizationId, HttpRequest request, Tradux.Application.Packaging.PackageService packages, HttpContext http, CancellationToken cancellationToken,
        string? templateId = null, string? operation = null)
    {
        // Parâmetros opcionais na assinatura: ausência vira 400 aqui, DEPOIS do filtro
        // de organização (vínculo obrigatório responderia 400 antes dele).
        if (!TemplateRequests.TryKey(organizationId, templateId ?? "", operation ?? "", out var key, out var problem))
        {
            return problem;
        }

        // Importar publica e cria camadas: exige também as permissões dessas ações
        // (revisão da Fase 10, A1) — template.import sozinho não basta.
        var principal = http.Principal();
        if (!principal.Can(Permissions.TemplatePublish) || !principal.Can(Permissions.TemplateCreate))
        {
            return TypedResults.Forbid();
        }

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        try
        {
            var result = await packages.ImportAsync(body.ToArray(), key, http.Principal().Actor, cancellationToken);
            return result.Success ? TypedResults.Ok(result) : TypedResults.UnprocessableEntity(result);
        }
        catch (Tradux.Application.Packaging.PackageValidationException ex)
        {
            return TypedResults.Problem(string.Join(" | ", ex.Problems), statusCode: StatusCodes.Status400BadRequest, title: "Pacote inválido");
        }
    }

    private static Results<Ok<PublicationResult>, UnprocessableEntity<PublicationResult>, ProblemHttpResult> Publication(PublicationResult result) =>
        TemplateRequests.Publication(result);

    private static ProblemHttpResult Bad(string message) => TemplateRequests.Bad(message);
}

/// <summary>Duração da captura em minutos (padrão da configuração se omitida).</summary>
public sealed record CaptureBody(int? Minutes = null);
