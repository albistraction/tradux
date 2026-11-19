using System.IO.Compression;
using System.Text;
using Xunit;

namespace Tradux.Xsd.Tests;

/// <summary>
/// Prova as mitigações de segurança de importação exigidas pela seção 11 do
/// requisito de origem ("importados de forma determinística, segura e
/// reproduzível") no único caminho de importação — o conjunto em memória que
/// upload e snapshot usam: referência externa, path traversal, DOCTYPE/XXE e
/// limites de quantidade e tamanho.
/// </summary>
public class XsdImportSecurityTests
{
    private const string SchemaHeader =
        """<?xml version="1.0" encoding="UTF-8"?><xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:tns="urn:test" targetNamespace="urn:test" elementFormDefault="qualified">""";

    private const string IncludedType =
        """<xs:complexType name="TipoDoInclude"><xs:sequence><xs:element name="campo" type="xs:string"/></xs:sequence></xs:complexType>""";

    // O elemento raiz usa um tipo declarado SÓ no arquivo incluído
    // (tns:TipoDoInclude) — isso força o compilador de XSD a de fato resolver
    // o include. Um include cujo conteúdo não é referenciado por nada pode ser
    // silenciosamente ignorado pelo compilador, o que mascararia estes testes.
    private static string EntryIncluding(string schemaLocation) => $"""
        {SchemaHeader}
          <xs:include schemaLocation="{schemaLocation}"/>
          <xs:element name="Raiz" type="tns:TipoDoInclude"/>
        </xs:schema>
        """;

    private static Dictionary<string, byte[]> Files(params (string Name, string Content)[] files) =>
        files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Content), StringComparer.Ordinal);

    [Fact]
    public void Rejects_include_pointing_outside_the_set()
    {
        var files = Files(
            ("allowed/entrada.xsd", EntryIncluding("../../secreto.xsd")),
            ("allowed/vizinho.xsd", $"{SchemaHeader}{IncludedType}</xs:schema>"));

        var exception = Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(files, "allowed/entrada.xsd"));
        Assert.Contains("path traversal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://exemplo-malicioso.invalid/schema.xsd")]
    [InlineData("file:///c:/windows/win.ini")]
    public void Rejects_schema_location_outside_the_set(string location)
    {
        var files = Files(("entrada.xsd", EntryIncluding(location)));

        var exception = Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(files, "entrada.xsd"));
        Assert.Contains("fora do pacote", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_included_file_with_external_entity()
    {
        var files = Files(
            ("entrada.xsd", EntryIncluding("com-doctype.xsd")),
            ("com-doctype.xsd", "<?xml version=\"1.0\"?><!DOCTYPE xs:schema [<!ENTITY xxe SYSTEM \"file:///c:/windows/win.ini\">]>" +
                $"{SchemaHeader}{IncludedType}</xs:schema>"));

        var exception = Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(files, "entrada.xsd"));
        Assert.Contains("DOCTYPE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_file_larger_than_the_configured_limit()
    {
        var files = Files(("entrada.xsd", $"""{SchemaHeader}<xs:element name="Raiz" type="xs:string"/></xs:schema>"""));

        Assert.Throws<InvalidOperationException>(
            () => XsdSchemaImporter.ImportInMemory(files, "entrada.xsd", options: new XsdImportSecurityOptions(MaxFileBytes: 10)));
    }

    [Fact]
    public void Rejects_set_above_the_configured_total_size()
    {
        var files = Files(
            ("a.xsd", $"""{SchemaHeader}<xs:element name="A" type="xs:string"/></xs:schema>"""),
            ("b.xsd", $"""{SchemaHeader}<xs:element name="B" type="xs:string"/></xs:schema>"""));
        var limit = files.Values.Max(f => f.LongLength) + 10;

        Assert.Throws<InvalidOperationException>(
            () => XsdSchemaImporter.ImportInMemory(files, "a.xsd", options: new XsdImportSecurityOptions(MaxPackageTotalBytes: limit)));
    }

    [Fact]
    public void Composite_fixture_zip_gives_the_same_result_as_the_loose_files()
    {
        var fromZip = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = ZipFile.OpenRead(RepositoryPaths.Combine("docs", "schemas", "exemplo-composto-pacote.zip")))
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                fromZip[entry.FullName] = buffer.ToArray();
            }
        }

        var fromPackage = XsdSchemaImporter.ImportInMemory(fromZip, "exemplo-composto.xsd");
        var fromLooseFiles = XsdFiles.Import(RepositoryPaths.Combine("docs", "schemas", "exemplo-composto.xsd"));

        Assert.Equal(fromLooseFiles.Schema.RootElementName, fromPackage.Schema.RootElementName);
        Assert.Equal(fromLooseFiles.Schema.RootContent.Items.Count, fromPackage.Schema.RootContent.Items.Count);
        Assert.Equal(fromLooseFiles.ContentHash, fromPackage.ContentHash);
    }

    [Theory]
    [InlineData("../fora-do-pacote.xsd", "path traversal")]
    [InlineData("pasta/../../fora.xsd", "path traversal")]
    [InlineData("/absoluto.xsd", "caminho absoluto")]
    public void Rejects_file_name_with_path_traversal_or_absolute_path(string name, string reason)
    {
        var files = Files((name, $"""{SchemaHeader}<xs:element name="X" type="xs:string"/></xs:schema>"""));

        var exception = Assert.Throws<InvalidOperationException>(() => XsdSchemaImporter.ImportInMemory(files, name));
        Assert.Contains(reason, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_set_with_more_files_than_the_configured_limit()
    {
        var files = Files(Enumerable.Range(0, 5)
            .Select(i => ($"arquivo{i}.xsd", $"""{SchemaHeader}<xs:element name="X{i}" type="xs:string"/></xs:schema>""")).ToArray());

        Assert.Throws<InvalidOperationException>(
            () => XsdSchemaImporter.ImportInMemory(files, "arquivo0.xsd", options: new XsdImportSecurityOptions(MaxPackageEntryCount: 3)));
    }
}
