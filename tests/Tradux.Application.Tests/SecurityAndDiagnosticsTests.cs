using System.Text.Json;
using Tradux.Application.Diagnostics;
using Tradux.Application.Security;
using Tradux.Contracts;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>Fase 9 (ADR-0012): permissões, credenciais e captura diagnóstica sem infraestrutura.</summary>
public class SecurityAndDiagnosticsTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryCredentials : IApiCredentialStore
    {
        public List<ApiCredential> Items { get; } = [];

        public int Reads { get; private set; }

        /// <summary>Simula o armazenamento fora do ar (ex.: MongoDB sem servidor elegível).</summary>
        public bool Down { get; set; }

        /// <summary>Simula um erro que NÃO é indisponibilidade (bug, dado corrompido).</summary>
        public bool Broken { get; set; }

        public int FailedReads { get; private set; }

        public Task<ApiCredential?> FindBySecretHashAsync(string secretHash, CancellationToken cancellationToken = default)
        {
            if (Down)
            {
                FailedReads++;
                throw new CredentialStoreUnavailableException("armazenamento indisponível");
            }

            if (Broken)
            {
                throw new InvalidOperationException("documento corrompido");
            }

            Reads++;
            return Task.FromResult(Items.SingleOrDefault(c => c.SecretHash == secretHash));
        }

        public Task AddAsync(ApiCredential credential, CancellationToken cancellationToken = default)
        {
            Items.Add(credential);
            return Task.CompletedTask;
        }

        public Task EnsureAsync(ApiCredential credential, CancellationToken cancellationToken = default)
        {
            if (Items.All(c => c.SecretHash != credential.SecretHash))
            {
                Items.Add(credential);
            }

            return Task.CompletedTask;
        }

        public Task<bool> RevokeAsync(string organizationId, string credentialId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken = default)
        {
            var index = Items.FindIndex(c => c.Id == credentialId && c.OrganizationId == organizationId && c.Active);
            if (index < 0)
            {
                return Task.FromResult(false);
            }

            Items[index] = Items[index] with { RevokedAtUtc = revokedAtUtc };
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<ApiCredential>> ListAsync(string organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApiCredential>>(Items.Where(c => c.OrganizationId == organizationId).ToList());
    }

    private sealed class MemoryDiagnostics(bool canProtect = true) : IDiagnosticCaptureStore
    {
        public List<CaptureWindow> Windows { get; } = [];
        public List<DiagnosticRecord> Records { get; } = [];

        public bool CanProtect { get; } = canProtect;

        public Task SetWindowAsync(CaptureWindow window, CancellationToken cancellationToken = default)
        {
            Windows.RemoveAll(w => w.Key == window.Key);
            Windows.Add(window);
            return Task.CompletedTask;
        }

        public Task RemoveWindowAsync(TemplateKey key, CancellationToken cancellationToken = default)
        {
            Windows.RemoveAll(w => w.Key == key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CaptureWindow>> ListActiveWindowsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CaptureWindow>>(Windows.Where(w => w.UntilUtc > nowUtc).ToList());

        public Task AddRecordAsync(DiagnosticRecord record, CancellationToken cancellationToken = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DiagnosticRecord>> ListRecordsAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiagnosticRecord>>(Records.Where(r => r.Key == key).ToList());

        public Task<long> DeleteRecordsAsync(TemplateKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult((long)Records.RemoveAll(r => r.Key == key));
    }

    private static readonly TemplateKey Key = new("org-a", "dps", "emissao");

    // ------------------------------------------------------------------ permissões

    [Fact]
    public void Roles_expand_to_the_section_20_permissions_and_runtime_is_use_only()
    {
        Assert.Equal(11, Permissions.All.Count);
        Assert.Equal(Permissions.All.Order(), Roles.Expand([Roles.Admin]).Order());
        Assert.Equal([Permissions.TemplateUse], Roles.Expand([Roles.Runtime]));
        Assert.DoesNotContain(Permissions.TemplatePublish, Roles.Expand([Roles.Editor]));
        Assert.Throws<ArgumentException>(() => Roles.Expand(["superusuario"]));
    }

    [Fact]
    public async Task Issued_secret_is_shown_once_and_only_its_hash_is_stored()
    {
        var store = new MemoryCredentials();
        var audit = new InMemoryAuditSink();
        var service = new CredentialService(store, audit);

        var issued = await service.IssueAsync("org-a", "integracao", [Roles.Runtime], "admin");

        Assert.StartsWith(CredentialSecrets.Prefix, issued.Secret);
        Assert.True(issued.Secret.Length > 40);
        Assert.DoesNotContain(store.Items, c => c.SecretHash == issued.Secret || c.Name.Contains(issued.Secret));
        Assert.Equal(CredentialSecrets.Hash(issued.Secret), store.Items.Single().SecretHash);
        var principal = (await service.AuthenticateAsync(issued.Secret))!;
        Assert.Equal(("org-a", true, false), (principal.OrganizationId, principal.Can(Permissions.TemplateUse), principal.Can(Permissions.TemplatePublish)));
        Assert.Null(await service.AuthenticateAsync(issued.Secret + "x"));
        Assert.Contains(audit.Events, e => e.Action == "permission.grant" && !e.Details.Values.Any(v => v.Contains(issued.Secret)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.IssueAsync("org-a", "x", ["superusuario"], "admin"));
    }

    [Fact]
    public async Task Authentication_is_cached_and_revocation_elsewhere_applies_after_the_ttl()
    {
        var store = new MemoryCredentials();
        var time = new ManualTime();
        var service = new CredentialService(store, new InMemoryAuditSink(), time, TimeSpan.FromSeconds(30));
        var issued = await service.IssueAsync("org-a", "c", [Roles.Runtime], "admin");

        await service.AuthenticateAsync(issued.Secret);
        await service.AuthenticateAsync(issued.Secret);
        Assert.Equal(1, store.Reads); // caminho aquecido não relê o armazenamento

        // Revogada por outra instância (direto no armazenamento): vale após a validade.
        await store.RevokeAsync("org-a", issued.Credential.Id, time.Now);
        Assert.NotNull(await service.AuthenticateAsync(issued.Secret));
        time.Now += TimeSpan.FromSeconds(31);
        Assert.Null(await service.AuthenticateAsync(issued.Secret));
    }

    [Fact]
    public async Task Store_outage_keeps_already_validated_credentials_only_within_the_grace()
    {
        // ADR-0014 §4: com o armazenamento fora, a credencial já validada segue
        // valendo (stale-if-error) por um tempo limitado; desconhecida não passa.
        var store = new MemoryCredentials();
        var time = new ManualTime();
        var service = new CredentialService(store, new InMemoryAuditSink(), time, TimeSpan.FromSeconds(30), staleGrace: TimeSpan.FromMinutes(10));
        var known = await service.IssueAsync("org-a", "c", [Roles.Runtime], "admin");
        var neverSeen = await service.IssueAsync("org-a", "d", [Roles.Runtime], "admin");
        Assert.NotNull(await service.AuthenticateAsync(known.Secret));

        store.Down = true;
        time.Now += TimeSpan.FromMinutes(5);
        Assert.Equal("org-a", (await service.AuthenticateAsync(known.Secret))?.OrganizationId);
        // Nova tentativa no armazenamento só após outra validade: não paga o timeout a cada pedido.
        Assert.NotNull(await service.AuthenticateAsync(known.Secret));
        Assert.Equal(1, store.FailedReads);
        time.Now += TimeSpan.FromSeconds(31);
        Assert.NotNull(await service.AuthenticateAsync(known.Secret));
        Assert.Equal(2, store.FailedReads);
        await Assert.ThrowsAsync<CredentialStoreUnavailableException>(() => service.AuthenticateAsync(neverSeen.Secret));

        time.Now += TimeSpan.FromMinutes(6); // além de validade + carência
        await Assert.ThrowsAsync<CredentialStoreUnavailableException>(() => service.AuthenticateAsync(known.Secret));

        // Voltou: relê normalmente.
        store.Down = false;
        Assert.NotNull(await service.AuthenticateAsync(known.Secret));
    }

    [Fact]
    public async Task Only_store_unavailability_releases_the_grace_other_errors_propagate()
    {
        var store = new MemoryCredentials();
        var time = new ManualTime();
        var service = new CredentialService(store, new InMemoryAuditSink(), time, TimeSpan.FromSeconds(30));
        var issued = await service.IssueAsync("org-a", "c", [Roles.Runtime], "admin");
        Assert.NotNull(await service.AuthenticateAsync(issued.Secret));

        store.Broken = true;
        time.Now += TimeSpan.FromMinutes(1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AuthenticateAsync(issued.Secret));
    }

    [Fact]
    public async Task Credential_revoked_in_this_instance_is_never_served_stale_during_an_outage()
    {
        var store = new MemoryCredentials();
        var time = new ManualTime();
        var service = new CredentialService(store, new InMemoryAuditSink(), time, TimeSpan.FromSeconds(30));
        var issued = await service.IssueAsync("org-a", "c", [Roles.Runtime], "admin");
        Assert.NotNull(await service.AuthenticateAsync(issued.Secret));

        Assert.True(await service.RevokeAsync("org-a", issued.Credential.Id, "admin"));
        store.Down = true;
        time.Now += TimeSpan.FromMinutes(1);

        // Sem entrada em cache (a revogação a removeu): nada a servir, o erro sobe.
        await Assert.ThrowsAsync<CredentialStoreUnavailableException>(() => service.AuthenticateAsync(issued.Secret));
    }

    [Fact]
    public async Task Revocation_in_this_instance_is_immediate()
    {
        var service = new CredentialService(new MemoryCredentials(), new InMemoryAuditSink());
        var issued = await service.IssueAsync("org-a", "c", [Roles.Admin], "admin");
        Assert.NotNull(await service.AuthenticateAsync(issued.Secret));

        Assert.True(await service.RevokeAsync("org-a", issued.Credential.Id, "admin"));

        Assert.Null(await service.AuthenticateAsync(issued.Secret));
        Assert.False(await service.RevokeAsync("org-b", issued.Credential.Id, "admin"));
    }

    // ------------------------------------------------------------------ captura diagnóstica

    [Fact]
    public void Masker_hides_sensitive_names_in_json_and_xml_and_keeps_the_rest()
    {
        var masker = new DiagnosticMasker(new DiagnosticCaptureOptions().SensitiveNamePattern);
        var json = masker.MaskJson(SnapshotFixtures.Json("""
            { "Numero": "000123", "CnpjPrestador": "12345678000199", "Tomador": { "Nome": "Fulano de Tal", "Email": "a@b.c" },
              "Contatos": [ { "Telefone": "11999998888" } ], "Valor": 10 }
            """));

        Assert.DoesNotContain("12345678000199", json);
        Assert.DoesNotContain("Fulano", json);
        Assert.DoesNotContain("11999998888", json);
        Assert.Contains("***99", json);
        Assert.Contains("000123", json);

        var xml = masker.MaskXml("<DPS xmlns=\"urn:x\"><numero>000123</numero><cnpjPrestador>12345678000199</cnpjPrestador><t cpf=\"12345678901\"/></DPS>")!;
        Assert.DoesNotContain("12345678000199", xml);
        Assert.DoesNotContain("12345678901", xml);
        Assert.Contains("000123", xml);
    }

    [Fact]
    public void Xml_masking_covers_descendants_of_sensitive_groups_and_national_layout_names()
    {
        var masker = new DiagnosticMasker(new DiagnosticCaptureOptions().SensitiveNamePattern);

        var xml = masker.MaskXml("<DPS><end><xLgr>Rua das Flores</xLgr><nro>1234</nro><xCpl>Apto 5</xCpl></end><IM>99887766</IM><vServ>10.00</vServ></DPS>")!;

        foreach (var secret in new[] { "Rua das Flores", "1234", "Apto 5", "99887766" })
        {
            Assert.DoesNotContain(secret, xml);
        }

        Assert.Contains("10.00", xml);
    }

    [Theory]
    [InlineData("abc-123:x.y_z", true)]
    [InlineData("cnpj 12345678000199", false)]
    [InlineData("", false)]
    public void Correlation_id_is_an_opaque_bounded_identifier(string value, bool valid)
    {
        if (valid)
        {
            Generation.XmlGenerationService.ValidateCorrelationId(value);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => Generation.XmlGenerationService.ValidateCorrelationId(value));
        }

        Assert.Throws<ArgumentException>(() => Generation.XmlGenerationService.ValidateCorrelationId(new string('a', 65)));
    }

    [Fact]
    public void Pattern_guard_cuts_catastrophic_backtracking_and_passes_normal_values()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.False(Engine.Validation.PatternGuard.CompletesInTime(new string('a', 40) + "b", [["(a|aa)+"]]));
        Assert.True(Engine.Validation.PatternGuard.CompletesInTime("12345678000199", [[@"\d{14}"]]));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Capture_is_off_by_default_needs_a_protected_store_and_has_limits()
    {
        var time = new ManualTime();
        var audit = new InMemoryAuditSink();
        var store = new MemoryDiagnostics();
        var capture = new DiagnosticCaptureService(store, audit, new DiagnosticCaptureOptions { MaxRecordsPerWindow = 2 }, time);
        var result = new GenerationResult(true, "<DPS><cnpjPrestador>12345678000199</cnpjPrestador></DPS>", [], TimeSpan.Zero);

        Assert.False(capture.ShouldCapture(Key));
        await capture.CaptureAsync(Key, SnapshotFixtures.DpsJson(), result);
        Assert.Empty(store.Records);

        await capture.EnableAsync(Key, TimeSpan.FromMinutes(10), "admin");
        for (var i = 0; i < 3; i++)
        {
            await capture.CaptureAsync(Key, SnapshotFixtures.DpsJson(), result);
        }

        Assert.Equal(2, store.Records.Count); // limite por janela
        Assert.All(store.Records, r => Assert.DoesNotContain("12345678000199", r.MaskedInputJson + r.MaskedXml));
        Assert.All(store.Records, r => Assert.Equal(time.Now + TimeSpan.FromHours(24), r.ExpiresAtUtc));

        time.Now += TimeSpan.FromMinutes(11);
        Assert.False(capture.ShouldCapture(Key)); // janela expirou

        await Assert.ThrowsAsync<ArgumentException>(() => capture.EnableAsync(Key, TimeSpan.FromDays(3), "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DiagnosticCaptureService(new MemoryDiagnostics(canProtect: false), audit).EnableAsync(Key, null, "admin"));
        Assert.Throws<ArgumentException>(() => new DiagnosticCaptureService(store, audit, new DiagnosticCaptureOptions { Retention = TimeSpan.FromDays(4) }));
        Assert.Contains(audit.Events, e => e.Action == "diagnostic.capture.enable");
    }

    [Fact]
    public async Task Capture_state_follows_other_instances_through_refresh()
    {
        var store = new MemoryDiagnostics();
        var here = new DiagnosticCaptureService(store, new InMemoryAuditSink());
        var elsewhere = new DiagnosticCaptureService(store, new InMemoryAuditSink());

        await elsewhere.EnableAsync(Key, TimeSpan.FromMinutes(5), "admin");
        Assert.False(here.ShouldCapture(Key));
        await here.RefreshAsync();
        Assert.True(here.ShouldCapture(Key));

        await elsewhere.DisableAsync(Key, "admin");
        await here.RefreshAsync();
        Assert.False(here.ShouldCapture(Key));
    }
}
