using System.Text;

namespace Tradux.Xsd.Tests;

/// <summary>Fase 8 (ADR-0011): facetas do tipo simples combinadas ao longo da derivação.</summary>
public class FacetReaderTests
{
    private const string Chain = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" targetNamespace="urn:t" elementFormDefault="qualified">
          <xs:simpleType name="Codigo">
            <xs:restriction base="xs:string">
              <xs:minLength value="2"/>
              <xs:maxLength value="10"/>
              <xs:pattern value="[A-Z]+\d*"/>
            </xs:restriction>
          </xs:simpleType>
          <xs:simpleType name="CodigoCurto">
            <xs:restriction base="t:Codigo">
              <xs:maxLength value="6"/>
              <xs:pattern value="AB.*"/>
              <xs:pattern value="CD.*"/>
            </xs:restriction>
          </xs:simpleType>
          <xs:simpleType name="Valor">
            <xs:restriction base="xs:decimal">
              <xs:minExclusive value="0"/>
              <xs:maxInclusive value="999.99"/>
              <xs:totalDigits value="5"/>
              <xs:fractionDigits value="2"/>
            </xs:restriction>
          </xs:simpleType>
          <xs:element name="R">
            <xs:complexType>
              <xs:sequence>
                <xs:element name="codigo" type="t:CodigoCurto"/>
                <xs:element name="valor" type="t:Valor"/>
                <xs:element name="uf">
                  <xs:simpleType>
                    <xs:restriction base="xs:string">
                      <xs:length value="2"/>
                      <xs:enumeration value="SP"/>
                      <xs:enumeration value="RJ"/>
                    </xs:restriction>
                  </xs:simpleType>
                </xs:element>
                <xs:element name="livre" type="xs:string"/>
              </xs:sequence>
            </xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static DestinationSchema Import(string xsd, string name = "t.xsd") =>
        XsdSchemaImporter.ImportInMemory(new Dictionary<string, byte[]> { [name] = Encoding.UTF8.GetBytes(xsd) }, name).Schema;

    [Fact]
    public void Lengths_take_the_most_restrictive_value_and_patterns_accumulate_per_step()
    {
        var codigo = Import(Chain).FindScalar("codigo")!.Facets;

        Assert.Equal(2, codigo.MinLength);
        Assert.Equal(6, codigo.MaxLength);
        Assert.Equal(2, codigo.PatternSteps.Count);
        Assert.Equal(["AB.*", "CD.*"], codigo.PatternSteps[0]);
        Assert.Equal([@"[A-Z]+\d*"], codigo.PatternSteps[1]);
    }

    [Fact]
    public void Numeric_bounds_and_digits_are_kept_as_invariant_text()
    {
        var valor = Import(Chain).FindScalar("valor")!.Facets;

        Assert.Equal("0", valor.MinExclusive);
        Assert.Equal("999.99", valor.MaxInclusive);
        Assert.Null(valor.MinInclusive);
        Assert.Equal((5, 2), (valor.TotalDigits, valor.FractionDigits));
    }

    [Fact]
    public void Enumeration_and_exact_length_come_from_the_anonymous_type()
    {
        var schema = Import(Chain);

        var uf = schema.FindScalar("uf")!.Facets;
        Assert.Equal(["SP", "RJ"], uf.Enumeration);
        Assert.Equal(2, uf.Length);
        Assert.Same(ScalarFacets.None, schema.FindScalar("livre")!.Facets);
    }

    [Fact]
    public void Reference_fixture_exposes_its_restrictions()
    {
        var schema = XsdFiles.Import(RepositoryPaths.Combine("docs", "schemas", "exemplo-simples.xsd")).Schema;

        Assert.Equal([[@"\d{14}"]], schema.FindScalar("cnpjPrestador")!.Facets.PatternSteps);
        Assert.Equal(5, schema.FindScalar("serie")!.Facets.MaxLength);
        Assert.Equal(["1", "2", "3"], schema.FindScalar("codigoSituacao")!.Facets.Enumeration.Take(3));
        Assert.Equal(("0", "999999999.99", 2), (schema.FindScalar("valorServico")!.Facets.MinInclusive,
            schema.FindScalar("valorServico")!.Facets.MaxInclusive, schema.FindScalar("valorServico")!.Facets.FractionDigits));
    }
}
