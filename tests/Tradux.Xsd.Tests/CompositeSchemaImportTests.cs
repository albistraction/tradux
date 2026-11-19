using Tradux.Domain.Schema;
using Xunit;

namespace Tradux.Xsd.Tests;

/// <summary>
/// Prova o gate da Fase 3 (docs/ROADMAP.md) para o caminho feliz: um pacote
/// complexo (xs:include, xs:choice, coleção repetível, tipos aninhados) é
/// importado de forma determinística contra
/// docs/schemas/exemplo-composto.xsd (+ exemplo-composto-tipos.xsd).
/// </summary>
public class CompositeSchemaImportTests
{
    private static string FixturePath => RepositoryPaths.Combine("docs", "schemas", "exemplo-composto.xsd");

    [Fact]
    public void Import_resolves_include_from_the_same_directory()
    {
        var imported = XsdFiles.Import(FixturePath);

        Assert.Equal("DpsComposta", imported.Schema.RootElementName);
        Assert.Equal("http://exemplo.ilustrativo.tradux/dps-composto/v1", imported.Schema.TargetNamespace);
    }

    [Fact]
    public void Root_content_is_a_sequence_with_eight_items_in_schema_order()
    {
        var imported = XsdFiles.Import(FixturePath);
        var root = imported.Schema.RootContent;

        Assert.Equal(ParticleKind.Sequence, root.Kind);
        Assert.Equal(8, root.Items.Count);

        var namesInOrder = root.Items.Select(GetItemName).ToArray();
        Assert.Equal(
            ["numero", "dataEmissao", "tomador", "itens", "valorTotal", "valorDeducao", "dataCancelamento", "observacao"],
            namesInOrder);
    }

    [Fact]
    public void Tomador_is_a_complex_element_wrapping_a_real_choice()
    {
        var imported = XsdFiles.Import(FixturePath);
        var tomador = FindComplexItem(imported.Schema.RootContent, "tomador");

        Assert.True(tomador.Cardinality.IsRequired);
        Assert.Equal(Nillability.NotNillable, tomador.Nillability);
        Assert.Equal(ParticleKind.Choice, tomador.Content.Kind);
        Assert.Equal(2, tomador.Content.Items.Count);

        var branchNames = tomador.Content.Items.Select(GetItemName).ToArray();
        Assert.Equal(["tomadorPorDocumento", "tomadorPorCadastroMunicipal"], branchNames);
    }

    [Fact]
    public void Choice_branch_tomadorPorDocumento_has_required_and_optional_scalar_fields()
    {
        var imported = XsdFiles.Import(FixturePath);
        var tomador = FindComplexItem(imported.Schema.RootContent, "tomador");
        var branch = FindComplexItem(tomador.Content, "tomadorPorDocumento");

        Assert.Equal(ParticleKind.Sequence, branch.Content.Kind);
        var cpfCnpj = FindScalarItem(branch.Content, "cpfCnpj");
        var razaoSocial = FindScalarItem(branch.Content, "razaoSocial");

        Assert.True(cpfCnpj.Cardinality.IsRequired);
        Assert.False(razaoSocial.Cardinality.IsRequired);
    }

    [Fact]
    public void Choice_branch_tomadorPorCadastroMunicipal_has_two_required_fields()
    {
        var imported = XsdFiles.Import(FixturePath);
        var tomador = FindComplexItem(imported.Schema.RootContent, "tomador");
        var branch = FindComplexItem(tomador.Content, "tomadorPorCadastroMunicipal");

        Assert.True(FindScalarItem(branch.Content, "inscricaoMunicipal").Cardinality.IsRequired);
        Assert.True(FindScalarItem(branch.Content, "codigoMunicipio").Cardinality.IsRequired);
    }

    [Fact]
    public void Itens_envelope_wraps_a_true_repeatable_collection_from_an_included_named_type()
    {
        var imported = XsdFiles.Import(FixturePath);
        var itens = FindComplexItem(imported.Schema.RootContent, "itens");

        Assert.Equal(ParticleKind.Sequence, itens.Content.Kind);
        var item = FindComplexItem(itens.Content, "item");

        // minOccurs=0, maxOccurs=unbounded — coleção repetível verdadeira,
        // definida em exemplo-composto-tipos.xsd (ItemServicoType) e
        // resolvida via xs:include.
        Assert.Equal(0, item.Cardinality.MinOccurs);
        Assert.Null(item.Cardinality.MaxOccurs);
        Assert.True(item.Cardinality.IsRepeatable);

        Assert.Equal(ParticleKind.Sequence, item.Content.Kind);
        Assert.True(FindScalarItem(item.Content, "descricao").Cardinality.IsRequired);
        Assert.True(FindScalarItem(item.Content, "valorUnitario").Cardinality.IsRequired);

        var quantidade = FindScalarItem(item.Content, "quantidade");
        Assert.False(quantidade.Cardinality.IsRequired);
        Assert.Equal("1", quantidade.DefaultValue);
    }

    [Fact]
    public void Required_nillable_and_optional_not_nillable_are_still_distinct_at_the_composite_level()
    {
        var imported = XsdFiles.Import(FixturePath);
        var valorDeducao = FindScalarItem(imported.Schema.RootContent, "valorDeducao");
        var dataCancelamento = FindScalarItem(imported.Schema.RootContent, "dataCancelamento");

        Assert.True(valorDeducao.Cardinality.IsRequired);
        Assert.Equal(Nillability.Nillable, valorDeducao.Nillability);

        Assert.False(dataCancelamento.Cardinality.IsRequired);
        Assert.Equal(Nillability.NotNillable, dataCancelamento.Nillability);
    }

    [Fact]
    public void Root_attribute_versao_is_fixed()
    {
        var imported = XsdFiles.Import(FixturePath);
        var versao = imported.Schema.Attributes.Single(a => a.Name == "versao");

        Assert.Equal("1.00", versao.FixedValue);
    }

    [Fact]
    public void Import_is_deterministic_content_hash_matches_across_repeated_imports()
    {
        var first = XsdFiles.Import(FixturePath);
        var second = XsdFiles.Import(FixturePath);

        Assert.NotEmpty(first.ContentHash);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void Content_hash_changes_when_the_included_file_changes()
    {
        // O hash cobre o CONJUNTO (entrada + include), não só o arquivo de
        // entrada — prova isso comparando com o hash do fixture simples
        // (Fase 2), que não inclui nada.
        var composite = XsdFiles.Import(FixturePath);
        var simple = XsdFiles.Import(RepositoryPaths.Combine("docs", "schemas", "exemplo-simples.xsd"));

        Assert.NotEqual(composite.ContentHash, simple.ContentHash);
    }

    private static string GetItemName(SchemaParticleItem item) => item switch
    {
        ScalarElementItem scalar => scalar.Element.Name,
        ComplexElementItem complex => complex.Name,
        GroupItem => throw new InvalidOperationException("Grupo aninhado sem nome próprio."),
        _ => throw new InvalidOperationException($"Tipo de item inesperado: {item.GetType().Name}"),
    };

    private static ComplexElementItem FindComplexItem(SchemaParticle particle, string name) =>
        particle.Items.OfType<ComplexElementItem>().Single(i => i.Name == name);

    private static SchemaElement FindScalarItem(SchemaParticle particle, string name) =>
        particle.Items.OfType<ScalarElementItem>().Single(i => i.Element.Name == name).Element;
}
