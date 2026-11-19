using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Tradux.Domain.SourceData;
using Tradux.Engine.Compilation;
using Tradux.Engine.Diagnostics;
using Tradux.Engine.Execution;
using Tradux.Engine.Generation;
using Tradux.Engine.Validation;

namespace Tradux.Engine;

/// <summary>
/// Orquestrador de ponta a ponta do motor: executa regras, gera o XML e
/// valida contra o XSD exato do template (seções 15, 17, 18 do requisito de
/// origem). O fallback entre versões candidatas (seção 17) fica em
/// <c>XmlGenerationService</c>, que escolhe o template antes de chamar o motor.
/// Pós-processamento do XML (assinatura etc.) é de quem chama o serviço (ADR-0025).
/// </summary>
public static class XmlGenerationEngine
{
    public static GenerationOutcome Generate(CompiledTemplate template, SourceDataTree sourceData)
    {
        using var deadline = Validation.GenerationDeadline.Start();
        var stopwatch = Stopwatch.StartNew();

        if (template.HasCriticalCompileTimeDiagnostics)
        {
            stopwatch.Stop();
            return new GenerationOutcome
            {
                Success = false,
                Xml = null,
                Diagnostics = template.CompileTimeDiagnostics,
                Duration = stopwatch.Elapsed,
            };
        }

        var executionResult = RuleExecutor.Execute(template, sourceData);
        if (executionResult.HasCriticalDiagnostics)
        {
            stopwatch.Stop();
            return new GenerationOutcome
            {
                Success = false,
                Xml = null,
                Diagnostics = executionResult.Diagnostics,
                Duration = stopwatch.Elapsed,
            };
        }

        var buildDiagnostics = new List<EngineDiagnostic>();
        var document = XmlDocumentBuilder.Build(template.Xsd.Schema, executionResult.Items, buildDiagnostics);
        if (buildDiagnostics.Count > 0)
        {
            // Valor que não converte para o tipo do elemento: o documento está
            // incompleto; validar contra o XSD só somaria ruído.
            stopwatch.Stop();
            return new GenerationOutcome
            {
                Success = false,
                Xml = null,
                Diagnostics = executionResult.Diagnostics.Concat(buildDiagnostics).ToList(),
                Duration = stopwatch.Elapsed,
            };
        }

        var validationDiagnostics = XsdValidator.Validate(document, template.Xsd.SchemaSet);
        var allDiagnostics = executionResult.Diagnostics.Concat(validationDiagnostics).ToList();
        var success = allDiagnostics.All(d => d.Severity != EngineDiagnosticSeverity.Critical);

        var xml = success ? SerializeUtf8(document) : null;

        stopwatch.Stop();
        return new GenerationOutcome
        {
            Success = success,
            Xml = xml,
            Diagnostics = allDiagnostics,
            Duration = stopwatch.Elapsed,
        };
    }

    private static string SerializeUtf8(XDocument document)
    {
        using var writer = new Utf8StringWriter();
        document.Save(writer);
        return writer.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
