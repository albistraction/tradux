using Tradux.Engine.Diagnostics;
using System.Linq.Expressions;
using System.Xml.Linq;
using Tradux.Domain.Mapping;
using Tradux.Engine.Operators;
using Xunit;

namespace Tradux.Engine.Tests;

/// <summary>
/// Prova, de ponta a ponta (compilação real via Expression Trees, execução,
/// geração e validação de XML — não apenas chamando os métodos do operador
/// diretamente), o catálogo tipado de operadores, grupos lógicos
/// (condição de mapeamento) e a extensibilidade por novos operadores C#
/// (seção 13 do requisito de origem, Fase 4).
/// </summary>
public class RuleEngineGenerationTests
{
    private static string FixturePath => RepositoryPaths.Combine("docs", "schemas", "exemplo-simples.xsd");

    private static readonly XNamespace Ns = "http://exemplo.ilustrativo.tradux/dps/v1";

    private static MappingTemplate BaseTemplate(params FieldMapping[] overrides)
    {
        var byName = overrides.ToDictionary(m => m.DestinationElementName);

        FieldMapping Resolve(string destination, string sourcePath) =>
            byName.TryGetValue(destination, out var over) ? over : FieldMapping.Direct(sourcePath, destination);

        return new MappingTemplate
        {
            Fields =
            [
                Resolve("numero", "Numero"),
                Resolve("serie", "Serie"),
                Resolve("dataEmissao", "DataEmissao"),
                Resolve("competencia", "Competencia"),
                Resolve("valorServico", "ValorServico"),
                Resolve("valorDesconto", "ValorDesconto"),
                Resolve("codigoServico", "CodigoServico"),
                Resolve("cnpjPrestador", "CnpjPrestador"),
                Resolve("cnpjTomador", "CnpjTomador"),
                Resolve("discriminacaoServico", "DiscriminacaoServico"),
                Resolve("codigoSituacao", "CodigoSituacao"),
            ],
        };
    }

    [Fact]
    public void Concat_operator_composes_a_literal_with_a_field_reference()
    {
        var mapping = new FieldMapping(
            "numero", OperatorNode.Of("concat", new LiteralNode("PED-"), new FieldReferenceNode("Numero")));
        var template = EngineHarness.Compile(FixturePath, BaseTemplate(mapping));

        var result = EngineHarness.Generate(DpsSourceData.Realista(), template);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(result.Xml!);
        Assert.Equal("PED-000123", doc.Root!.Element(Ns + "numero")!.Value);
    }

    [Fact]
    public void Lookup_operator_translates_a_code_through_a_de_para_table()
    {
        var mapping = new FieldMapping(
            "discriminacaoServico", OperatorNode.Of("lookup", new FieldReferenceNode("CodigoSituacao"), new LiteralNode("situacaoLabels")));
        var lookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["situacaoLabels"] = new Dictionary<string, string> { ["1"] = "Situação normal", ["2"] = "Cancelada" },
        };

        var importedXsd = XsdFiles.Import(FixturePath);
        var compiled = Compilation.TemplateCompiler.Compile(BaseTemplate(mapping), importedXsd, lookupTables: lookupTables);
        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(DpsSourceData.Realista()));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(outcome.Xml!);
        Assert.Equal("Situação normal", doc.Root!.Element(Ns + "discriminacaoServico")!.Value);
    }

    [Fact]
    public void Lookup_with_unmapped_key_produces_a_critical_diagnostic_not_a_crash()
    {
        var mapping = new FieldMapping(
            "discriminacaoServico", OperatorNode.Of("lookup", new FieldReferenceNode("CodigoSituacao"), new LiteralNode("situacaoLabels")));
        var lookupTables = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["situacaoLabels"] = new Dictionary<string, string> { ["9"] = "Não deveria bater" },
        };

        var importedXsd = XsdFiles.Import(FixturePath);
        var compiled = Compilation.TemplateCompiler.Compile(BaseTemplate(mapping), importedXsd, lookupTables: lookupTables);
        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(DpsSourceData.Realista()));

        Assert.False(outcome.Success);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "LOOKUP_KEY_NOT_FOUND");
    }

    [Fact]
    public void IfElse_evaluates_only_the_chosen_branch_and_lookup_default_avoids_the_error()
    {
        // Fase 21: o ramo não escolhido (lookup sem padrão, chave fora da tabela) não é avaliado.
        var tables = new Dictionary<string, IReadOnlyDictionary<string, string>> { ["situacaoLabels"] = new Dictionary<string, string> { ["9"] = "Nove" } };
        var strict = OperatorNode.Of("lookup", new FieldReferenceNode("CodigoSituacao"), new LiteralNode("situacaoLabels"));
        var withDefault = OperatorNode.Of("lookup", new FieldReferenceNode("CodigoSituacao"), new LiteralNode("situacaoLabels"), new LiteralNode("sem tabela"));

        string Generate(RuleNode value)
        {
            var compiled = Compilation.TemplateCompiler.Compile(BaseTemplate(new FieldMapping("discriminacaoServico", value)),
                XsdFiles.Import(FixturePath), lookupTables: tables);
            var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(DpsSourceData.Realista()));
            Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Code)));
            return XDocument.Parse(outcome.Xml!).Root!.Element(Ns + "discriminacaoServico")!.Value;
        }

        Assert.Equal("outro", Generate(OperatorNode.Of("ifElse", new LiteralNode(false), strict, new LiteralNode("outro"))));
        Assert.Equal("sem tabela", Generate(withDefault));
        Assert.Equal("sem", Generate(OperatorNode.Of("truncate", withDefault, new LiteralNode(3m))));
    }

    [Fact]
    public void Field_mapping_condition_gates_whether_the_mapping_applies()
    {
        // Grupo de regras (seção 9): discriminacaoServico só é mapeado
        // quando o tomador tem CNPJ — do contrário, o campo (opcional) fica
        // omitido, mesmo que o dado de origem tenha um valor.
        var mapping = new FieldMapping(
            "discriminacaoServico",
            Value: new FieldReferenceNode("DiscriminacaoServico"),
            Condition: OperatorNode.Of("notEmpty", new FieldReferenceNode("CnpjTomador")));
        var template = EngineHarness.Compile(FixturePath, BaseTemplate(mapping));

        var comCnpj = DpsSourceData.Realista();
        var resultComCnpj = EngineHarness.Generate(comCnpj, template);
        Assert.True(resultComCnpj.Success);
        Assert.NotNull(XDocument.Parse(resultComCnpj.Xml!).Root!.Element(Ns + "discriminacaoServico"));

        var semCnpj = DpsSourceData.Realista();
        semCnpj.CnpjTomador = null;
        var resultSemCnpj = EngineHarness.Generate(semCnpj, template);
        Assert.True(resultSemCnpj.Success);
        Assert.Null(XDocument.Parse(resultSemCnpj.Xml!).Root!.Element(Ns + "discriminacaoServico"));
    }

    [Fact]
    public void Type_error_inside_a_condition_is_a_critical_diagnostic_not_a_crash()
    {
        // Mesma proteção de diagnóstico que já existe para erros dentro de
        // Value (ver testes de lookup acima), agora para dentro de
        // Condition: greaterThan entre texto não numérico e inteiro.
        var mapping = new FieldMapping(
            "discriminacaoServico",
            Value: new FieldReferenceNode("DiscriminacaoServico"),
            Condition: OperatorNode.Of("greaterThan", new FieldReferenceNode("DiscriminacaoServico"), new LiteralNode(10m)));
        var template = EngineHarness.Compile(FixturePath, BaseTemplate(mapping));

        var result = EngineHarness.Generate(DpsSourceData.Realista(), template);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "COMPARISON_INCOMPATIBLE_TYPES");
        Assert.Equal(EngineDiagnosticSeverity.Critical, diagnostic.Severity);
    }

    [Fact]
    public void Custom_operator_registered_outside_the_engine_core_works_end_to_end()
    {
        // "Extensão segura por novos operadores C#" (seção 13): o operador
        // 'shout' é definido e registrado inteiramente neste teste — nenhum
        // arquivo de Tradux.Engine precisou mudar para isso funcionar.
        var catalog = Operators.RuleOperatorCatalog.CreateDefault();
        var shoutMethod = typeof(TestOnlyOperators).GetMethod(
            nameof(TestOnlyOperators.Shout), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        catalog.Register(new RuleOperatorDefinition("shout", 1, 1, (_, args, _) => Expression.Call(shoutMethod, args[0])));

        var mapping = new FieldMapping("discriminacaoServico", OperatorNode.Of("shout", new FieldReferenceNode("DiscriminacaoServico")));
        var importedXsd = XsdFiles.Import(FixturePath);
        var compiled = Compilation.TemplateCompiler.Compile(BaseTemplate(mapping), importedXsd, catalog);
        var outcome = XmlGenerationEngine.Generate(compiled, EngineHarness.Source(DpsSourceData.Realista()));

        Assert.True(outcome.Success, string.Join("; ", outcome.Diagnostics.Select(d => d.Message)));
        var doc = XDocument.Parse(outcome.Xml!);
        Assert.Equal("SERVIÇO DE CONSULTORIA", doc.Root!.Element(Ns + "discriminacaoServico")!.Value);
    }

    [Fact]
    public void Unknown_operator_name_fails_fast_at_compile_time()
    {
        var mapping = new FieldMapping("numero", OperatorNode.Of("naoExiste", new FieldReferenceNode("Numero")));
        var importedXsd = XsdFiles.Import(FixturePath);

        Assert.Throws<NotSupportedException>(() => Compilation.TemplateCompiler.Compile(BaseTemplate(mapping), importedXsd));
    }
}

/// <summary>Operador de teste, definido fora de Tradux.Engine — prova a extensibilidade sem tocar o núcleo.</summary>
public static class TestOnlyOperators
{
    public static object? Shout(object? value) => (value as string)?.ToUpperInvariant();
}
