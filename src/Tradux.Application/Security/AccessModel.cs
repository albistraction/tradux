using System.Security.Cryptography;
using System.Text;

namespace Tradux.Application.Security;

/// <summary>Permissões por ação da seção 20 (ADR-0012 §2). Toda rota exige uma delas — nunca "é admin".</summary>
public static class Permissions
{
    public const string TemplateRead = "template.read";
    public const string TemplateCreate = "template.create";
    public const string TemplateEdit = "template.edit";
    public const string TemplateTest = "template.test";
    public const string TemplatePublish = "template.publish";
    public const string TemplateUse = "template.use";
    public const string TemplateArchive = "template.archive";
    public const string TemplateImport = "template.import";
    public const string TemplateExport = "template.export";
    public const string DiagnosticCapture = "diagnostic.capture";
    public const string AuditRead = "audit.read";

    public static IReadOnlyList<string> All { get; } =
    [
        TemplateRead, TemplateCreate, TemplateEdit, TemplateTest, TemplatePublish, TemplateUse,
        TemplateArchive, TemplateImport, TemplateExport, DiagnosticCapture, AuditRead,
    ];
}

/// <summary>Papéis = conjuntos nomeados de permissões (ADR-0012 §2).</summary>
public static class Roles
{
    public const string Admin = "admin";
    public const string Runtime = "runtime";
    public const string Editor = "editor";
    public const string Auditor = "auditor";

    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> Definitions { get; } = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
    {
        [Admin] = Permissions.All.ToHashSet(StringComparer.Ordinal),
        [Runtime] = new HashSet<string>(StringComparer.Ordinal) { Permissions.TemplateUse },
        [Editor] = new HashSet<string>(StringComparer.Ordinal) { Permissions.TemplateRead, Permissions.TemplateCreate, Permissions.TemplateEdit, Permissions.TemplateTest },
        [Auditor] = new HashSet<string>(StringComparer.Ordinal) { Permissions.TemplateRead, Permissions.AuditRead },
    };

    /// <summary>Permissões efetivas de um conjunto de papéis.</summary>
    /// <exception cref="ArgumentException">Papel desconhecido.</exception>
    public static IReadOnlyCollection<string> Expand(IEnumerable<string> roles)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            permissions.UnionWith(Definitions.TryGetValue(role, out var set) ? set : throw new ArgumentException($"Papel desconhecido: '{role}'.", nameof(roles)));
        }

        return permissions;
    }
}

/// <summary>Quem está chamando: organização, identificação para auditoria e permissões efetivas.</summary>
public sealed record AccessPrincipal(string OrganizationId, string Actor, IReadOnlyCollection<string> Permissions)
{
    public bool Can(string permission) => Permissions.Contains(permission);
}

/// <summary>
/// Credencial persistida: só o hash do segredo (ADR-0012 §1). O segredo em
/// claro existe apenas no retorno da emissão.
/// </summary>
public sealed record ApiCredential
{
    public required string Id { get; init; }
    public required string OrganizationId { get; init; }
    public required string Name { get; init; }
    public required string SecretHash { get; init; }
    public required IReadOnlyList<string> Roles { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }

    public bool Active => RevokedAtUtc is null;

    /// <summary>Ator único na auditoria: nome legível + prefixo do id (nomes podem se repetir).</summary>
    public AccessPrincipal ToPrincipal() => new(OrganizationId, $"credential:{Name}#{Id[..Math.Min(8, Id.Length)]}", Security.Roles.Expand(Roles));
}

/// <summary>Resultado da emissão: o segredo aparece aqui e em nenhum outro lugar.</summary>
public sealed record IssuedCredential(ApiCredential Credential, string Secret);

/// <summary>
/// Armazenamento de credenciais indisponível (rede, eleição, sem servidor
/// elegível). Só esta falha libera a carência de credencial em cache
/// (ADR-0014 §4) — qualquer outro erro sobe normalmente.
/// </summary>
public sealed class CredentialStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Porta de armazenamento de credenciais (índice único no hash).</summary>
public interface IApiCredentialStore
{
    Task<ApiCredential?> FindBySecretHashAsync(string secretHash, CancellationToken cancellationToken = default);

    Task AddAsync(ApiCredential credential, CancellationToken cancellationToken = default);

    /// <summary>Insere se não existir credencial com o mesmo hash (credenciais iniciais da configuração).</summary>
    Task EnsureAsync(ApiCredential credential, CancellationToken cancellationToken = default);

    Task<bool> RevokeAsync(string organizationId, string credentialId, DateTimeOffset revokedAtUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApiCredential>> ListAsync(string organizationId, CancellationToken cancellationToken = default);
}

/// <summary>Hash e geração de segredos de credencial.</summary>
public static class CredentialSecrets
{
    public const string Prefix = "tradux_";

    public static string NewSecret() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 hexadecimal minúsculo — o segredo tem 256 bits de entropia, não precisa de KDF lento.</summary>
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
