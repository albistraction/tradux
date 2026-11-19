namespace Tradux.Application;

/// <summary>
/// Chave lógica de um snapshot publicado (ADR-0005:
/// <c>{organizationId}:{templateId}:{operation}</c>). Toda leitura de
/// snapshot passa por esta chave, e ela sempre inclui a organização — o
/// isolamento multiempresa (seção 20) não depende de um filtro opcional.
/// </summary>
public sealed record TemplateKey
{
    public TemplateKey(string organizationId, string templateId, string operation)
    {
        OrganizationId = RequireNonBlank(organizationId, nameof(organizationId));
        TemplateId = RequireNonBlank(templateId, nameof(templateId));
        Operation = RequireNonBlank(operation, nameof(operation));
    }

    public string OrganizationId { get; }
    public string TemplateId { get; }
    public string Operation { get; }

    public override string ToString() => $"{OrganizationId}:{TemplateId}:{Operation}";

    private static string RequireNonBlank(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"'{name}' é obrigatório e não pode ser vazio.", name)
            : value;
}
