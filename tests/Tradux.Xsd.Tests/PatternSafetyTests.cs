using System.Text;
using System.Xml.Schema;
using Tradux.Xsd.Security;

namespace Tradux.Xsd.Tests;

/// <summary>Fase 9 (ADR-0012 §9): patterns que tornariam a validação XSD cara demais são recusados na importação.</summary>
public class PatternSafetyTests
{
    [Theory]
    [InlineData(@"\d{14}")]
    [InlineData(@"\d{2}\.\d{2}\.\d{2}")]
    [InlineData(@"[A-Z]{2}\d{0,3}")]
    [InlineData(@"(\d{3}\.){2}\d{3}")]
    [InlineData(@"[^()*+]+")]
    [InlineData(@"(AB|CD)*")]
    [InlineData(@"\(\d+\)*")]
    public void Usual_fiscal_patterns_are_accepted(string pattern)
    {
        Assert.Null(PatternSafetyChecker.Problem(pattern, XsdImportSecurityOptions.Default));
    }

    [Theory]
    [InlineData("(a?){2000000000}", "acima")]
    [InlineData(@"\d{1001}", "acima")]
    [InlineData("(a*)*b", "exponencial")]
    [InlineData("(a+)+", "exponencial")]
    [InlineData(@"(\d{1,3})*", "exponencial")]
    [InlineData(@"((ab)+)+", "exponencial")]
    [InlineData("(x{2,})+y", "exponencial")]
    [InlineData("(a*){1,1000}", "exponencial")]
    [InlineData("(a?){1000}a{1000}", "exponencial")]
    [InlineData("(.*a){20}", "exponencial")]
    [InlineData(@"(\d{1,3}\.){3}", "exponencial")]
    public void Dangerous_patterns_are_refused(string pattern, string reason)
    {
        Assert.Contains(reason, PatternSafetyChecker.Problem(pattern, XsdImportSecurityOptions.Default));
    }

    [Fact]
    public void Import_refuses_a_dangerous_pattern_in_an_included_file()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["main.xsd"] = Encoding.UTF8.GetBytes("""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" targetNamespace="urn:t" elementFormDefault="qualified">
                  <xs:include schemaLocation="tipos.xsd"/>
                  <xs:element name="R" type="t:Ruim"/>
                </xs:schema>
                """),
            ["tipos.xsd"] = Encoding.UTF8.GetBytes("""
                <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t">
                  <xs:simpleType name="Ruim"><xs:restriction base="xs:string"><xs:pattern value="(a+)+b"/></xs:restriction></xs:simpleType>
                </xs:schema>
                """),
        };

        var ex = Assert.Throws<XmlSchemaException>(() => XsdSchemaImporter.ImportInMemory(files, "main.xsd"));
        Assert.Contains("exponencial", ex.Message);
    }

    [Fact]
    public void Reference_fixtures_still_import()
    {
        foreach (var file in new[] { "exemplo-simples.xsd", "exemplo-colecao.xsd", "exemplo-composto.xsd" })
        {
            Assert.NotNull(XsdFiles.Import(RepositoryPaths.Combine("docs", "schemas", file)));
        }
    }
}
