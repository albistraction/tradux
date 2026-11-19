using Tradux.Application.Diagnostics;
using Tradux.Application.Generation;

namespace Tradux.Api;

/// <summary>Opções de cache/invalidação (seção <c>Tradux:Cache</c>, ADR-0005).</summary>
public sealed class SnapshotCacheOptions
{
    public const string SectionName = "Tradux:Cache";

    /// <summary>Liga a invalidação por Change Streams e o poll de segurança. Desligar só faz sentido em teste.</summary>
    public bool InvalidationEnabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ChangeFeedRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Credencial inicial vinda de configuração: só o hash SHA-256 do segredo (ADR-0012 §5).</summary>
public sealed class BootstrapCredentialOptions
{
    public string OrganizationId { get; set; } = "";

    public string Name { get; set; } = "";

    public string SecretSha256 { get; set; } = "";

    public string[] Roles { get; set; } = [];
}

/// <summary>Seção <c>Tradux:Security</c>.</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Tradux:Security";

    public BootstrapCredentialOptions[] BootstrapCredentials { get; set; } = [];

    /// <summary>Validade do cache de credenciais em memória (revogação vale em até este tempo nas outras instâncias).</summary>
    public TimeSpan CredentialCacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Com o MongoDB fora, credencial já validada vale até este tempo além do cache (ADR-0014 §4).</summary>
    public TimeSpan CredentialStaleGrace { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>Seção <c>Tradux:Generation</c> (os limites de entrada ficam em <see cref="GenerationLimits"/>, <c>Tradux:Generation:Limits</c>).</summary>
public sealed class GenerationOptions
{
    public const string SectionName = "Tradux:Generation";

    /// <summary>Versões candidatas aceitas no fallback (cada uma pode ser compilada e ir para o cache).</summary>
    public int MaxCandidateVersions { get; set; } = XmlGenerationService.DefaultMaxCandidateVersions;

    /// <summary>Corpo máximo de <c>…/xml</c>, <c>…/transform</c> e <c>…/convert</c> (ADR-0014 §3): uma nota cabe com folga em 1 MiB.</summary>
    public long MaxRequestBytes { get; set; } = 1024L * 1024;
}

/// <summary>Seção <c>Tradux:Design</c>.</summary>
public sealed class DesignOptions
{
    public const string SectionName = "Tradux:Design";

    /// <summary>
    /// Corpo máximo das rotas que recebem XSD em Base64 (desenho, cadastro,
    /// publicação de conteúdo, camadas): o limite de pacote XSD (20 MB) + ~33%
    /// de Base64 + mapeamento e cenários.
    /// </summary>
    public long MaxRequestBytes { get; set; } = 28L * 1024 * 1024;
}

/// <summary>
/// Seção <c>Tradux:Cors</c>: o editor (Tradux.Web) é outra origem e só fala
/// HTTP com a API (CONTAINERS.md). Vazio = sem CORS.
/// </summary>
public sealed class EditorCorsOptions
{
    public const string SectionName = "Tradux:Cors";

    public string[] AllowedOrigins { get; set; } = [];
}

/// <summary>Chave de criptografia em repouso da captura diagnóstica (mesma seção de <see cref="DiagnosticCaptureOptions"/>).</summary>
public sealed class DiagnosticStoreOptions
{
    /// <summary>AES-256 em Base64 (32 bytes); vazia = captura indisponível.</summary>
    public string? EncryptionKey { get; set; }

    public byte[]? KeyBytes() => string.IsNullOrEmpty(EncryptionKey) ? null : Convert.FromBase64String(EncryptionKey);
}

internal static class ConfigurationExtensions
{
    public static T Section<T>(this IConfiguration configuration, string name) where T : new() =>
        configuration.GetSection(name).Get<T>() ?? new T();
}
