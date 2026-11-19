using System.Globalization;
using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Publishing;
using Tradux.Application.Snapshots;
using Tradux.Application.Testing;
using Tradux.Contracts;
using Tradux.Domain.Layers;

namespace Tradux.Application.Packaging;

/// <summary>Resultado da importação de pacote.</summary>
/// <param name="Success">Publicado e com hash preservado.</param>
/// <param name="SnapshotVersion">Versão publicada neste ambiente (a numeração é local).</param>
/// <param name="SnapshotContentHash">Hash do snapshot publicado aqui.</param>
/// <param name="Diagnostics">Motivos de recusa (gates, conflito de camada, hash).</param>
/// <param name="Report">Relatório dos testes estruturais da publicação.</param>
/// <param name="HashPreserved">O snapshot publicado no destino tem o mesmo hash do manifesto (gate da Fase 10).</param>
public sealed record PackageImportResult(
    bool Success,
    int? SnapshotVersion,
    string? SnapshotContentHash,
    bool HashPreserved,
    IReadOnlyList<Diagnostic> Diagnostics,
    Contracts.Quality.QualityReport? Report = null);

/// <summary>
/// Exporta e importa o pacote portátil de um template (seção 22,
/// ADR-0013). Importar sempre publica pelos gates (compilação, testes
/// estruturais, cenários do pacote); camadas entram na versão exata.
/// </summary>
public sealed class PackageService
{
    public const string LayerConflictCode = "PACKAGE_LAYER_CONFLICT";
    public const string LayerIntegrityCode = "PACKAGE_LAYER_INTEGRITY";
    public const string OrganizationMismatchCode = "PACKAGE_ORGANIZATION_MISMATCH";
    public const string HashMismatchCode = "PACKAGE_HASH_NOT_PRESERVED";

    private sealed record BindingEntry(UpdatePolicy Policy);

    private readonly ITemplateSnapshotRepository _snapshots;
    private readonly ITemplateLayerRepository _layers;
    private readonly ITemplateBindingRepository _bindings;
    private readonly LayeredTemplateService _layered;
    private readonly ContentPublicationService _content;
    private readonly AuditTrail _audit;

    public PackageService(
        ITemplateSnapshotRepository snapshots,
        ITemplateLayerRepository layers,
        ITemplateBindingRepository bindings,
        LayeredTemplateService layered,
        ContentPublicationService content,
        IAuditSink audit)
    {
        _snapshots = snapshots;
        _layers = layers;
        _bindings = bindings;
        _layered = layered;
        _content = content;
        _audit = new AuditTrail(audit);
    }

    /// <summary>Pacote da versão ativa (ou da informada). Nulo se não há snapshot.</summary>
    /// <exception cref="PackageValidationException">Conteúdo com segredo — exportação recusada.</exception>
    public async Task<byte[]?> ExportAsync(TemplateKey key, int? version, string actor, CancellationToken cancellationToken = default)
    {
        var snapshot = version is { } v
            ? await _snapshots.GetVersionAsync(key, v, cancellationToken).ConfigureAwait(false)
            : await _snapshots.GetActiveAsync(key, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var binding = await _bindings.GetAsync(key, cancellationToken).ConfigureAwait(false);

        // Snapshot anterior à Fase 10 não guarda cenários: usa os do vínculo, se houver
        // (revisão da Fase 10, A5). Sem nenhum, a importação vai recusar (cenário obrigatório).
        var scenarios = snapshot.Scenarios.Count > 0 ? snapshot.Scenarios : binding?.Scenarios ?? [];
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PackagePaths.SnapshotContent] = CanonicalJson.Serialize(snapshot.Content),
            [PackagePaths.Scenarios] = CanonicalJson.Serialize(scenarios),
        };

        if (snapshot.Manifest is { } manifest)
        {
            entries[PackagePaths.SnapshotManifest] = CanonicalJson.Serialize(manifest);

            // Só as camadas da PRÓPRIA organização vão com conteúdo. Camada de outra
            // organização (ex.: base não compartilhada sob produto compartilhado) vai
            // só como referência + hash no manifesto — nunca o conteúdo alheio
            // (revisão da Fase 10, B1). A importação confere que ela já existe igual.
            foreach (var layerRef in manifest.Layers.Select(l => l.Ref).Where(r => r.OrganizationId == key.OrganizationId))
            {
                var layer = await _layers.GetAsync(layerRef, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Camada '{layerRef}' do manifesto não existe mais.");
                entries[PackagePaths.Layer(layerRef)] = CanonicalJson.Serialize(layer);
            }

            if (binding is not null)
            {
                entries[PackagePaths.Binding] = CanonicalJson.Serialize(new BindingEntry(binding.Policy));
            }
        }

        byte[] package;
        try
        {
            package = PackageArchive.Write(new PackageManifest
            {
                Format = PackageManifest.CurrentFormat,
                OrganizationId = key.OrganizationId,
                TemplateId = key.TemplateId,
                Operation = key.Operation,
                SnapshotVersion = snapshot.Version,
                SnapshotContentHash = snapshot.ContentHash,
                ExportedAtUtc = TimeProvider.System.GetUtcNow(),
                Entries = [],
            }, entries);
        }
        catch (PackageValidationException ex)
        {
            await _audit.RecordAsync(key, actor, "template.export", AuditTrail.Rejected, new() { ["problems"] = ex.Problems.Count.ToString(CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await _audit.RecordAsync(key, actor, "template.export", AuditTrail.Success, new()
        {
            ["version"] = snapshot.Version.ToString(CultureInfo.InvariantCulture),
            ["hash"] = snapshot.ContentHash,
            ["layers"] = (snapshot.Manifest?.Layers.Count ?? 0).ToString(CultureInfo.InvariantCulture),
        }, cancellationToken).ConfigureAwait(false);
        return package;
    }

    /// <summary>
    /// Importa para <paramref name="target"/>. Pacote com camadas só na mesma
    /// organização de origem (ADR-0013 §5).
    /// </summary>
    /// <exception cref="PackageValidationException">Pacote inválido (nada é gravado).</exception>
    public async Task<PackageImportResult> ImportAsync(byte[] package, TemplateKey target, string actor, CancellationToken cancellationToken = default)
    {
        PackageManifest manifest;
        IReadOnlyDictionary<string, byte[]> entries;
        SnapshotContent content;
        List<FunctionalScenario> scenarios;
        SnapshotManifest? snapshotManifest;
        UpdatePolicy policy;
        try
        {
            (manifest, entries) = PackageArchive.Read(package);
            content = CanonicalJson.Deserialize<SnapshotContent>(entries[PackagePaths.SnapshotContent]);
            scenarios = CanonicalJson.Deserialize<List<FunctionalScenario>>(entries[PackagePaths.Scenarios]);
            snapshotManifest = entries.TryGetValue(PackagePaths.SnapshotManifest, out var m) ? CanonicalJson.Deserialize<SnapshotManifest>(m) : null;
            policy = entries.TryGetValue(PackagePaths.Binding, out var b) ? CanonicalJson.Deserialize<BindingEntry>(b).Policy : UpdatePolicy.Manual;
        }
        catch (Exception ex) when (ex is PackageValidationException or System.Text.Json.JsonException or NullReferenceException or KeyNotFoundException or InvalidOperationException)
        {
            // JSON malformado com hashes coerentes também é pacote inválido (400), e toda recusa é auditada (revisão A4).
            var validation = ex as PackageValidationException ?? new PackageValidationException([$"Entrada JSON inválida no pacote ({ex.GetType().Name})."]);
            await _audit.RecordAsync(target, actor, "template.import", AuditTrail.Rejected, new() { ["problems"] = validation.Problems.Count.ToString(CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
            throw validation;
        }

        // Hash do snapshot declarado conferido ANTES de gravar qualquer coisa (revisão A3):
        // o que for publicado a partir destas camadas/conteúdo terá este hash.
        PublicationCandidate candidate;
        try
        {
            if (_content.Prepare(content, out var failure) is not { } prepared)
            {
                return await FailAsync(target, actor, failure!.Code, failure.Message, cancellationToken).ConfigureAwait(false);
            }

            candidate = prepared;
        }
        catch (NullReferenceException ex)
        {
            // Conteúdo com campos ausentes também é recusa auditada, não 500 (revisão R3).
            return await FailAsync(target, actor, Generation.XmlGenerationService.SnapshotCompilationFailedCode, $"Conteúdo do snapshot inválido ({ex.GetType().Name}).", cancellationToken).ConfigureAwait(false);
        }

        if (candidate.SnapshotHash(snapshotManifest) != manifest.SnapshotContentHash)
        {
            return await FailAsync(target, actor, HashMismatchCode, "O conteúdo do pacote não confere com o hash de snapshot declarado no manifesto.", cancellationToken).ConfigureAwait(false);
        }

        PublicationResult publication;
        if (snapshotManifest is { Layers.Count: > 0 })
        {
            if (target.OrganizationId != manifest.OrganizationId)
            {
                return await FailAsync(target, actor, OrganizationMismatchCode,
                    "Pacote com camadas só pode ser importado na organização de origem (as referências e os hashes dependem dela).", cancellationToken).ConfigureAwait(false);
            }

            // 1ª passada: valida TODAS as camadas e resolve a cadeia em memória, sem
            // gravar nada (revisão R1/R2). A cadeia resolvida é exatamente o que será
            // publicado — o hash declarado é conferido contra ela, não só contra content.json.
            var chain = new List<TemplateLayer>(snapshotManifest.Layers.Count);
            var toWrite = new List<TemplateLayer>();
            foreach (var declared in snapshotManifest.Layers)
            {
                var reference = declared.Ref;
                if (reference.OrganizationId != target.OrganizationId)
                {
                    // Camada de terceiros: não vem no pacote; precisa já existir aqui com o
                    // mesmo hash — nunca é criada em nome de outra organização.
                    var foreign = await _layers.GetAsync(reference, cancellationToken).ConfigureAwait(false);
                    if (foreign is null || foreign.ContentHash != declared.ContentHash)
                    {
                        return await FailAsync(target, actor, LayerConflictCode,
                            $"Camada '{reference}' de outra organização não existe neste ambiente com o mesmo conteúdo.", cancellationToken).ConfigureAwait(false);
                    }

                    chain.Add(foreign);
                    continue;
                }

                if (!entries.TryGetValue(PackagePaths.Layer(reference), out var layerBytes))
                {
                    return await FailAsync(target, actor, LayerIntegrityCode, $"Camada '{reference}' do manifesto não está no pacote.", cancellationToken).ConfigureAwait(false);
                }

                TemplateLayer layer;
                try
                {
                    layer = CanonicalJson.Deserialize<TemplateLayer>(layerBytes);
                }
                catch (System.Text.Json.JsonException)
                {
                    return await FailAsync(target, actor, LayerIntegrityCode, $"Camada '{reference}' com JSON inválido.", cancellationToken).ConfigureAwait(false);
                }

                // Hash recalculado = integridade (corrupção/edição descuidada), não
                // autenticidade: quem recalcula o hash passa daqui — por isso a camada
                // ainda precisa bater com o hash do snapshot já conferido acima e passar
                // pelos gates na publicação (revisão A2).
                if (layer.Ref != reference || LayerHashing.Compute(layer) != layer.ContentHash || layer.ContentHash != declared.ContentHash || layer.Kind != declared.Kind)
                {
                    return await FailAsync(target, actor, LayerIntegrityCode, $"Camada '{reference}' não confere com o próprio hash de conteúdo.", cancellationToken).ConfigureAwait(false);
                }

                if (await _layers.GetLatestAsync(reference.OrganizationId, reference.LayerId, cancellationToken).ConfigureAwait(false) is { } latest && latest.Kind != layer.Kind)
                {
                    return await FailAsync(target, actor, LayerConflictCode, $"Camada '{reference.LayerId}' já existe aqui com outro tipo ({latest.Kind}).", cancellationToken).ConfigureAwait(false);
                }

                if (await _layers.GetAsync(reference, cancellationToken).ConfigureAwait(false) is { } existing && existing.ContentHash != layer.ContentHash)
                {
                    return await FailAsync(target, actor, LayerConflictCode,
                        $"Camada '{reference}' diverge neste ambiente: importe as mesmas versões ou resolva o conflito antes.", cancellationToken).ConfigureAwait(false);
                }

                chain.Add(layer);
                toWrite.Add(layer);
            }

            try
            {
                var resolved = _layered.Resolve(chain);
                if (_content.Prepare(resolved.Content, out _) is not { } resolvedCandidate)
                {
                    return await FailAsync(target, actor, LayerIntegrityCode, "As camadas do pacote não formam uma cadeia válida (o conteúdo resolvido não compila).", cancellationToken).ConfigureAwait(false);
                }

                if (resolvedCandidate.SnapshotHash(resolved.Manifest) != manifest.SnapshotContentHash)
                {
                    return await FailAsync(target, actor, HashMismatchCode, "As camadas do pacote não resolvem para o hash de snapshot declarado no manifesto.", cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidLayerChainException or NullReferenceException or ArgumentException)
            {
                return await FailAsync(target, actor, LayerIntegrityCode, $"As camadas do pacote não formam uma cadeia válida ({ex.GetType().Name}).", cancellationToken).ConfigureAwait(false);
            }

            // 2ª passada: só agora grava as camadas próprias.
            foreach (var layer in toWrite)
            {
                var stored = await _layers.ImportVersionAsync(layer, cancellationToken).ConfigureAwait(false);
                if (stored.ContentHash != layer.ContentHash)
                {
                    return await FailAsync(target, actor, LayerConflictCode,
                        $"Camada '{layer.Ref}' diverge neste ambiente: importe as mesmas versões ou resolva o conflito antes.", cancellationToken).ConfigureAwait(false);
                }
            }

            publication = await _layered.BindAndPublishAsync(target, snapshotManifest.Layers[^1].Ref, policy, scenarios, actor, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            publication = await _content.PublishAsync(target, candidate, scenarios, actor, cancellationToken).ConfigureAwait(false);
        }

        if (!publication.Success)
        {
            await _audit.RecordAsync(target, actor, "template.import", AuditTrail.Rejected, new() { ["failures"] = string.Join(",", publication.Diagnostics.Select(d => d.Code).Distinct()) }, cancellationToken).ConfigureAwait(false);
            return new PackageImportResult(false, null, null, false, publication.Diagnostics, publication.Report);
        }

        var published = await _snapshots.GetVersionAsync(target, publication.SnapshotVersion!.Value, cancellationToken).ConfigureAwait(false);
        var preserved = published?.ContentHash == manifest.SnapshotContentHash;
        var diagnostics = preserved
            ? (IReadOnlyList<Diagnostic>)[]
            : [Diagnostic.Critical(HashMismatchCode, "O snapshot publicado neste ambiente não tem o hash do pacote.")];
        await _audit.RecordAsync(target, actor, "template.import", preserved ? AuditTrail.Success : "hash-mismatch", new()
        {
            ["version"] = publication.SnapshotVersion.Value.ToString(CultureInfo.InvariantCulture),
            ["hash"] = published?.ContentHash ?? "",
            ["sourceHash"] = manifest.SnapshotContentHash,
            ["source"] = $"{manifest.OrganizationId}/{manifest.TemplateId}/{manifest.Operation}@{manifest.SnapshotVersion}",
        }, cancellationToken).ConfigureAwait(false);
        return new PackageImportResult(preserved, publication.SnapshotVersion, published?.ContentHash, preserved, diagnostics, publication.Report);
    }

    private async Task<PackageImportResult> FailAsync(TemplateKey key, string actor, string code, string message, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(key, actor, "template.import", AuditTrail.Rejected, new() { ["failures"] = code }, cancellationToken).ConfigureAwait(false);
        return new PackageImportResult(false, null, null, false, [Diagnostic.Critical(code, message)]);
    }
}
