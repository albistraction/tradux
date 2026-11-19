using System.Text.Json;

namespace Tradux.Contracts;

/// <summary>
/// Pedido de geração de XML a partir de um snapshot publicado (seção 26,
/// docs/api/README.md §3), montado pela API a partir da rota e do corpo JSON.
/// <see cref="Data"/> precisa ser um objeto JSON cujos nomes de propriedade
/// são os caminhos que o mapeamento lê (sensíveis a maiúsculas/minúsculas).
/// </summary>
public sealed record GenerateXmlRequest(
    string OrganizationId,
    string TemplateId,
    string Operation,
    JsonElement Data)
{
    /// <summary>Versão de snapshot fixada. <c>null</c> = versão ativa publicada (padrão).</summary>
    public int? Version { get; init; }

    public string? CorrelationId { get; init; }

    /// <summary>
    /// Fallback (seção 17): versões candidatas em ordem de preferência; a
    /// primeira que gera XML válido é usada. Tem precedência sobre
    /// <see cref="Version"/>.
    /// </summary>
    public IReadOnlyList<int>? CandidateVersions { get; init; }
}
