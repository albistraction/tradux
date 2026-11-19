using System.Xml.Linq;
using Tradux.Application.Generation;
using Tradux.Application.Quality;
using Tradux.Application.Testing;
using Tradux.Contracts;
using Tradux.Contracts.Quality;
using Tradux.Domain.SourceData;
using Tradux.Engine;

namespace Tradux.Application.Publishing;

/// <summary>Falhas de todos os gates (vazio = aprovado) e o relatório dos testes estruturais.</summary>
public sealed record PipelineResult(IReadOnlyList<Diagnostic> Failures, QualityReport Report);

/// <summary>
/// Gates de publicação (seção 16: "publica apenas se tudo passar"; seção 19:
/// "nenhum template pode ser publicado se os testes obrigatórios falharem"),
/// iguais para todo caminho de publicação (ADR-0011 §5), sempre nesta ordem:
/// compilação → testes estruturais → cenários funcionais. Se a compilação
/// reprova, os testes não rodam. Exceção inesperada num gate vira reprovação
/// (<see cref="GateExecutionFailedCode"/>), nunca aprovação.
/// </summary>
internal static class PublicationGates
{
    public const string GateExecutionFailedCode = "GATE_EXECUTION_FAILED";

    public static PipelineResult Evaluate(PublicationCandidate candidate, IReadOnlyList<FunctionalScenario> scenarios, GenerationLimits limits)
    {
        // Obrigatório sem mapeamento, operador inválido etc. (diagnóstico crítico de compilação).
        var compilation = GenerationResultMapper.ToContract(
            candidate.Template.CompileTimeDiagnostics.Where(d => d.Severity == Engine.Diagnostics.EngineDiagnosticSeverity.Critical));
        if (compilation.Count > 0)
        {
            return new PipelineResult(compilation, QualityReport.Empty);
        }

        var structural = Run("testes estruturais", () => StructuralTestGate.Evaluate(candidate, scenarios, limits));
        var functional = Run("cenários funcionais", () => (FunctionalScenarioGate.Evaluate(candidate, scenarios, limits), null));
        return new PipelineResult([.. structural.Failures, .. functional.Failures], structural.Report ?? QualityReport.Empty);
    }

    private static (IReadOnlyList<Diagnostic> Failures, QualityReport? Report) Run(string gate, Func<(IReadOnlyList<Diagnostic>, QualityReport?)> evaluate)
    {
        try
        {
            return evaluate();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([new Diagnostic(DiagnosticSeverity.Critical, GateExecutionFailedCode,
                $"O gate '{gate}' falhou ao executar ({ex.GetType().Name}); a publicação é recusada.", null, null, null)], null);
        }
    }
}

/// <summary>
/// Testes estruturais automáticos (seção 19, ADR-0011 §3): suíte
/// determinística gerada do perfil de origem + XSD. Cada caso reprovado vira
/// um diagnóstico crítico com nome do caso, código e XPath — nunca valores.
/// </summary>
public static class StructuralTestGate
{
    public const string StructuralTestFailedCode = "STRUCTURAL_TEST_FAILED";
    private const int MaxReportedFailures = 20;

    internal static (IReadOnlyList<Diagnostic> Failures, QualityReport? Report) Evaluate(PublicationCandidate candidate, IReadOnlyList<FunctionalScenario> scenarios, GenerationLimits limits)
    {
        // Sementes em JSON: cenário em XML é convertido pelo XSD de origem (inválido fica de fora — o gate funcional acusa).
        var seeds = new List<System.Text.Json.JsonElement>();
        foreach (var scenario in scenarios)
        {
            try
            {
                seeds.Add(SourceInputs.Seed(scenario, candidate.Source, limits));
            }
            catch (Exception ex) when (ex is ArgumentException or Xsd.SourceXmlException)
            {
            }
        }

        var report = StructuralTestRunner.Run(candidate.Template, candidate.Content.Mapping, candidate.Content.LookupTables, seeds);
        if (report.Cases.Count == 0)
        {
            return ([new Diagnostic(DiagnosticSeverity.Critical, StructuralTestFailedCode,
                "Nenhum caso estrutural pôde ser gerado para o template.", null, null, null)], report);
        }

        var failed = report.Cases.Where(c => !c.Passed).ToList();
        var failures = failed.Take(MaxReportedFailures).Select(c => new Diagnostic(
            DiagnosticSeverity.Critical,
            StructuralTestFailedCode,
            c.ExpectedValid
                ? c.FailureCode switch
                {
                    StructuralTestRunner.ValueUnavailableCode =>
                        $"Teste estrutural: não há valor válido gerável para {c.Target}, que alimenta destino obrigatório — cadastre um cenário funcional com esse campo.",
                    StructuralTestRunner.ChoiceBranchUnreachableCode =>
                        $"Teste estrutural: o ramo {c.Target} nunca sai sozinho — outro ramo do mesmo xs:choice sempre aparece (campo usado também fora do choice, ou constante); entrada com ele gera dois ramos.",
                    _ => $"Teste estrutural '{c.Name}' ({c.Category}): entrada válida pelo contrato não gerou XML válido ({c.FailureCode}).",
                }
                : $"Teste estrutural '{c.Name}': entrada que viola {c.Target} foi aceita — o template deveria reprovar.",
            c.DestinationXPath,
            null,
            c.ExpectedValid
                ? "Ajuste a regra do destino apontado para que toda entrada válida gere XML válido."
                : "Confira se a regra transforma o valor de forma a esconder a violação.")).ToList();
        if (failed.Count > MaxReportedFailures)
        {
            failures.Add(new Diagnostic(DiagnosticSeverity.Critical, StructuralTestFailedCode,
                $"Mais {failed.Count - MaxReportedFailures} testes estruturais reprovados (ver relatório).", null, null, null));
        }

        return (failures, report);
    }
}

/// <summary>
/// Cenários funcionais do autor (seção 19): exige ao menos um cenário com XML
/// esperado ou asserções estruturais; cada cenário precisa gerar XML válido
/// e bater com o esperado (comparação semântica) e com as asserções. As
/// mensagens trazem só código e XPath, nunca valores do cenário ou do XML:
/// ficam gravadas em atualizações e auditoria (seção 20).
/// </summary>
public static class FunctionalScenarioGate
{
    public const string ScenarioFailedCode = "SCENARIO_FAILED";
    public const string ScenarioRequiredCode = "FUNCTIONAL_SCENARIO_REQUIRED";

    internal static IReadOnlyList<Diagnostic> Evaluate(PublicationCandidate candidate, IReadOnlyList<FunctionalScenario> scenarios, GenerationLimits limits)
    {
        var failures = new List<Diagnostic>();
        if (!scenarios.Any(s => s.ExpectedXml is { Length: > 0 } || s.Assertions is { Count: > 0 }))
        {
            failures.Add(new Diagnostic(DiagnosticSeverity.Critical, ScenarioRequiredCode,
                "Nenhum cenário funcional com XML esperado ou asserções estruturais: a publicação exige ao menos um.",
                null, null, "Cadastre um cenário com entrada real de negócio e o XML esperado (ou asserções por caminho)."));
        }

        foreach (var scenario in scenarios)
        {
            SourceDataTree data;
            try
            {
                data = SourceInputs.Tree(scenario, candidate.Source, limits);
            }
            catch (Exception ex) when (ex is ArgumentException or Xsd.SourceXmlException)
            {
                failures.Add(Failure(scenario, UnreadableInput(ex), null));
                continue;
            }

            var outcome = XmlGenerationEngine.Generate(candidate.Template, data);
            if (!outcome.Success)
            {
                var first = outcome.Diagnostics.FirstOrDefault(d => d.Severity == Engine.Diagnostics.EngineDiagnosticSeverity.Critical);
                failures.Add(Failure(scenario, $"não gera XML válido ({first?.Code}).", first?.DestinationXPath));
                continue;
            }

            if (scenario.ExpectedXml is { Length: > 0 } expected)
            {
                var comparison = XmlSemanticComparer.Compare(expected, outcome.Xml);
                if (!comparison.Equal)
                {
                    failures.Add(Failure(scenario, comparison.ExpectedInvalid ? "o XML esperado do cenário não é bem formado." : "o XML gerado difere do esperado.", comparison.XPath));
                }
            }

            if (scenario.Assertions is { Count: > 0 } assertions)
            {
                var document = XDocument.Parse(outcome.Xml!);
                foreach (var assertion in assertions)
                {
                    if (StructuralAssertions.Check(document, assertion) is { } problem)
                    {
                        failures.Add(Failure(scenario, problem, assertion.Path));
                    }
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// Por que a entrada do cenário não pôde ser lida, só com tipo, limite ou
    /// posição: a mensagem do validador pode trazer valores do cenário.
    /// </summary>
    internal static string UnreadableInput(Exception ex) => ex switch
    {
        Xsd.SourceXmlException xml when xml.Problems.Count > 0 =>
            $"XML de origem do cenário não segue o XSD de origem (linha {xml.Problems[0].Line}, coluna {xml.Problems[0].Position}).",
        GenerationInputLimitException limit => $"entrada do cenário acima do limite '{limit.Limit}'.",
        _ => $"dados do cenário inválidos ({ex.GetType().Name}).",
    };

    private static Diagnostic Failure(FunctionalScenario scenario, string message, string? xpath) =>
        new(DiagnosticSeverity.Critical, ScenarioFailedCode, $"Cenário '{scenario.Name}': {message}", xpath, null,
            "Ajuste a camada (ou o cenário, se o comportamento novo for o correto) antes de publicar.");
}
