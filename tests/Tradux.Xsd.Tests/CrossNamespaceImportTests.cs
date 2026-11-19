using Xunit;

namespace Tradux.Xsd.Tests;

/// <summary>
/// Cobre xs:import entre namespaces diferentes — gap deixado registrado na
/// análise de exemplo-composto.ANALISE.md (que só exercita xs:include,
/// mesmo namespace).
/// </summary>
public class CrossNamespaceImportTests
{
    private static string FixturePath => RepositoryPaths.Combine("docs", "schemas", "exemplo-import-ns-b.xsd");

    [Fact]
    public void Import_resolves_xs_import_across_different_namespaces()
    {
        var imported = XsdFiles.Import(FixturePath);

        Assert.Equal("DocumentoComImport", imported.Schema.RootElementName);
        Assert.Equal("http://exemplo.ilustrativo.tradux/import-b/v1", imported.Schema.TargetNamespace);
    }

    [Fact]
    public void Imported_complex_type_from_the_other_namespace_is_resolved_with_its_own_fields()
    {
        var imported = XsdFiles.Import(FixturePath);
        var endereco = imported.Schema.RootContent.Items
            .OfType<ComplexElementItem>()
            .Single(i => i.Name == "endereco");

        Assert.True(endereco.Cardinality.IsRequired);
        Assert.Equal(ParticleKind.Sequence, endereco.Content.Kind);

        var fieldNames = endereco.Content.Items
            .OfType<ScalarElementItem>()
            .Select(i => i.Element.Name)
            .ToArray();
        Assert.Equal(["logradouro", "codigoMunicipio"], fieldNames);
    }
}
