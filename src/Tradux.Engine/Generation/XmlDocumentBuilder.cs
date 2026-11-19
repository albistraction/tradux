using System.Globalization;
using System.Xml.Linq;
using Tradux.Engine.Diagnostics;
using Tradux.Engine.Execution;
using Tradux.Xsd;

namespace Tradux.Engine.Generation;

/// <summary>
/// Gerador de XML (docs/architecture/COMPONENTS.md). Constrói o documento
/// recursivamente a partir da árvore de itens já resolvidos — nunca por
/// XML-base fixo nem concatenação de string (seção 15 do requisito de
/// origem). Ordem dos elementos segue exatamente a ordem em que
/// <c>TemplateCompiler</c> os organizou a partir do schema.
///
/// Um valor que não pode ser convertido para o tipo escalar do elemento
/// (ex.: texto "abc" num <c>xs:decimal</c>, objeto num <c>xs:string</c>)
/// vira diagnóstico crítico <c>VALUE_CONVERSION_FAILED</c> com XPath — nunca
/// exceção (.claude/rules/dotnet.md; revisão da Fase 5, B7/B8).
/// </summary>
public static class XmlDocumentBuilder
{
    private static readonly XNamespace XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    /// <param name="diagnostics">Recebe os erros de conversão.</param>
    public static XDocument Build(DestinationSchema schema, IReadOnlyList<ResolvedItem> rootItems, List<EngineDiagnostic> diagnostics)
    {
        var ns = schema.TargetNamespace is { Length: > 0 } tns ? XNamespace.Get(tns) : XNamespace.None;
        var root = new XElement(ns + schema.RootElementName);

        foreach (var attribute in schema.Attributes)
        {
            if (attribute.FixedValue is not null)
            {
                root.SetAttributeValue(attribute.Name, attribute.FixedValue);
            }
        }

        AppendItems(root, ns, rootItems, "/" + schema.RootElementName, diagnostics);

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static void AppendItems(
        XElement parent, XNamespace ns, IReadOnlyList<ResolvedItem> items, string xpath, List<EngineDiagnostic> diagnostics)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case ResolvedScalar scalar:
                    AppendScalar(parent, ns, scalar, xpath, diagnostics);
                    break;

                case ResolvedComplex { Kind: ResolvedValueKind.Omitted }:
                    break;

                case ResolvedComplex complex:
                    var complexElement = new XElement(ns + complex.Name);
                    AppendItems(complexElement, ns, complex.Children!, $"{xpath}/{complex.Name}", diagnostics);
                    parent.Add(complexElement);
                    break;

                case ResolvedGroup group:
                    AppendItems(parent, ns, group.Items, xpath, diagnostics); // grupo: transparente no XML
                    break;

                case ResolvedCollection collection:
                    var index = 0;
                    foreach (var itemChildren in collection.Items)
                    {
                        var itemElement = new XElement(ns + collection.Name);
                        var itemPath = $"{xpath}/{collection.Name}[{index++}]";
                        AppendItems(itemElement, ns, itemChildren, itemPath, diagnostics);
                        parent.Add(itemElement);
                    }

                    break;

                default:
                    throw new NotSupportedException($"Item resolvido '{item.GetType().Name}' não suportado.");
            }
        }
    }

    private static void AppendScalar(
        XElement parent, XNamespace ns, ResolvedScalar scalar, string xpath, List<EngineDiagnostic> diagnostics)
    {
        if (scalar.Kind == ResolvedValueKind.Omitted)
        {
            return;
        }

        var element = new XElement(ns + scalar.Name);

        if (scalar.Kind == ResolvedValueKind.ExplicitNil)
        {
            element.SetAttributeValue(XsiNamespace + "nil", "true");
        }
        else if (XsdScalarText.TryFormat(scalar.Element.ScalarType, scalar.Element.Facets.FractionDigits, scalar.Value!, out var text))
        {
            // Valor que faria o pattern do XSD rodar sem fim: falha aqui, sem
            // chegar ao validador (que não tem timeout próprio).
            if (scalar.Element.Facets.PatternSteps.Count > 0 && !Validation.PatternGuard.CompletesInTime(text, scalar.Element.Facets.PatternSteps))
            {
                if (Validation.GenerationDeadline.Expired && diagnostics.Any(d => d.Code == EngineDiagnosticCodes.XsdValidationTimeout))
                {
                    return; // prazo esgotado: um diagnóstico basta
                }

                diagnostics.Add(EngineDiagnostic.Critical(
                    EngineDiagnosticCodes.XsdValidationTimeout,
                    $"A verificação do pattern do elemento '{scalar.Name}' excedeu o tempo limite.",
                    $"{xpath}/{scalar.Name}",
                    suggestion: "Simplifique o pattern do schema de destino (evite repetições aninhadas ou alternativas sobrepostas)."));
                return;
            }

            element.Value = text;
        }
        else
        {
            diagnostics.Add(EngineDiagnostic.Critical(
                "VALUE_CONVERSION_FAILED",
                $"Valor do tipo '{ScalarValues.DescribeKind(scalar.Value)}' não pode ser convertido para o tipo " +
                $"'{scalar.Element.ScalarType}' do elemento '{scalar.Name}'.",
                $"{xpath}/{scalar.Name}",
                suggestion: "Envie o valor na forma do tipo XSD do elemento (ex.: número com ponto decimal, data ISO-8601) ou ajuste a regra."));
            return;
        }

        parent.Add(element);
    }
}
