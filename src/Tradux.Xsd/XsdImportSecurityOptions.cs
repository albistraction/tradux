namespace Tradux.Xsd;

/// <summary>
/// Limites de segurança para importação de XSD/pacote (seção 11 do
/// requisito de origem): bloqueia arquivos excessivos e pacotes acima dos
/// limites configurados. Os defaults são conservadores o suficiente para
/// qualquer schema fiscal real, mas configuráveis por ambiente.
/// </summary>
public sealed record XsdImportSecurityOptions(
    long MaxFileBytes = 5 * 1024 * 1024,
    int MaxPackageEntryCount = 200,
    long MaxPackageTotalBytes = 20 * 1024 * 1024,
    int MaxPatternQuantifier = 1000,
    int MaxPatternLength = 1000,
    int MaxElementsPerContentModel = 1000,
    int MaxElementDeclarations = 10_000,
    int MaxContentModelParticlesTotal = 20_000,
    int MaxXmlDepth = 128,
    int MaxReferenceHeight = 256,
    int MaxAttributesPerType = 500,
    int MaxAttributeUsesTotal = 20_000,
    int MaxUnionMembers = 64,
    int MaxUnionMembersTotal = 20_000,
    int MaxPatternEvaluationsPerValue = 8,
    int MaxPatternEvaluationsPerTag = 8,
    int MaxWildcardNamespaces = 64,
    int MaxWildcardNamespacesTotal = 2_000,
    int MaxIdentityXPathLength = 1_000,
    int MaxIdentityXPathBranches = 32,
    int MaxIdentityFields = 32,
    int MaxIdentityConstraints = 16,
    int MaxIdentityCost = 64)
{
    public static XsdImportSecurityOptions Default { get; } = new();
}
