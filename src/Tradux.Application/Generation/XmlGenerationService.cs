using System.Diagnostics;
using System.Globalization;
using Tradux.Application.Caching;
using Tradux.Application.Snapshots;
using Tradux.Contracts;
using Tradux.Domain.SourceData;
using Tradux.Engine;
using Tradux.Xsd;

namespace Tradux.Application.Generation;

/// <summary>
/// Caso de uso "Gerar XML" a partir de um snapshot publicado (seção 26):
/// o único ponto de entrada de geração da API. A entrada é JSON
/// (<see cref="GenerateAsync"/>) ou XML de origem validado pelo XSD de origem
/// do template (<see cref="TransformAsync"/>, ADR-0015) — daí em diante o
/// caminho é o mesmo.
///
/// Com o cache aquecido, nada aqui acessa o repositório (ADR-0005).
/// </summary>
public sealed class XmlGenerationService
{
    public const string SnapshotNotFoundCode = "TEMPLATE_SNAPSHOT_NOT_FOUND";
    public const string SnapshotCompilationFailedCode = "TEMPLATE_SNAPSHOT_COMPILATION_FAILED";
    public const string SourceSchemaMissingCode = "TEMPLATE_WITHOUT_SOURCE_SCHEMA";
    public const string SourceXmlInvalidCode = "SOURCE_XML_INVALID";

    /// <summary>Padrão do limite de versões candidatas no fallback (revisão da Fase 7, W7): cada uma pode ser compilada e ir para o cache.</summary>
    public const int DefaultMaxCandidateVersions = 5;

    private readonly CompiledSnapshotCache _cache;

    public XmlGenerationService(CompiledSnapshotCache cache, int maxCandidateVersions = DefaultMaxCandidateVersions, GenerationLimits? limits = null)
    {
        _cache = cache;
        MaxCandidateVersions = maxCandidateVersions;
        Limits = (limits ?? GenerationLimits.Default).Validated();
    }

    public int MaxCandidateVersions { get; }

    /// <summary>Limites de entrada aplicados a toda geração (ADR-0014 §3).</summary>
    public GenerationLimits Limits { get; }

    /// <summary>Converte a entrada para a árvore interna com o snapshot que vai gerar; ou diagnósticos de entrada inválida.</summary>
    private delegate InputOutcome InputResolver(CompiledSnapshot compiled);

    private readonly record struct InputOutcome(SourceDataTree? Tree, IReadOnlyList<Diagnostic> Failures);

    /// <summary>Gera a partir de dados JSON.</summary>
    /// <exception cref="ArgumentException"><see cref="GenerateXmlRequest.Data"/> não é um objeto JSON válido para a árvore interna.</exception>
    public Task<GenerationResult> GenerateAsync(GenerateXmlRequest request, CancellationToken cancellationToken = default)
    {
        ValidateCorrelationId(request.CorrelationId);
        var key = new TemplateKey(request.OrganizationId, request.TemplateId, request.Operation);
        var data = SourceInputs.Json(request.Data, Limits);
        InputOutcome Input(CompiledSnapshot _) => new(data, []);
        return request.CandidateVersions is { Count: > 0 } candidates
            ? RunFallbackAsync(key, Input, candidates, request.CorrelationId, cancellationToken)
            : RunAsync(key, Input, request.Version, request.CorrelationId, cancellationToken);
    }

    /// <summary>
    /// Gera a partir de um XML de origem (ADR-0015), lido dos bytes (respeita a
    /// codificação declarada no XML): valida contra o XSD de origem da versão
    /// usada, converte para a árvore interna e segue o mesmo caminho da geração
    /// por JSON. XML fora do XSD → resultado com <see cref="SourceXmlInvalidCode"/>
    /// (linha/coluna); template sem XSD de origem → <see cref="SourceSchemaMissingCode"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Correlação, lista de versões ou limite de entrada.</exception>
    public async Task<GenerationResult> TransformAsync(
        TemplateKey key, byte[] sourceXml, int? version = null, IReadOnlyList<int>? candidateVersions = null, string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        SourceInputs.EnforceSize(sourceXml.Length, Limits); // antes de procurar a versão
        ValidateCorrelationId(correlationId);
        InputOutcome Resolve(CompiledSnapshot compiled)
        {
            if (compiled.Source is not { } source)
            {
                return Invalid(SourceSchemaMissingCode,
                    $"A versão {compiled.Version.ToString(CultureInfo.InvariantCulture)} de '{key}' não tem XSD de origem: não aceita XML de entrada.",
                    "Publique o template com o XSD de origem (ou envie os dados em JSON).");
            }

            try
            {
                return new InputOutcome(SourceInputs.Xml(source, sourceXml, Limits), []);
            }
            catch (SourceXmlException ex)
            {
                return new InputOutcome(null, SourceInputs.Diagnostics(ex));
            }
        }

        return candidateVersions is { Count: > 0 }
            ? await RunFallbackAsync(key, Resolve, candidateVersions, correlationId, cancellationToken).ConfigureAwait(false)
            : await RunAsync(key, Resolve, version, correlationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Prazo de uma requisição de geração, somadas todas as versões tentadas no fallback.</summary>
    public static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(8);

    public const string RequestTimeLimitCode = "GENERATION_TIME_LIMIT";

    /// <summary>
    /// A correlação vai para log e trace (seção 23): só identificador opaco,
    /// até 64 caracteres [A-Za-z0-9._:-] (revisão da Fase 9, W3). Não use
    /// dado fiscal (CNPJ, número da nota) como correlação.
    /// </summary>
    /// <exception cref="ArgumentException">Formato inválido.</exception>
    public static void ValidateCorrelationId(string? correlationId)
    {
        if (correlationId is not null
            && (correlationId.Length is 0 or > 64 || !correlationId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
        {
            throw new ArgumentException("correlationId: até 64 caracteres entre letras, dígitos e . _ : -", nameof(correlationId));
        }
    }

    /// <summary>
    /// Fallback de execução (seção 17): tenta as versões na ordem dada, usa a
    /// primeira que gera XML válido e, se todas falharem, devolve os erros
    /// agrupados por versão em <see cref="GenerationResult.VersionsAttempted"/>.
    /// O fallback é local: escolhe uma versão que gera e valida aqui, não
    /// descobre o que a prefeitura aceita (CLAUDE.md, limites do núcleo).
    /// </summary>
    private async Task<GenerationResult> RunFallbackAsync(
        TemplateKey key, InputResolver input, IReadOnlyList<int> candidateVersions, string? correlationId, CancellationToken cancellationToken)
    {
        if (candidateVersions.Count == 0 || candidateVersions.Count > MaxCandidateVersions)
        {
            throw new ArgumentException($"Informe de 1 a {MaxCandidateVersions} versões candidatas.", nameof(candidateVersions));
        }

        using var activity = Observability.TraduxTelemetry.StartGeneration(key, fallback: true, requestedVersion: null);
        var result = await FallbackCoreAsync(key, input, candidateVersions, correlationId, cancellationToken).ConfigureAwait(false);
        Observability.TraduxTelemetry.CompleteGeneration(activity, key, fallback: true, result);
        return result;
    }

    private async Task<GenerationResult> RunAsync(
        TemplateKey key, InputResolver input, int? version, string? correlationId, CancellationToken cancellationToken)
    {
        using var activity = Observability.TraduxTelemetry.StartGeneration(key, fallback: false, version);
        var result = await GenerateCoreAsync(key, input, version, correlationId, cancellationToken).ConfigureAwait(false);
        Observability.TraduxTelemetry.CompleteGeneration(activity, key, fallback: false, result);
        return result;
    }

    private async Task<GenerationResult> FallbackCoreAsync(
        TemplateKey key, InputResolver input, IReadOnlyList<int> candidateVersions, string? correlationId, CancellationToken cancellationToken)
    {
        using var deadline = Engine.Validation.GenerationDeadline.Start(RequestBudget);
        var stopwatch = Stopwatch.StartNew();
        correlationId ??= Guid.NewGuid().ToString("N");
        var attempts = new List<VersionAttempt>();

        foreach (var version in candidateVersions.Distinct())
        {
            var label = version.ToString(CultureInfo.InvariantCulture);
            if (Engine.Validation.GenerationDeadline.Expired)
            {
                // Prazo do pedido esgotado: as versões restantes não são tentadas (nunca multiplica o orçamento).
                attempts.Add(new VersionAttempt(label, false,
                    [new Diagnostic(DiagnosticSeverity.Critical, RequestTimeLimitCode, $"Tempo da requisição esgotado ({RequestBudget.TotalSeconds:0} s) antes de tentar esta versão.", null, null, null)], TimeSpan.Zero));
                continue;
            }

            CompiledSnapshot? compiled;
            try
            {
                compiled = await _cache.GetVersionAsync(key, version, cancellationToken).ConfigureAwait(false);
            }
            catch (SnapshotCompilationException ex)
            {
                attempts.Add(new VersionAttempt(label, false, [new Diagnostic(DiagnosticSeverity.Critical, SnapshotCompilationFailedCode, ex.Message, null, null, null)], TimeSpan.Zero));
                continue;
            }

            if (compiled is null)
            {
                attempts.Add(new VersionAttempt(label, false,
                    [new Diagnostic(DiagnosticSeverity.Critical, SnapshotNotFoundCode, $"Template '{key}' não tem a versão {label}.", null, null, null)], TimeSpan.Zero));
                continue;
            }

            // Cada versão lê a entrada com o próprio XSD de origem: o XML pode valer numa e não noutra.
            var resolved = input(compiled);
            if (resolved.Tree is null)
            {
                attempts.Add(new VersionAttempt(label, false, resolved.Failures, TimeSpan.Zero));
                continue;
            }

            var outcome = XmlGenerationEngine.Generate(compiled.Template, resolved.Tree);
            var diagnostics = GenerationResultMapper.ToContract(outcome.Diagnostics);
            attempts.Add(new VersionAttempt(label, outcome.Success, diagnostics, outcome.Duration));
            if (outcome.Success)
            {
                stopwatch.Stop();
                return new GenerationResult(true, outcome.Xml, diagnostics, stopwatch.Elapsed)
                {
                    VersionUsed = label,
                    VersionsAttempted = attempts,
                    CorrelationId = correlationId,
                    Metadata = Metadata(key, compiled),
                };
            }
        }

        stopwatch.Stop();
        return new GenerationResult(false, null, attempts.SelectMany(a => a.Diagnostics).ToList(), stopwatch.Elapsed)
        {
            VersionsAttempted = attempts,
            CorrelationId = correlationId,
            Metadata = Metadata(key, compiled: null),
        };
    }

    private async Task<GenerationResult> GenerateCoreAsync(
        TemplateKey key, InputResolver input, int? version, string? correlationId, CancellationToken cancellationToken)
    {
        using var deadline = Engine.Validation.GenerationDeadline.Start(RequestBudget);
        var stopwatch = Stopwatch.StartNew();
        correlationId ??= Guid.NewGuid().ToString("N");

        CompiledSnapshot? compiled;
        try
        {
            compiled = version is { } pinned
                ? await _cache.GetVersionAsync(key, pinned, cancellationToken).ConfigureAwait(false)
                : await _cache.GetActiveAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (SnapshotCompilationException ex)
        {
            return Failure(key, version, correlationId, stopwatch,
                [Diagnostic.Critical(SnapshotCompilationFailedCode, ex.Message, "Verifique se os operadores customizados usados pelo template estão registrados neste processo.")]);
        }

        if (compiled is null)
        {
            var what = version is { } v ? $"a versão {v.ToString(CultureInfo.InvariantCulture)}" : "uma versão ativa publicada";
            return Failure(key, version, correlationId, stopwatch,
                [Diagnostic.Critical(SnapshotNotFoundCode, $"Template '{key}' não tem {what}.", "Confira organização, template e operação, ou publique o template antes de gerar.")]);
        }

        var versionLabel = compiled.Version.ToString(CultureInfo.InvariantCulture);
        var resolved = input(compiled);
        if (resolved.Tree is null)
        {
            stopwatch.Stop();
            return new GenerationResult(false, null, resolved.Failures, stopwatch.Elapsed)
            {
                VersionsAttempted = [new VersionAttempt(versionLabel, false, resolved.Failures, TimeSpan.Zero)],
                CorrelationId = correlationId,
                Metadata = Metadata(key, compiled),
            };
        }

        var outcome = XmlGenerationEngine.Generate(compiled.Template, resolved.Tree);
        var diagnostics = GenerationResultMapper.ToContract(outcome.Diagnostics);
        stopwatch.Stop();

        return new GenerationResult(outcome.Success, outcome.Xml, diagnostics, stopwatch.Elapsed)
        {
            VersionUsed = outcome.Success ? versionLabel : null,
            VersionsAttempted = [new VersionAttempt(versionLabel, outcome.Success, diagnostics, outcome.Duration)],
            CorrelationId = correlationId,
            Metadata = Metadata(key, compiled),
        };
    }

    private static InputOutcome Invalid(string code, string message, string suggestion) => new(null, [Diagnostic.Critical(code, message, suggestion)]);

    private static GenerationResult Failure(
        TemplateKey key, int? version, string correlationId, Stopwatch stopwatch, IReadOnlyList<Diagnostic> diagnostics)
    {
        stopwatch.Stop();
        return new GenerationResult(false, null, diagnostics, stopwatch.Elapsed)
        {
            VersionsAttempted = version is { } v
                ? [new VersionAttempt(v.ToString(CultureInfo.InvariantCulture), false, diagnostics, TimeSpan.Zero)]
                : [],
            CorrelationId = correlationId,
            Metadata = Metadata(key, compiled: null),
        };
    }

    private static Dictionary<string, string> Metadata(TemplateKey key, CompiledSnapshot? compiled)
    {
        var metadata = new Dictionary<string, string>
        {
            ["organizationId"] = key.OrganizationId,
            ["templateId"] = key.TemplateId,
            ["operation"] = key.Operation,
        };

        if (compiled is not null)
        {
            metadata["snapshotVersion"] = compiled.Version.ToString(CultureInfo.InvariantCulture);
            metadata["snapshotContentHash"] = compiled.ContentHash;
        }

        return metadata;
    }
}
