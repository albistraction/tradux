using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Snapshots;
using Tradux.Application.Testing;
using Tradux.Contracts;
using Tradux.Engine.Compilation;
using Tradux.Xsd;

namespace Tradux.Application.Publishing;

/// <summary>Resultado de uma tentativa de publicação (conteúdo, camadas ou pacote). Falha nunca altera a versão ativa.</summary>
public sealed record PublicationResult(
    bool Success,
    int? SnapshotVersion,
    IReadOnlyList<ResolutionConflict> Conflicts,
    IReadOnlyList<Diagnostic> Diagnostics,
    Contracts.Quality.QualityReport? Report = null)
{
    /// <summary>A versão ativa mudou entre a avaliação e a gravação; nada foi gravado.</summary>
    public const string ConcurrentChangeCode = "PUBLICATION_CONCURRENT_CHANGE";
}

/// <summary>
/// Conteúdo na forma que vai ser gravada (literais normalizados pelo JSON do
/// snapshot), já importado e compilado. Os gates testam e a gravação usa
/// exatamente este objeto: cada publicação compila uma vez.
/// </summary>
internal sealed record PublicationCandidate(SnapshotContent Content, string XsdContentHash, CompiledTemplate Template, SourceSchema? Source)
{
    /// <summary>Hash do snapshot: conteúdo + (se houver) camadas exatas do manifesto (ADR-0003 §3). Único lugar desta regra.</summary>
    public string SnapshotHash(SnapshotManifest? manifest)
    {
        var contentHash = SnapshotCompiler.ComputeContentHash(XsdContentHash, Content);
        if (manifest is null)
        {
            return contentHash;
        }

        var layers = string.Join('|', manifest.Layers.Select(l => $"{l.Ref}#{l.ContentHash}"));
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(contentHash + "|" + layers)));
    }
}

/// <summary>
/// Núcleo único de publicação (ADR-0004/0005, ADR-0011 §5): prepara o
/// conteúdo (normaliza e compila), roda os gates e grava o snapshot como
/// versão ativa, atomicamente e condicionado à versão ativa lida. Quem
/// publica (conteúdo, camadas, pacote) só decide o que publicar e como
/// auditar.
/// </summary>
internal sealed class SnapshotPublisher(ITemplateSnapshotRepository repository, SnapshotCompiler compiler, Generation.GenerationLimits limits)
{
    /// <summary>Normaliza e compila; nulo (com a falha crítica) se o conteúdo não serializa, não importa ou não compila.</summary>
    public PublicationCandidate? Prepare(SnapshotContent content, out Diagnostic? failure)
    {
        try
        {
            content = SnapshotJson.Rehydrate(content);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            failure = Diagnostic.Critical(Generation.XmlGenerationService.SnapshotCompilationFailedCode,
                $"Conteúdo não pode ser normalizado para publicação ({ex.GetType().Name}).");
            return null;
        }

        try
        {
            var (xsdHash, template) = compiler.CompileContent(content);
            failure = null;
            return new PublicationCandidate(content, xsdHash, template, compiler.CompileSource(content));
        }
        catch (SnapshotCompilationException ex)
        {
            failure = Diagnostic.Critical(Generation.XmlGenerationService.SnapshotCompilationFailedCode, ex.Message);
            return null;
        }
    }

    /// <summary>Gates com os limites de entrada configurados (os mesmos da geração).</summary>
    public PipelineResult Gate(PublicationCandidate candidate, IReadOnlyList<FunctionalScenario> scenarios) =>
        PublicationGates.Evaluate(candidate, scenarios, limits);

    /// <summary>
    /// Grava o candidato como nova versão ativa, só se a ativa ainda for
    /// <paramref name="expectedActiveVersion"/> (nulo = nenhuma). Idempotente
    /// por conteúdo: se a ativa já tem o mesmo hash, devolve a ativa.
    /// </summary>
    /// <exception cref="SnapshotConcurrencyException">A versão ativa mudou; nada foi gravado.</exception>
    public async Task<SnapshotSummary> WriteAsync(
        TemplateKey key, PublicationCandidate candidate, SnapshotManifest? manifest, int? expectedActiveVersion,
        IReadOnlyList<FunctionalScenario> scenarios, CancellationToken cancellationToken)
    {
        var contentHash = candidate.SnapshotHash(manifest);
        var active = await repository.GetActiveAsync(key, cancellationToken).ConfigureAwait(false);
        if (active is not null && active.ContentHash == contentHash)
        {
            return Summarize(active);
        }

        var published = await repository
            .PublishAsync(new SnapshotDraft(key, contentHash, TimeProvider.System.GetUtcNow(), candidate.Content)
            {
                Manifest = manifest,
                EnforceExpectedActive = true,
                ExpectedActiveVersion = expectedActiveVersion,
                Scenarios = scenarios,
            }, cancellationToken)
            .ConfigureAwait(false);
        return Summarize(published);
    }

    private static SnapshotSummary Summarize(TemplateSnapshot snapshot) =>
        new(snapshot.Version, snapshot.ContentHash, snapshot.Status, snapshot.PublishedAtUtc);
}
