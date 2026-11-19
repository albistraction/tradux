using System.Collections.Concurrent;
using Tradux.Application.Ports;

namespace Tradux.Application.Security;

/// <summary>
/// Emissão, revogação e autenticação de credenciais (ADR-0012 §1, §5).
/// Emissão e revogação são mudanças de permissão: sempre auditadas.
/// A autenticação usa um cache em memória de validade curta: o caminho
/// aquecido de geração não consulta o MongoDB a cada requisição (seção 21);
/// revogação vale em até <see cref="CacheTtl"/> nas demais instâncias
/// (imediato na que revogou) — ou até <see cref="StaleGrace"/> numa instância
/// que perdeu o acesso ao armazenamento (ADR-0014 §4).
/// </summary>
public sealed class CredentialService
{
    private readonly IApiCredentialStore _store;
    private readonly AuditTrail _audit;
    private readonly TimeProvider _time;
    // StaleUntil = última validação bem-sucedida + validade + carência (ADR-0014 §4).
    private readonly ConcurrentDictionary<string, (AccessPrincipal? Principal, string? CredentialId, DateTimeOffset ExpiresAt, DateTimeOffset StaleUntil)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _unknown = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _revokedHere = new(StringComparer.Ordinal);

    public CredentialService(IApiCredentialStore store, IAuditSink audit, TimeProvider? time = null, TimeSpan? cacheTtl = null, TimeSpan? staleGrace = null)
    {
        _store = store;
        _time = time ?? TimeProvider.System;
        _audit = new AuditTrail(audit, _time);
        CacheTtl = cacheTtl ?? TimeSpan.FromSeconds(30);
        StaleGrace = staleGrace ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Com o armazenamento de credenciais indisponível
    /// (<see cref="CredentialStoreUnavailableException"/>; qualquer outro erro
    /// sobe), uma credencial já validada continua aceita até este tempo além
    /// do vencimento do cache (ADR-0014 §4). Numa queda total ninguém consegue
    /// revogar; numa partição (só esta instância sem acesso ao banco) uma
    /// revogação feita em outra instância leva até validade + carência para
    /// valer aqui — risco aceito e documentado.
    /// </summary>
    public TimeSpan StaleGrace { get; }

    /// <summary>Validade do cache de credencial válida (a desconhecida fica 1/6 disso, para não martelar o banco).</summary>
    public TimeSpan CacheTtl { get; }

    private const int MaxCacheEntries = 10_000;

    /// <exception cref="ArgumentException">Organização/nome vazios, sem papel ou papel desconhecido.</exception>
    public async Task<IssuedCredential> IssueAsync(string organizationId, string name, IReadOnlyList<string> roles, string actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(organizationId) || string.IsNullOrWhiteSpace(name) || roles.Count == 0)
        {
            throw new ArgumentException("Organização, nome e ao menos um papel são obrigatórios.");
        }

        _ = Roles.Expand(roles); // valida papéis antes de gravar
        var secret = CredentialSecrets.NewSecret();
        var credential = new ApiCredential
        {
            Id = Guid.NewGuid().ToString("N"),
            OrganizationId = organizationId,
            Name = name,
            SecretHash = CredentialSecrets.Hash(secret),
            Roles = roles.Distinct(StringComparer.Ordinal).ToList(),
            CreatedAtUtc = _time.GetUtcNow(),
            CreatedBy = actor,
        };
        await _store.AddAsync(credential, cancellationToken).ConfigureAwait(false);
        await AuditAsync(organizationId, actor, "permission.grant", credential, cancellationToken).ConfigureAwait(false);
        return new IssuedCredential(credential, secret);
    }

    public async Task<bool> RevokeAsync(string organizationId, string credentialId, string actor, CancellationToken cancellationToken = default)
    {
        var revoked = await _store.RevokeAsync(organizationId, credentialId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (revoked)
        {
            // Marca antes de limpar: uma autenticação em andamento não recoloca o
            // principal revogado no cache (revisão da Fase 9, W6).
            _revokedHere[credentialId] = 0;
            foreach (var entry in _cache.Where(e => e.Value.CredentialId == credentialId).ToList())
            {
                _cache.TryRemove(entry.Key, out _);
            }

            await _audit.RecordAsync(organizationId, actor, "permission.revoke", $"credential:{credentialId}", AuditTrail.Success, [], cancellationToken).ConfigureAwait(false);
        }

        return revoked;
    }

    /// <summary>Credencial inicial vinda de configuração (só o hash; o segredo nunca passa pela configuração).</summary>
    public async Task EnsureBootstrapAsync(string organizationId, string name, string secretHash, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
    {
        _ = Roles.Expand(roles);
        await _store.EnsureAsync(new ApiCredential
        {
            Id = "bootstrap-" + secretHash[..16],
            OrganizationId = organizationId,
            Name = name,
            SecretHash = secretHash.ToLowerInvariant(),
            Roles = roles,
            CreatedAtUtc = _time.GetUtcNow(),
            CreatedBy = "configuração",
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Principal da credencial ativa com este segredo; nulo se não existe ou foi revogada.</summary>
    public async Task<AccessPrincipal?> AuthenticateAsync(string secret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length > 256)
        {
            return null;
        }

        var hash = CredentialSecrets.Hash(secret);
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(hash, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Principal;
        }

        if (_unknown.TryGetValue(hash, out var unknownUntil) && unknownUntil > now)
        {
            return null;
        }

        ApiCredential? credential;
        try
        {
            credential = await _store.FindBySecretHashAsync(hash, cancellationToken).ConfigureAwait(false);
        }
        catch (CredentialStoreUnavailableException) when (!cancellationToken.IsCancellationRequested
            && cached.Principal is not null && cached.StaleUntil > now && !_revokedHere.ContainsKey(cached.CredentialId!))
        {
            // Armazenamento fora: mantém a credencial já validada (stale-if-error) e só
            // tenta o armazenamento de novo após outra validade — sem isso, todo pedido
            // pagaria o timeout de seleção de servidor. A carência total não cresce.
            var retryAt = now + CacheTtl < cached.StaleUntil ? now + CacheTtl : cached.StaleUntil;
            _cache[hash] = cached with { ExpiresAt = retryAt };
            if (_revokedHere.ContainsKey(cached.CredentialId!))
            {
                _cache.TryRemove(hash, out _); // revogada no meio: não fica em cache
                return null;
            }

            return cached.Principal;
        }

        if (credential is not { Active: true } || _revokedHere.ContainsKey(credential.Id))
        {
            // Cache negativo separado: segredos aleatórios em massa não derrubam
            // as credenciais válidas em cache (revisão W6). Sem limite de taxa
            // aqui — isso é papel do gateway/infra (Fase 11).
            if (_unknown.Count >= MaxCacheEntries)
            {
                _unknown.Clear();
            }

            _unknown[hash] = now + TimeSpan.FromTicks(CacheTtl.Ticks / 6);
            return null;
        }

        var principal = credential.ToPrincipal();
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[hash] = (principal, credential.Id, now + CacheTtl, now + CacheTtl + StaleGrace);

        // Revogação que chegou durante a consulta: desfaz o cache (2ª revisão, A4).
        if (_revokedHere.ContainsKey(credential.Id))
        {
            _cache.TryRemove(hash, out _);
            return null;
        }

        return principal;
    }

    public Task<IReadOnlyList<ApiCredential>> ListAsync(string organizationId, CancellationToken cancellationToken = default) =>
        _store.ListAsync(organizationId, cancellationToken);

    private Task AuditAsync(string organizationId, string actor, string action, ApiCredential credential, CancellationToken cancellationToken) =>
        _audit.RecordAsync(organizationId, actor, action, $"credential:{credential.Id}", AuditTrail.Success,
            new() { ["name"] = credential.Name, ["roles"] = string.Join(",", credential.Roles) }, cancellationToken);
}
