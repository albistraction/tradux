using System.Xml;
using System.Xml.Linq;

namespace Tradux.Application.Testing;

/// <summary>Resultado de uma comparação semântica de XML.</summary>
/// <param name="Equal">Os documentos são semanticamente iguais.</param>
/// <param name="ExpectedInvalid">O XML esperado não é bem formado (ou traz DOCTYPE).</param>
/// <param name="XPath">Onde está a primeira diferença, quando há.</param>
/// <param name="Message">Explicação legível da diferença ou do erro.</param>
public sealed record XmlComparison(bool Equal, bool ExpectedInvalid, string? XPath, string? Message);

/// <summary>
/// Comparação semântica de XML (.claude/rules/testing.md): ignora prefixo de
/// namespace e espaço de formatação; aponta a primeira diferença. Usada pelo
/// teste de rascunho do editor e pelos gates de publicação (cenários
/// funcionais). XML esperado é lido sem DTD.
/// </summary>
public static class XmlSemanticComparer
{
    public static XmlComparison Compare(string expectedXml, string? actualXml)
    {
        XElement expected;
        try
        {
            expected = Normalize(ParseWithoutDtd(expectedXml));
        }
        catch (XmlException ex)
        {
            return new XmlComparison(false, true, null, $"O XML esperado não é um XML bem formado (DOCTYPE não é aceito): {ex.Message}");
        }

        if (actualXml is null)
        {
            return new XmlComparison(false, false, null, "Nenhum XML foi gerado para comparar.");
        }

        var difference = FirstDifference(expected, Normalize(ParseWithoutDtd(actualXml)), "");
        return difference is null
            ? new XmlComparison(true, false, null, null)
            : new XmlComparison(false, false, difference.Value.XPath, $"O XML gerado difere do esperado: {difference.Value.Message}");
    }

    // XML esperado vem do usuário: sem DTD (nem entidades internas), como a
    // importação de XSD (revisão da Fase 6, A6).
    private static XDocument ParseWithoutDtd(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    /// <summary>Primeira diferença semântica (nome, atributo, texto ou filhos), com XPath do ponto — para o editor apontar onde difere.</summary>
    private static (string XPath, string Message)? FirstDifference(XElement expected, XElement actual, string parentPath)
    {
        var path = $"{parentPath}/{actual.Name.LocalName}";
        if (expected.Name != actual.Name)
        {
            return (path, $"esperado elemento <{expected.Name.LocalName}>, gerado <{actual.Name.LocalName}>.");
        }

        var expectedAttributes = expected.Attributes().ToDictionary(a => a.Name, a => a.Value);
        var actualAttributes = actual.Attributes().ToDictionary(a => a.Name, a => a.Value);
        foreach (var name in expectedAttributes.Keys.Union(actualAttributes.Keys))
        {
            expectedAttributes.TryGetValue(name, out var e);
            actualAttributes.TryGetValue(name, out var a);
            if (e != a)
            {
                return (path, $"atributo {name.LocalName}: esperado {Show(e)}, gerado {Show(a)}.");
            }
        }

        var expectedChildren = expected.Elements().ToList();
        var actualChildren = actual.Elements().ToList();
        if (expectedChildren.Count == 0 && actualChildren.Count == 0)
        {
            return expected.Value == actual.Value ? null : (path, $"texto esperado {Show(expected.Value)}, gerado {Show(actual.Value)}.");
        }

        for (var i = 0; i < Math.Max(expectedChildren.Count, actualChildren.Count); i++)
        {
            if (i >= expectedChildren.Count)
            {
                return ($"{path}/{actualChildren[i].Name.LocalName}", $"elemento <{actualChildren[i].Name.LocalName}> gerado a mais.");
            }

            if (i >= actualChildren.Count)
            {
                return ($"{path}/{expectedChildren[i].Name.LocalName}", $"elemento <{expectedChildren[i].Name.LocalName}> esperado não foi gerado.");
            }

            if (FirstDifference(expectedChildren[i], actualChildren[i], path) is { } childDifference)
            {
                return childDifference;
            }
        }

        return null;
    }

    private static string Show(string? value) => value is null ? "(ausente)" : $"\"{value}\"";

    // Comparação semântica (.claude/rules/testing.md): nomes por namespace
    // (prefixo irrelevante), sem declarações xmlns e sem espaço de formatação.
    private static XElement Normalize(XDocument document)
    {
        var root = new XElement(document.Root!);
        foreach (var element in root.DescendantsAndSelf())
        {
            element.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
            foreach (var text in element.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList())
            {
                text.Remove();
            }
        }

        return root;
    }
}
