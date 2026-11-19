using Tradux.Domain.Schema;
using Xunit;

namespace Tradux.Xsd.Tests;

public class XsdSchemaImporterTests
{
    private static string FixturePath => RepositoryPaths.Combine("docs", "schemas", "exemplo-simples.xsd");

    [Fact]
    public void Import_resolves_root_element_and_namespace()
    {
        var imported = XsdFiles.Import(FixturePath);

        Assert.Equal("DPS", imported.Schema.RootElementName);
        Assert.Equal("http://exemplo.ilustrativo.tradux/dps/v1", imported.Schema.TargetNamespace);
    }

    [Fact]
    public void Import_preserves_schema_order_of_elements()
    {
        var imported = XsdFiles.Import(FixturePath);

        var expectedOrder = new[]
        {
            "numero", "serie", "dataEmissao", "competencia", "valorServico",
            "valorDesconto", "codigoServico", "cnpjPrestador", "cnpjTomador",
            "discriminacaoServico", "codigoSituacao",
        };

        Assert.Equal(ParticleKind.Sequence, imported.Schema.RootContent.Kind);
        Assert.Equal(expectedOrder, imported.Schema.RootScalars().Select(e => e.Name));
    }

    [Fact]
    public void Import_marks_numero_as_required_and_not_nillable()
    {
        var imported = XsdFiles.Import(FixturePath);

        var numero = imported.Schema.FindScalar("numero");

        Assert.NotNull(numero);
        Assert.True(numero!.Cardinality.IsRequired);
        Assert.Equal(Nillability.NotNillable, numero.Nillability);
    }

    [Fact]
    public void Import_marks_serie_as_optional()
    {
        var imported = XsdFiles.Import(FixturePath);

        var serie = imported.Schema.FindScalar("serie");

        Assert.NotNull(serie);
        Assert.False(serie!.Cardinality.IsRequired);
    }

    [Fact]
    public void Import_marks_valorDesconto_as_required_and_nillable()
    {
        // Caso de borda proposital da fixture: obrigatório E aceita nulo ao
        // mesmo tempo — opcional != aceita nulo (docs/GLOSSARY.md).
        var imported = XsdFiles.Import(FixturePath);

        var valorDesconto = imported.Schema.FindScalar("valorDesconto");

        Assert.NotNull(valorDesconto);
        Assert.True(valorDesconto!.Cardinality.IsRequired);
        Assert.Equal(Nillability.Nillable, valorDesconto.Nillability);
    }

    [Fact]
    public void Import_reads_default_value_of_codigoSituacao()
    {
        var imported = XsdFiles.Import(FixturePath);

        var codigoSituacao = imported.Schema.FindScalar("codigoSituacao");

        Assert.NotNull(codigoSituacao);
        Assert.Equal("1", codigoSituacao!.DefaultValue);
    }

    [Fact]
    public void Import_reads_fixed_attribute_versao()
    {
        var imported = XsdFiles.Import(FixturePath);

        var versao = imported.Schema.Attributes.Single(a => a.Name == "versao");

        Assert.Equal("1.00", versao.FixedValue);
    }

    [Fact]
    public void Import_reads_attribute_id_without_fixed_value()
    {
        var imported = XsdFiles.Import(FixturePath);

        var id = imported.Schema.Attributes.Single(a => a.Name == "id");

        Assert.Null(id.FixedValue);
    }

    [Fact]
    public void Import_resolves_scalar_types_through_named_restrictions()
    {
        var imported = XsdFiles.Import(FixturePath);

        Assert.Equal(SchemaScalarType.String, imported.Schema.FindScalar("numero")!.ScalarType);
        Assert.Equal(SchemaScalarType.Date, imported.Schema.FindScalar("dataEmissao")!.ScalarType);
        Assert.Equal(SchemaScalarType.YearMonth, imported.Schema.FindScalar("competencia")!.ScalarType);
        Assert.Equal(SchemaScalarType.Decimal, imported.Schema.FindScalar("valorServico")!.ScalarType);
        Assert.Equal(SchemaScalarType.String, imported.Schema.FindScalar("codigoServico")!.ScalarType);
    }
}
