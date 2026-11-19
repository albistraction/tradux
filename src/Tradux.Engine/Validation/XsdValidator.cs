using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Tradux.Engine.Diagnostics;

namespace Tradux.Engine.Validation;

/// <summary>
/// Validador (docs/architecture/COMPONENTS.md): valida o XML gerado contra
/// o conjunto exato de XSDs da versão do template (seção 15/17 do requisito
/// de origem). Todo erro de validação de schema é sempre crítico. Rastreia o
/// elemento aberto no momento de cada evento para produzir um
/// <c>DestinationXPath</c> aproximado — diagnóstico útil também neste
/// estágio final, não só nos estágios de compilação/execução de regras.
/// </summary>
public static class XsdValidator
{
    public static IReadOnlyList<EngineDiagnostic> Validate(XDocument document, XmlSchemaSet schemaSet)
    {
        var diagnostics = new List<EngineDiagnostic>();
        var openElementPath = new List<string>();

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = schemaSet,
            XmlResolver = null,
        };
        settings.ValidationEventHandler += (_, e) =>
        {
            var severity = e.Severity == XmlSeverityType.Error
                ? EngineDiagnosticSeverity.Critical
                : EngineDiagnosticSeverity.Warning;

            diagnostics.Add(new EngineDiagnostic(
                severity,
                Code: "XSD_VALIDATION",
                Message: e.Message,
                DestinationXPath: openElementPath.Count > 0 ? "/" + string.Join('/', openElementPath) : null,
                Suggestion: "Verifique o valor contra as restrições do schema de destino (pattern, enumeração, faixa ou tamanho)."));
        };

        using var sourceReader = document.CreateReader();
        using var validatingReader = XmlReader.Create(sourceReader, settings);

        try
        {
            ReadAll(validatingReader, openElementPath);
        }
        catch (Exception ex) when (ex is System.Text.RegularExpressions.RegexMatchTimeoutException or TimeoutException)
        {
            // Pattern do XSD caro demais para o valor (retrocesso): o host fixa um
            // timeout global de regex (REGEX_DEFAULT_MATCH_TIMEOUT, ADR-0012 §9);
            // estourar é falha crítica, nunca thread presa nem aceitação.
            diagnostics.Add(new EngineDiagnostic(
                EngineDiagnosticSeverity.Critical,
                Code: EngineDiagnosticCodes.XsdValidationTimeout,
                Message: "A validação de um pattern do XSD excedeu o tempo limite.",
                DestinationXPath: openElementPath.Count > 0 ? "/" + string.Join('/', openElementPath) : null,
                Suggestion: "Simplifique o pattern do schema de destino (evite repetições aninhadas) ou revise o valor."));
        }

        return diagnostics;
    }

    private static void ReadAll(XmlReader validatingReader, List<string> openElementPath)
    {
        while (validatingReader.Read())
        {
            if (GenerationDeadline.Expired)
            {
                throw new TimeoutException("Orçamento de validação da geração esgotado.");
            }

            switch (validatingReader.NodeType)
            {
                case XmlNodeType.Element:
                    openElementPath.Add(validatingReader.LocalName);
                    if (validatingReader.IsEmptyElement)
                    {
                        openElementPath.RemoveAt(openElementPath.Count - 1);
                    }

                    break;
                case XmlNodeType.EndElement when openElementPath.Count > 0:
                    openElementPath.RemoveAt(openElementPath.Count - 1);
                    break;
            }
        }
    }
}
