using System.Text;
using Tradux.Application.Design;
using Tradux.Contracts.Design;
using Tradux.Domain.Mapping;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>Fase 14 (ADR-0017): nova versão do XSD de destino → o que mudou e o que remapear.</summary>
public class SchemaChangeTests
{
    private const string V1 = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:pref" xmlns="urn:pref" elementFormDefault="qualified">
          <xs:simpleType name="TStr60"><xs:restriction base="xs:string"><xs:maxLength value="60"/></xs:restriction></xs:simpleType>
          <xs:element name="Nota">
            <xs:complexType><xs:sequence>
              <xs:element name="Numero" type="xs:string"/>
              <xs:element name="Data" type="xs:date"/>
              <xs:element name="Tomador"><xs:complexType><xs:sequence>
                <xs:element name="Cnpj" type="xs:string"/>
                <xs:element name="Razao" type="TStr60"/>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Servico"><xs:complexType><xs:sequence>
                <xs:element name="Descricao" type="xs:string"/>
                <xs:element name="Valor" type="xs:string"/>
                <xs:element name="Aliquota" type="xs:decimal" minOccurs="0"/>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Obs" type="xs:string" minOccurs="0"/>
              <xs:element name="Item" minOccurs="0"><xs:complexType><xs:sequence>
                <xs:element name="Cod" type="xs:string"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    // Data antes de Numero; Razao → RazaoSocial; Cnpj com pattern; Valor decimal; Aliquota obrigatória;
    // Descricao foi para a raiz; Obs saiu; Item repete; campo obrigatório e bloco opcional novos.
    private const string V2 = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:pref" xmlns="urn:pref" elementFormDefault="qualified">
          <xs:simpleType name="TStr60"><xs:restriction base="xs:string"><xs:maxLength value="60"/></xs:restriction></xs:simpleType>
          <xs:simpleType name="TCnpj"><xs:restriction base="xs:string"><xs:pattern value="[0-9]{14}"/></xs:restriction></xs:simpleType>
          <xs:element name="Nota">
            <xs:complexType><xs:sequence>
              <xs:element name="Data" type="xs:date"/>
              <xs:element name="Numero" type="xs:string"/>
              <xs:element name="CodigoMunicipio" type="xs:string"/>
              <xs:element name="Descricao" type="xs:string"/>
              <xs:element name="Tomador"><xs:complexType><xs:sequence>
                <xs:element name="Cnpj" type="TCnpj"/>
                <xs:element name="RazaoSocial" type="TStr60"/>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Servico"><xs:complexType><xs:sequence>
                <xs:element name="Valor" type="xs:decimal"/>
                <xs:element name="Aliquota" type="xs:decimal"/>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Item" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="Cod" type="xs:string"/>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Intermediario" minOccurs="0"><xs:complexType><xs:sequence>
                <xs:element name="Cpf" type="xs:string"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static FieldMapping Field(string name) => new(name, new LiteralNode("x"));

    private static readonly MappingTemplate Mapping = new()
    {
        Fields = [Field("Numero"), Field("Data")],
        Complex =
        [
            new ComplexMapping { DestinationElementName = "Tomador", Content = new MappingTemplate { Fields = [Field("Cnpj"), Field("Razao")] } },
            new ComplexMapping { DestinationElementName = "Servico", Content = new MappingTemplate { Fields = [Field("Descricao"), Field("Valor")] } },
            new ComplexMapping { DestinationElementName = "Item", Content = new MappingTemplate { Fields = [Field("Cod")] } },
        ],
    };

    private static SchemaChangeReport Report() => new DraftDesignService().CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(V1), SnapshotFixtures.Upload(V2), Mapping));

    private static SchemaChange Single(SchemaChangeReport report, string path, SchemaChangeKind kind) =>
        Assert.Single(report.Changes, c => c.Path == path && c.Kind == kind);

    [Fact]
    public void Renamed_mapped_field_must_be_remapped_with_a_suggestion()
    {
        var change = Single(Report(), "/Nota/Tomador/Razao", SchemaChangeKind.Removed);

        Assert.Equal(SchemaChangeImpact.Remap, change.Impact);
        Assert.True(change.Mapped);
        Assert.Equal("/Nota/Tomador/RazaoSocial", change.Suggestions![0]);
    }

    [Fact]
    public void Mapped_field_moved_to_another_block_is_detected_as_moved()
    {
        var change = Single(Report(), "/Nota/Descricao", SchemaChangeKind.Moved);

        Assert.Equal(SchemaChangeImpact.Remap, change.Impact);
        Assert.Equal("/Nota/Servico/Descricao", change.BeforePath);
        Assert.Equal(["/Nota/Descricao"], change.Suggestions);
    }

    [Fact]
    public void Tag_sequence_change_is_informative_only()
    {
        var change = Single(Report(), "/Nota", SchemaChangeKind.OrderChanged);

        Assert.Equal(SchemaChangeImpact.Info, change.Impact);
        Assert.StartsWith("Numero, Data", change.Before, StringComparison.Ordinal);
        Assert.StartsWith("Data, Numero", change.After, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_and_restriction_changes_on_mapped_fields_ask_for_review()
    {
        var report = Report();

        var type = Single(report, "/Nota/Servico/Valor", SchemaChangeKind.TypeChanged);
        Assert.Equal(SchemaChangeImpact.Review, type.Impact);
        Assert.Equal(("texto", "decimal"), (type.Before, type.After));

        var pattern = Single(report, "/Nota/Tomador/Cnpj", SchemaChangeKind.RestrictionChanged);
        Assert.Equal(SchemaChangeImpact.Review, pattern.Impact);
        Assert.Contains("[0-9]{14}", pattern.After, StringComparison.Ordinal);
    }

    [Fact]
    public void New_or_newly_required_unmapped_fields_must_be_mapped()
    {
        var report = Report();

        Assert.Equal(SchemaChangeImpact.Map, Single(report, "/Nota/CodigoMunicipio", SchemaChangeKind.Added).Impact);
        Assert.Equal(SchemaChangeImpact.Map, Single(report, "/Nota/Servico/Aliquota", SchemaChangeKind.BecameRequired).Impact);
        Assert.Equal(SchemaChangeImpact.Info, Single(report, "/Nota/Intermediario", SchemaChangeKind.Added).Impact); // bloco opcional
        Assert.DoesNotContain(report.Changes, c => c.Path == "/Nota/Intermediario/Cpf"); // dentro de bloco opcional novo: não exige nada
    }

    [Fact]
    public void Block_that_became_repeatable_with_mappings_must_be_remapped_and_unmapped_removals_are_info()
    {
        var report = Report();

        Assert.Equal(SchemaChangeImpact.Remap, Single(report, "/Nota/Item", SchemaChangeKind.BecameRepeatable).Impact);
        var obs = Single(report, "/Nota/Obs", SchemaChangeKind.Removed);
        Assert.Equal(SchemaChangeImpact.Info, obs.Impact);
        Assert.False(obs.Mapped);
    }

    [Fact]
    public void Summary_counts_and_ordering_put_actions_first()
    {
        var report = Report();

        Assert.False(report.Identical);
        Assert.Equal(report.Changes.Count(c => c.Impact == SchemaChangeImpact.Remap), report.Summary.Remap);
        Assert.Equal(3, report.Summary.Remap); // Razao, Descricao (movido), Item (repete)
        Assert.Equal(3, report.Summary.Map); // CodigoMunicipio, Aliquota e RazaoSocial (até receber o mapeamento de Razao)
        Assert.Contains("substituto de 'Razao'", Single(report, "/Nota/Tomador/RazaoSocial", SchemaChangeKind.Added).Message, StringComparison.Ordinal);
        Assert.Equal(SchemaChangeImpact.Remap, report.Changes[0].Impact);
        Assert.Equal(SchemaChangeImpact.Info, report.Changes[^1].Impact);
    }

    [Fact]
    public void Identical_schema_reports_nothing_and_without_mapping_nothing_is_remap()
    {
        var service = new DraftDesignService();

        var same = service.CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(V1), SnapshotFixtures.Upload(V1), Mapping));
        Assert.True(same.Identical);
        Assert.Empty(same.Changes);

        var unmapped = service.CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(V1), SnapshotFixtures.Upload(V2)));
        Assert.DoesNotContain(unmapped.Changes, c => c.Impact is SchemaChangeImpact.Remap or SchemaChangeImpact.Review);
    }

    [Fact]
    public void Renamed_root_keeps_children_aligned()
    {
        var report = new DraftDesignService().CompareSchemas(new SchemaChangeRequest(
            SnapshotFixtures.Upload(V1), SnapshotFixtures.Upload(V1.Replace("name=\"Nota\"", "name=\"NotaFiscal\"")), Mapping));

        Assert.Equal(SchemaChangeImpact.Info, Single(report, "/NotaFiscal", SchemaChangeKind.RootChanged).Impact);
        Assert.DoesNotContain(report.Changes, c => c.Kind is SchemaChangeKind.Removed or SchemaChangeKind.Added);
    }

    [Fact]
    public void Invalid_new_xsd_is_refused_naming_which_side()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new DraftDesignService().CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(V1), SnapshotFixtures.Upload("<xs:schema"))));

        Assert.StartsWith("XSD novo:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Large_schemas_with_many_removed_mapped_fields_stay_fast()
    {
        string Schema(string prefix) => $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="R"><xs:complexType><xs:sequence>
            {string.Concat(Enumerable.Range(0, 1_000).Select(i => $"<xs:element name=\"{prefix}{i}\" type=\"xs:string\" minOccurs=\"0\"/>"))}
            </xs:sequence></xs:complexType></xs:element></xs:schema>
            """;
        var mapping = new MappingTemplate { Fields = Enumerable.Range(0, 1_000).Select(i => Field($"campo{i}")).ToList() };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var report = new DraftDesignService().CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(Schema("campo")), SnapshotFixtures.Upload(Schema("item")), mapping));

        Assert.Equal(1_000, report.Summary.Remap);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"levou {watch.Elapsed}");
    }

    private static string Root(string content) => $"""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="R"><xs:complexType><xs:sequence>
        {content}
        </xs:sequence></xs:complexType></xs:element></xs:schema>
        """;

    [Fact]
    public void Long_enumerations_are_compared_structurally()
    {
        // Revisão da Fase 14, A1: a descrição trunca em 20 valores; a comparação não pode.
        string Uf(IEnumerable<int> values) => Root($"""
            <xs:element name="uf"><xs:simpleType><xs:restriction base="xs:string">
            {string.Concat(values.Select(v => $"<xs:enumeration value=\"V{v}\"/>"))}
            </xs:restriction></xs:simpleType></xs:element>
            """);
        var report = new DraftDesignService().CompareSchemas(new SchemaChangeRequest(
            SnapshotFixtures.Upload(Uf(Enumerable.Range(0, 30))), SnapshotFixtures.Upload(Uf(Enumerable.Range(0, 30).Where(v => v != 24))),
            new MappingTemplate { Fields = [Field("uf")] }));

        var change = Single(report, "/R/uf", SchemaChangeKind.RestrictionChanged);
        Assert.Equal(SchemaChangeImpact.Review, change.Impact);
        Assert.Contains("V24", change.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Suggestions_have_bounded_cost_with_long_names_and_many_siblings()
    {
        // Revisão da Fase 14, A2: nomes de 64 caracteres × 2.000 irmãos × 500 removidos mapeados (limite por bloco aumentado só aqui).
        string Name(string prefix, int i) => (prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).PadRight(64, 'x');
        string Schema(string prefix) => Root(string.Concat(Enumerable.Range(0, 2_000).Select(i => $"<xs:element name=\"{Name(prefix, i)}\" type=\"xs:string\" minOccurs=\"0\"/>")));
        var mapping = new MappingTemplate { Fields = Enumerable.Range(0, 500).Select(i => Field(Name("a", i))).ToList() };
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var report = new DraftDesignService(importOptions: new Tradux.Xsd.XsdImportSecurityOptions(MaxElementsPerContentModel: 3_000)).CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(Schema("a")), SnapshotFixtures.Upload(Schema("b")), mapping));

        Assert.Equal(500, report.Summary.Remap);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"levou {watch.Elapsed}");
    }

    [Fact]
    public void Same_name_elsewhere_with_another_type_is_not_taken_as_moved_over_a_likely_rename()
    {
        // Revisão da Fase 14, A3.
        var v1 = Root("""<xs:element name="A"><xs:complexType><xs:sequence><xs:element name="valor" type="xs:decimal"/></xs:sequence></xs:complexType></xs:element>""");
        var v2 = Root("""
            <xs:element name="A"><xs:complexType><xs:sequence><xs:element name="valorServico" type="xs:decimal"/></xs:sequence></xs:complexType></xs:element>
            <xs:element name="C"><xs:complexType><xs:sequence><xs:element name="valor" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
            """);
        var mapping = new MappingTemplate { Complex = [new ComplexMapping { DestinationElementName = "A", Content = new MappingTemplate { Fields = [Field("valor")] } }] };

        var report = new DraftDesignService().CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(v1), SnapshotFixtures.Upload(v2), mapping));

        var removed = Single(report, "/R/A/valor", SchemaChangeKind.Removed);
        Assert.Equal("/R/A/valorServico", removed.Suggestions![0]);
        Assert.DoesNotContain(report.Changes, c => c.Kind == SchemaChangeKind.Moved);
    }

    [Fact]
    public void Malformed_mapping_is_an_argument_error_not_a_crash()
    {
        var malformed = new MappingTemplate { Fields = [null!] };

        Assert.Throws<ArgumentException>(() => new DraftDesignService().CompareSchemas(new SchemaChangeRequest(SnapshotFixtures.Upload(V1), SnapshotFixtures.Upload(V2), malformed)));
    }
}
