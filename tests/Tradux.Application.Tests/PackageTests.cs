using System.IO.Compression;
using System.Text;
using Tradux.Application.Layers;
using Tradux.Application.Packaging;
using Tradux.Domain.Layers;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

using Tradux.Application.Testing;
using Tradux.Application.Publishing;
namespace Tradux.Application.Tests;

/// <summary>Fase 10 (seção 22, ADR-0013): pacote portátil entre ambientes e validação na importação.</summary>
public class PackageTests
{
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation);

    // Cada TestServices é um "ambiente": repositórios próprios, como um banco distinto.

    // Cadeia toda da org-a: base → produto → cliente.
    private static async Task PublishOwnChainAsync(TestServices env)
    {
        var admin = env.Layered;
        var baseRef = (await admin.CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer() with { OrganizationId = Key.OrganizationId }, "a")).Layer.Ref;
        var product = (await admin.CreateLayerVersionAsync(LayeredTemplateServiceTests.ProductLayer(baseRef, "P-") with { OrganizationId = Key.OrganizationId }, "a")).Layer.Ref;
        var client = (await admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = Key.OrganizationId, LayerId = "cliente", Kind = LayerKind.Client, Parent = product,
            Overrides = [new LayerOverride("/DPS/numero", OverrideOperation.Set, MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode("C-"), new FieldReferenceNode("Numero"))))],
        }, "a")).Layer.Ref;
        Assert.True((await admin.BindAndPublishAsync(Key, client, UpdatePolicy.Automatic, SnapshotFixtures.DpsScenarios(), "a")).Success);
    }

    private static Dictionary<string, byte[]> Unzip(byte[] package)
    {
        using var zip = new ZipArchive(new MemoryStream(package));
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var s = e.Open();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        });
    }

    private static byte[] Zip(IReadOnlyDictionary<string, byte[]> entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in entries)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    // Reescreve uma entrada e o manifesto de forma coerente (sem o filtro de segredos do Write).
    private static byte[] Forge(byte[] package, string path, byte[] content)
    {
        var entries = Unzip(package);
        entries[path] = content;
        var manifest = CanonicalJson.Deserialize<PackageManifest>(entries[PackagePaths.Manifest]);
        manifest = manifest with
        {
            Entries = entries.Where(e => e.Key != PackagePaths.Manifest).OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new PackageEntry(e.Key, PackageArchive.Sha256(e.Value), e.Value.LongLength)).ToList(),
        };
        entries[PackagePaths.Manifest] = CanonicalJson.Serialize(manifest);
        return Zip(entries);
    }

    // ------------------------------------------------------------------ gate: hashes preservados

    [Fact]
    public async Task Layered_package_moves_between_environments_with_hashes_preserved()
    {
        var a = new TestServices();
        var b = new TestServices();
        await PublishOwnChainAsync(a);
        var packageA = (await a.Packages.ExportAsync(Key, null, "a"))!;

        var imported = await b.Packages.ImportAsync(packageA, Key, "b");

        Assert.True(imported.Success, string.Join("; ", imported.Diagnostics.Select(d => d.Code + " " + d.Message)));
        Assert.True(imported.HashPreserved);
        Assert.Equal((await a.Repository.GetActiveAsync(Key))!.ContentHash, imported.SnapshotContentHash);
        var packageB = (await b.Packages.ExportAsync(Key, null, "b"))!;
        var entriesA = CanonicalJson.Deserialize<PackageManifest>(Unzip(packageA)[PackagePaths.Manifest]).Entries;
        var entriesB = CanonicalJson.Deserialize<PackageManifest>(Unzip(packageB)[PackagePaths.Manifest]).Entries;
        Assert.Equal(entriesA, entriesB); // mesmo caminho, SHA-256 e tamanho por entrada
        Assert.Contains(entriesA, e => e.Path.StartsWith("layers/", StringComparison.Ordinal));
        Assert.Contains(b.Audit.Events, e => e.Action == "template.import" && e.Outcome == "success");
        Assert.Contains(a.Audit.Events, e => e.Action == "template.export" && e.Outcome == "success");

        // Mesma geração nos dois ambientes.
        var request = new Contracts.GenerateXmlRequest(Key.OrganizationId, Key.TemplateId, Key.Operation, SnapshotFixtures.DpsJson());
        Assert.Equal((await a.Generation.GenerateAsync(request)).Xml, (await b.Generation.GenerateAsync(request)).Xml);
    }

    [Fact]
    public async Task Content_package_can_be_promoted_to_another_organization_with_the_same_hash()
    {
        var a = new TestServices();
        await a.PublishAsync(Key, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "a");
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;
        var b = new TestServices();

        var imported = await b.Packages.ImportAsync(package, new TemplateKey("org-producao", "dps", "emissao"), "b");

        Assert.True(imported.Success);
        Assert.Equal((await a.Repository.GetActiveAsync(Key))!.ContentHash, imported.SnapshotContentHash);
        Assert.NotEmpty((await b.Repository.GetActiveAsync(new TemplateKey("org-producao", "dps", "emissao")))!.Scenarios);
    }

    // ------------------------------------------------------------------ validação

    [Fact]
    public async Task Tampered_unlisted_or_dangerous_entries_are_refused_before_anything_is_written()
    {
        var a = new TestServices();
        await PublishOwnChainAsync(a);
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;
        var b = new TestServices();

        var tampered = Unzip(package);
        tampered[PackagePaths.SnapshotContent] = [.. tampered[PackagePaths.SnapshotContent], (byte)' '];
        var unlisted = Unzip(package);
        unlisted["extra.txt"] = "x"u8.ToArray();
        var traversal = Unzip(package);
        traversal["../fora.json"] = "{}"u8.ToArray();

        foreach (var (bad, reason) in new[] { (Zip(tampered), "adulterado"), (Zip(unlisted), "não está no manifesto"), (Zip(traversal), "perigoso"), ("nao-zip"u8.ToArray(), "ZIP") })
        {
            var ex = await Assert.ThrowsAsync<PackageValidationException>(() => b.Packages.ImportAsync(bad, Key, "b"));
            Assert.Contains(ex.Problems, p => p.Contains(reason, StringComparison.Ordinal));
        }

        Assert.Null(await b.Repository.GetActiveVersionAsync(Key));
        Assert.Null(await b.Layers.GetLatestAsync(Key.OrganizationId, "base-dps"));
    }

    [Fact]
    public async Task Packages_never_carry_secrets_on_export_or_import()
    {
        var a = new TestServices();
        var content = SnapshotFixtures.SimpleDps();
        var withKey = content with
        {
            LookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>>(content.LookupTables)
            {
                ["certificados"] = new Dictionary<string, string> { ["a"] = "-----BEGIN PRIVATE KEY-----" },
            },
        };
        await a.PublishAsync(Key, withKey, SnapshotFixtures.DpsScenarios(), "a");

        await Assert.ThrowsAsync<PackageValidationException>(() => a.Packages.ExportAsync(Key, null, "a"));
        Assert.Contains(a.Audit.Events, e => e.Action == "template.export" && e.Outcome == "rejected");

        // Pacote forjado com manifesto coerente contendo credencial/campo de senha.
        await a.PublishAsync(Key, content, SnapshotFixtures.DpsScenarios(), "a");
        var clean = (await a.Packages.ExportAsync(Key, null, "a"))!;
        foreach (var secret in new[] { "{\"senha\":\"123456\"}", "\"tradux_AbCdEfGhIjKlMnOpQrStUvWxYz0123456789\"" })
        {
            var forged = Forge(clean, "notas/extra.json", Encoding.UTF8.GetBytes(secret));
            await Assert.ThrowsAsync<PackageValidationException>(() => new TestServices().Packages.ImportAsync(forged, Key, "b"));
        }

        var certificate = Forge(clean, "cert/cliente.pfx", [1, 2, 3]);
        await Assert.ThrowsAsync<PackageValidationException>(() => new TestServices().Packages.ImportAsync(certificate, Key, "b"));
    }

    [Fact]
    public async Task Diverging_layer_or_foreign_organization_is_a_conflict_not_a_silent_new_hash()
    {
        var a = new TestServices();
        await PublishOwnChainAsync(a);
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;

        // Ambiente B já tem uma base-dps@1 diferente.
        var b = new TestServices();
        await b.Layered.CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer(keepPath: p => p != "/DPS/serie") with { OrganizationId = Key.OrganizationId }, "b");
        var conflict = await b.Packages.ImportAsync(package, Key, "b");
        Assert.False(conflict.Success);
        Assert.Contains(conflict.Diagnostics, d => d.Code == PackageService.LayerConflictCode);
        Assert.Null(await b.Repository.GetActiveVersionAsync(Key));

        var otherOrg = await new TestServices().Packages.ImportAsync(package, new TemplateKey("org-b", Key.TemplateId, Key.Operation), "b");
        Assert.Contains(otherOrg.Diagnostics, d => d.Code == PackageService.OrganizationMismatchCode);
    }

    [Fact]
    public async Task Layer_whose_content_does_not_match_its_own_hash_is_refused()
    {
        var a = new TestServices();
        await PublishOwnChainAsync(a);
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;
        var entries = Unzip(package);
        var clientPath = entries.Keys.Single(k => k.StartsWith($"layers/{Key.OrganizationId}/cliente/", StringComparison.Ordinal));
        var forgedLayer = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entries[clientPath]).Replace("C-", "X-"));

        var b = new TestServices();
        var result = await b.Packages.ImportAsync(Forge(package, clientPath, forgedLayer), Key, "b");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == PackageService.LayerIntegrityCode);
        // Todas as camadas são validadas antes de gravar qualquer uma.
        Assert.Null(await b.Layers.GetLatestAsync(Key.OrganizationId, "base-dps"));
    }

    [Fact]
    public async Task Forged_layer_with_recomputed_hashes_is_refused_before_changing_anything()
    {
        var a = new TestServices();
        await PublishOwnChainAsync(a);
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;
        var entries = Unzip(package);
        var clientPath = entries.Keys.Single(k => k.StartsWith($"layers/{Key.OrganizationId}/cliente/", StringComparison.Ordinal));

        // Forja coerente: camada com hash próprio recalculado, manifesto do snapshot com
        // esse hash e hash do snapshot recalculado sobre o content.json ORIGINAL.
        var layer = CanonicalJson.Deserialize<TemplateLayer>(entries[clientPath]);
        IReadOnlyList<LayerOverride> overrides = [new LayerOverride("/DPS/numero", OverrideOperation.Set, MappingEntry.Scalar(OperatorNode.Of("concat", new LiteralNode("X-"), new FieldReferenceNode("Numero"))))];
        var layerHash = LayerHashing.Compute(layer with { Overrides = overrides });
        entries[clientPath] = CanonicalJson.Serialize(layer with { Overrides = overrides, ContentHash = layerHash });
        var snapshotManifest = CanonicalJson.Deserialize<SnapshotManifest>(entries[PackagePaths.SnapshotManifest]);
        snapshotManifest = snapshotManifest with { Layers = snapshotManifest.Layers.Select(l => l.Ref == layer.Ref ? l with { ContentHash = layerHash } : l).ToList() };
        entries[PackagePaths.SnapshotManifest] = CanonicalJson.Serialize(snapshotManifest);
        var content = CanonicalJson.Deserialize<Snapshots.SnapshotContent>(entries[PackagePaths.SnapshotContent]);
        var candidate = new SnapshotPublisher(a.Repository, a.Compiler, Generation.GenerationLimits.Default).Prepare(content, out _)!;
        var manifest = CanonicalJson.Deserialize<PackageManifest>(entries[PackagePaths.Manifest]) with
        {
            SnapshotContentHash = candidate.SnapshotHash(snapshotManifest),
            Entries = entries.Where(e => e.Key != PackagePaths.Manifest).OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => new PackageEntry(e.Key, PackageArchive.Sha256(e.Value), e.Value.LongLength)).ToList(),
        };
        entries[PackagePaths.Manifest] = CanonicalJson.Serialize(manifest);

        var b = new TestServices();
        var result = await b.Packages.ImportAsync(Zip(entries), Key, "b");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == PackageService.HashMismatchCode);
        Assert.Null(await b.Repository.GetActiveVersionAsync(Key));
        Assert.Null(await b.Layers.GetLatestAsync(Key.OrganizationId, "cliente"));
        Assert.Null(await b.Layers.GetLatestAsync(Key.OrganizationId, "base-dps"));
    }

    [Fact]
    public async Task Import_goes_through_the_gates_with_the_package_scenarios()
    {
        var a = new TestServices();
        await a.PublishAsync(Key, SnapshotFixtures.SimpleDps(), SnapshotFixtures.DpsScenarios(), "a");
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;
        var wrong = CanonicalJson.Serialize(new[]
        {
            new FunctionalScenario("errado", SnapshotFixtures.DpsJson(), Assertions: [new StructuralAssertion("/DPS/numero", AssertionKind.Equals, "000000")]),
        });

        var result = await new TestServices().Packages.ImportAsync(Forge(package, PackagePaths.Scenarios, wrong), Key, "b");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == FunctionalScenarioGate.ScenarioFailedCode);
    }

    // ------------------------------------------------------------------ camadas de outra organização e hashes

    [Fact]
    public async Task Foreign_private_layers_never_travel_in_the_package_but_the_hash_is_still_preserved()
    {
        // Cliente org-b → produto org-a (compartilhado) → base org-a NÃO compartilhada.
        var a = new TestServices();
        var admin = a.Layered;
        var privateBase = (await admin.CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer() with
        {
            OrganizationId = "org-a", LayerId = "base-secreta", Shared = false, Comment = "COMENTARIO-INTERNO-ORG-A",
        }, "fulano@org-a")).Layer.Ref;
        var product = (await admin.CreateLayerVersionAsync(LayeredTemplateServiceTests.ProductLayer(privateBase, "P-") with { OrganizationId = "org-a", Shared = true }, "fulano@org-a")).Layer.Ref;
        var client = (await admin.CreateLayerVersionAsync(new NewLayerVersion { OrganizationId = "org-b", LayerId = "cliente", Kind = LayerKind.Client, Parent = product }, "b")).Layer.Ref;
        Assert.True((await admin.BindAndPublishAsync(new TemplateKey("org-b", "dps", "emissao"), client, UpdatePolicy.Manual, SnapshotFixtures.DpsScenarios(), "b")).Success);

        var package = (await a.Packages.ExportAsync(new TemplateKey("org-b", "dps", "emissao"), null, "b"))!;

        var entries = Unzip(package);
        Assert.DoesNotContain(entries.Keys, k => k.StartsWith("layers/org-a/", StringComparison.Ordinal));
        Assert.Contains(entries.Keys, k => k.StartsWith("layers/org-b/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries.Values, v => Encoding.UTF8.GetString(v).Contains("COMENTARIO-INTERNO-ORG-A") || Encoding.UTF8.GetString(v).Contains("fulano@org-a"));

        // Destino sem as camadas da org-a: conflito, nada publicado.
        var empty = new TestServices();
        var refused = await empty.Packages.ImportAsync(package, new TemplateKey("org-b", "dps", "emissao"), "b");
        Assert.Contains(refused.Diagnostics, d => d.Code == PackageService.LayerConflictCode);
        Assert.Null(await empty.Repository.GetActiveVersionAsync(new TemplateKey("org-b", "dps", "emissao")));

        // Destino onde a org-a já promoveu as mesmas camadas: hash preservado.
        var b = new TestServices();
        foreach (var reference in new[] { privateBase, product })
        {
            await b.Layers.ImportVersionAsync((await a.Layers.GetAsync(reference))!);
        }

        var imported = await b.Packages.ImportAsync(package, new TemplateKey("org-b", "dps", "emissao"), "b");
        Assert.True(imported.Success, string.Join("; ", imported.Diagnostics.Select(d => d.Code)));
        Assert.Equal((await a.Repository.GetActiveAsync(new TemplateKey("org-b", "dps", "emissao")))!.ContentHash, imported.SnapshotContentHash);
    }

    [Fact]
    public async Task Declared_hash_mismatch_or_malformed_json_is_refused_before_any_write()
    {
        var a = new TestServices();
        await PublishOwnChainAsync(a);
        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;

        // Manifesto que declara outro hash de snapshot (entradas coerentes).
        var entries = Unzip(package);
        var manifest = CanonicalJson.Deserialize<PackageManifest>(entries[PackagePaths.Manifest]) with { SnapshotContentHash = new string('0', 64) };
        entries[PackagePaths.Manifest] = CanonicalJson.Serialize(manifest);
        var b = new TestServices();
        var mismatch = await b.Packages.ImportAsync(Zip(entries), Key, "b");
        Assert.Contains(mismatch.Diagnostics, d => d.Code == PackageService.HashMismatchCode);
        Assert.Null(await b.Repository.GetActiveVersionAsync(Key));
        Assert.Null(await b.Layers.GetLatestAsync(Key.OrganizationId, "cliente"));

        // JSON malformado com hash coerente: pacote inválido (não exceção crua), auditado.
        var malformed = Forge(package, PackagePaths.Scenarios, "{ não é json"u8.ToArray());
        await Assert.ThrowsAsync<PackageValidationException>(() => b.Packages.ImportAsync(malformed, Key, "b"));
        Assert.Contains(b.Audit.Events, e => e.Action == "template.import" && e.Outcome == "rejected");
    }

    [Fact]
    public async Task Layered_snapshot_without_stored_scenarios_exports_the_binding_scenarios()
    {
        var a = new TestServices();
        await PublishOwnChainAsync(a);
        // Simula snapshot anterior à Fase 10: republica o mesmo conteúdo sem cenários direto no repositório.
        var active = (await a.Repository.GetActiveAsync(Key))!;
        await a.Repository.PublishAsync(new Snapshots.SnapshotDraft(Key, active.ContentHash + "-legado", DateTimeOffset.UtcNow, active.Content) { Manifest = active.Manifest });

        var package = (await a.Packages.ExportAsync(Key, null, "a"))!;

        var scenarios = CanonicalJson.Deserialize<List<FunctionalScenario>>(Unzip(package)[PackagePaths.Scenarios]);
        Assert.NotEmpty(scenarios);
    }
}
