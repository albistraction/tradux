using System.Globalization;
using System.Text.Json;
using Tradux.Application.Testing;
using Tradux.Contracts;
using Tradux.Domain.SourceData;
using Tradux.Xsd;

namespace Tradux.Application.Generation;

/// <summary>
/// Único leitor de entrada, em XML de origem (ADR-0015: tamanho, prazo de
/// validação, XSD de origem) ou em JSON, sempre com os limites de entrada
/// (ADR-0014 §3). Geração, teste do editor, gates e regressão de
/// atualizações leem por aqui, com os limites configurados.
/// </summary>
public static class SourceInputs
{
    /// <summary>Teto do tempo de validação; dentro de uma geração, nunca passa do prazo que resta.</summary>
    private static readonly TimeSpan MaxValidationTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Entrada em JSON: os limites valem para o JSON recebido antes de montar a
    /// árvore (entrada gigante não é alocada) e para a árvore montada.
    /// </summary>
    /// <exception cref="ArgumentException">Não é um objeto JSON válido para a árvore interna, ou passa dos limites.</exception>
    public static SourceDataTree Json(JsonElement data, GenerationLimits limits)
    {
        limits.Enforce(data);
        var tree = SourceDataTree.FromJson(data);
        limits.Enforce(tree);
        return tree;
    }

    /// <summary>Entrada de um cenário funcional como árvore interna.</summary>
    /// <exception cref="ArgumentException">JSON inválido, cenário XML sem XSD de origem ou entrada acima dos limites.</exception>
    /// <exception cref="SourceXmlException">XML fora do XSD de origem.</exception>
    public static SourceDataTree Tree(FunctionalScenario scenario, SourceSchema? source, GenerationLimits limits) =>
        scenario.SourceXml is { Length: > 0 } xml ? Xml(RequireSource(source), xml, limits) : Json(scenario.Data, limits);

    /// <summary>A entrada do cenário em JSON: semente da suíte estrutural, que trabalha com dados JSON.</summary>
    /// <exception cref="ArgumentException">Cenário XML sem XSD de origem ou entrada acima dos limites.</exception>
    /// <exception cref="SourceXmlException">XML fora do XSD de origem.</exception>
    public static JsonElement Seed(FunctionalScenario scenario, SourceSchema? source, GenerationLimits limits)
    {
        if (scenario.SourceXml is { Length: > 0 } xml)
        {
            return JsonSerializer.SerializeToElement(Values(RequireSource(source), xml, limits));
        }

        limits.Enforce(scenario.Data);
        return scenario.Data;
    }

    /// <exception cref="GenerationInputLimitException">XML ou árvore acima dos limites.</exception>
    /// <exception cref="SourceXmlException">XML malformado ou fora do XSD de origem.</exception>
    public static SourceDataTree Xml(SourceSchema source, string xml, GenerationLimits limits) =>
        new(Values(source, xml, limits));

    /// <summary>XML em bytes: respeita a codificação declarada no próprio XML.</summary>
    /// <exception cref="GenerationInputLimitException">XML ou árvore acima dos limites.</exception>
    /// <exception cref="SourceXmlException">XML malformado ou fora do XSD de origem.</exception>
    public static SourceDataTree Xml(SourceSchema source, byte[] xml, GenerationLimits limits)
    {
        EnforceSize(xml.Length, limits);
        return new(Enforced(SourceXmlReader.Read(source, new MemoryStream(xml, writable: false), ReaderLimits(limits)), limits));
    }

    /// <summary>Problemas do XML de origem como diagnósticos (linha/coluna quando conhecidas).</summary>
    public static IReadOnlyList<Diagnostic> Diagnostics(SourceXmlException ex) =>
        ex.Problems.Select(p => Diagnostic.Critical(XmlGenerationService.SourceXmlInvalidCode,
            p.Line > 0 ? $"Linha {p.Line.ToString(CultureInfo.InvariantCulture)}, coluna {p.Position.ToString(CultureInfo.InvariantCulture)}: {p.Message}" : p.Message,
            "O XML de entrada precisa seguir o XSD de origem do template.")).ToList();

    private static SourceSchema RequireSource(SourceSchema? source) =>
        source ?? throw new ArgumentException("Entrada em XML exige o XSD de origem do template.", nameof(source));

    internal static void EnforceSize(long length, GenerationLimits limits)
    {
        if (length > limits.MaxSourceXmlCharacters)
        {
            throw new GenerationInputLimitException(GenerationLimits.SizeLimit,
                $"XML de origem com mais de {limits.MaxSourceXmlCharacters.ToString(CultureInfo.InvariantCulture)} caracteres.");
        }
    }

    private static SourceXmlLimits ReaderLimits(GenerationLimits limits)
    {
        var remaining = Engine.Validation.GenerationDeadline.Remaining;
        return limits.SourceXml with { MaxValidationTime = remaining < MaxValidationTime ? remaining : MaxValidationTime };
    }

    private static IReadOnlyDictionary<string, object?> Values(SourceSchema source, string xml, GenerationLimits limits)
    {
        EnforceSize(xml.Length, limits);
        return Enforced(SourceXmlReader.Read(source, xml, ReaderLimits(limits)), limits);
    }

    private static IReadOnlyDictionary<string, object?> Enforced(IReadOnlyDictionary<string, object?> values, GenerationLimits limits)
    {
        limits.Enforce(new SourceDataTree(values));
        return values;
    }
}
