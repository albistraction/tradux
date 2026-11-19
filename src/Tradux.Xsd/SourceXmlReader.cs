using Tradux.Domain.SourceContracts;
using Tradux.Domain.SourceData;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Tradux.Xsd;

/// <summary>
/// Limites de leitura do XML de origem (ADR-0015 §4), aplicados durante a
/// leitura: profundidade e quantidade de elementos antes de montar a árvore.
/// </summary>
public sealed record SourceXmlLimits(long MaxCharacters, int MaxDepth, int MaxElements = 100_000)
{
    /// <summary>
    /// Tempo total da leitura e validação do XML de origem. O timeout de regex
    /// é por valor; restrições de identidade e patterns caros somam por
    /// elemento. Sem teto total, um XML pequeno custaria minutos.
    /// </summary>
    public TimeSpan MaxValidationTime { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>Um problema do XML de origem: posição (1-based; 0 = desconhecida) e mensagem.</summary>
public sealed record SourceXmlProblem(int Line, int Position, string Message);

/// <summary>XML de origem malformado ou fora do XSD de origem — nada é gerado.</summary>
public sealed class SourceXmlException(IReadOnlyList<SourceXmlProblem> problems)
    : Exception($"XML de origem inválido: {problems.Count} problema(s).")
{
    public IReadOnlyList<SourceXmlProblem> Problems { get; } = problems;
}

/// <summary>
/// Lê uma instância XML do contrato de origem (ADR-0015 §3): valida contra o
/// XSD de origem (DTD proibido, sem resolver externo, limite de tamanho) e
/// converte na mesma forma que um JSON produziria — dicionário por nome local
/// de elemento, coleções como listas conforme o XSD, atributos como
/// <c>@nome</c>, <c>xsi:nil</c> como nulo, números como <see cref="decimal"/>
/// (cultura invariante) e datas como o texto recebido.
/// </summary>
public static class SourceXmlReader
{
    private const int MaxReportedProblems = 20;
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <exception cref="SourceXmlException">XML malformado, fora do XSD ou acima dos limites.</exception>
    public static IReadOnlyDictionary<string, object?> Read(SourceSchema source, string xml, SourceXmlLimits limits)
    {
        using var text = new StringReader(xml);
        return Read(source, settings => XmlReader.Create(text, settings), limits);
    }

    /// <summary>Lê de bytes: respeita a codificação declarada no próprio XML (ex.: ISO-8859-1).</summary>
    /// <exception cref="SourceXmlException">XML malformado, fora do XSD ou acima dos limites.</exception>
    public static IReadOnlyDictionary<string, object?> Read(SourceSchema source, Stream xml, SourceXmlLimits limits) =>
        Read(source, settings => XmlReader.Create(xml, settings), limits);

    private static IReadOnlyDictionary<string, object?> Read(
        SourceSchema source, Func<XmlReaderSettings, XmlReader> open, SourceXmlLimits limits)
    {
        var shape = source.Shape;
        var problems = new List<SourceXmlProblem>();
        var deadline = new Deadline(limits.MaxValidationTime);
        // Com extensões (ADR-0016) a validação pelo XSD vem depois, sem os elementos de extensão.
        var extended = source.Extensions.Count > 0;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        if (!extended)
        {
            settings.ValidationType = ValidationType.Schema;
            settings.Schemas = source.SchemaSet;
            // xs:unique/xs:key/xs:keyref valem (revisão da Fase 12, aviso 2); nada de schemaLocation nem resolução externa.
            settings.ValidationFlags = XmlSchemaValidationFlags.ProcessIdentityConstraints;
            settings.ValidationEventHandler += (_, e) => Report(problems, e);
        }

        XDocument document;
        try
        {
            using var reader = new LimitedXmlReader(open(settings), limits, deadline);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw TimeExceeded(limits.MaxValidationTime);
        }
        catch (XmlException ex)
        {
            // Malformado, DTD, limite de tamanho: posição + mensagem do parser.
            problems.Insert(0, new SourceXmlProblem(ex.LineNumber, ex.LinePosition, ex.Message));
            throw new SourceXmlException(problems);
        }

        var root = document.Root!;
        if (root.Name.LocalName != shape.RootElementName || (shape.TargetNamespace ?? "") != root.Name.NamespaceName)
        {
            problems.Insert(0, new SourceXmlProblem(Line(root), Position(root),
                $"Elemento raiz '{root.Name.LocalName}' não é o do contrato de origem ('{shape.RootElementName}')."));
        }

        if (problems.Count == 0 && extended)
        {
            var extensionElements = ValidateExtensions(document.Root!, source, problems, limits.MaxDepth);
            if (problems.Count == 0)
            {
                try
                {
                    ValidateBase(document, source, extensionElements, problems, deadline, limits.MaxValidationTime);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    throw TimeExceeded(limits.MaxValidationTime);
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new SourceXmlException(problems);
        }

        var tree = ConvertObject(root, shape.Fields, depth: 1, limits.MaxDepth);
        AddText(tree, root, shape.Fields); // raiz de conteúdo simples com atributos (revisão, aviso 5)
        return tree;
    }

    private static void Report(List<SourceXmlProblem> problems, ValidationEventArgs e)
    {
        if (e.Severity != XmlSeverityType.Error)
        {
            return;
        }

        problems.Add(new SourceXmlProblem(e.Exception?.LineNumber ?? 0, e.Exception?.LinePosition ?? 0, e.Message));
        if (problems.Count >= MaxReportedProblems)
        {
            // Já basta para o autor corrigir: não paga a validação do resto do documento.
            throw new SourceXmlException(problems);
        }
    }

    /// <summary>
    /// ADR-0016 §3, parte 1: elementos de extensão são validados pelas regras
    /// da extensão; devolve a posição de início de cada um para a parte 2
    /// pulá-los. Filhos agrupados uma vez por pai: custo linear no XML.
    /// </summary>
    private static HashSet<(int Line, int Position)> ValidateExtensions(XElement root, SourceSchema source, List<SourceXmlProblem> problems, int maxDepth)
    {
        var children = new Dictionary<XElement, ILookup<XName, XElement>>(ReferenceEqualityComparer.Instance);
        ILookup<XName, XElement> ChildrenOf(XElement parent) =>
            children.TryGetValue(parent, out var lookup) ? lookup : children[parent] = parent.Elements().ToLookup(e => e.Name);

        var found = new HashSet<(int Line, int Position)>();
        // Cada caminho é percorrido uma vez por pai, não uma vez por extensão (revisão da Fase 13, A2).
        foreach (var group in source.Extensions.GroupBy(e => string.Join('.', SourceExtensionMerger.Segments(e.ParentPath)), StringComparer.Ordinal))
        {
            var segments = SourceExtensionMerger.Segments(group.Key);
            // Extensão dentro de outra extensão já é validada pelas regras do bloco que a contém.
            if (!PathInBase(source.Shape.Fields, segments))
            {
                continue;
            }

            var nodes = group.Select(e => FindNode(source.Shape.Fields, segments, e.Node.Name)!).ToList();
            var byName = nodes.ToDictionary(n => n.ElementName);
            var required = nodes.Where(n => n.Required).ToList();
            IEnumerable<XElement> parents = [root];
            var fields = source.Shape.Fields;
            foreach (var segment in segments)
            {
                // Namespace real de cada passo (qualified, unqualified ou ref): revisão da Fase 13, R1.
                var step = fields.First(f => f.Name == segment);
                parents = parents.SelectMany(p => ChildrenOf(p)[step.ElementName]).ToList();
                fields = step.Children;
            }

            foreach (var parent in parents)
            {
                if (problems.Count >= MaxReportedProblems)
                {
                    return found;
                }

                var present = ChildrenOf(parent);
                foreach (var node in required.Where(n => !present.Contains(n.ElementName)))
                {
                    CheckOccurrences(node, [], parent, problems);
                }

                foreach (var occurrences in present)
                {
                    if (!byName.TryGetValue(occurrences.Key, out var node))
                    {
                        continue;
                    }

                    var list = occurrences.ToList();
                    CheckOccurrences(node, list, parent, problems);
                    foreach (var element in list)
                    {
                        ValidateExtensionElement(element, node, depth: 1, maxDepth, problems);
                        found.Add((Line(element), Position(element)));
                    }
                }
            }
        }

        return found;
    }

    /// <summary>
    /// ADR-0016 §3, parte 2: o restante pelo XSD base, numa passagem só,
    /// pulando as extensões; o documento não é alterado.
    /// </summary>
    private static SourceXmlException TimeExceeded(TimeSpan maxTime) =>
        new([new SourceXmlProblem(0, 0, $"Validação do XML de origem excedeu o tempo limite ({maxTime.TotalSeconds:0.#} s): o XSD de origem tem restrições caras (pattern, xs:unique/xs:key) para este volume.")]);

    private static void ValidateBase(XDocument document, SourceSchema source, HashSet<(int Line, int Position)> extensionElements, List<SourceXmlProblem> problems,
        Deadline deadline, TimeSpan maxTime)
    {
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = source.SchemaSet,
            ValidationFlags = XmlSchemaValidationFlags.ProcessIdentityConstraints,
            XmlResolver = null,
            DtdProcessing = DtdProcessing.Prohibit,
        };
        settings.ValidationEventHandler += (_, e) => Report(problems, e);
        using var reader = XmlReader.Create(new SkippingXmlReader(document.CreateReader(), extensionElements), settings);
        while (reader.Read())
        {
            if (deadline.Expired)
            {
                throw TimeExceeded(maxTime);
            }
        }
    }

    private static bool PathInBase(IReadOnlyList<SourceShapeNode> fields, IReadOnlyList<string> segments)
    {
        var current = fields;
        foreach (var segment in segments)
        {
            var next = current.FirstOrDefault(f => f.Name == segment);
            if (next is null || next.IsExtension)
            {
                return false;
            }

            current = next.Children;
        }

        return true;
    }

    private static SourceShapeNode? FindNode(IReadOnlyList<SourceShapeNode> fields, IReadOnlyList<string> segments, string name)
    {
        var current = fields;
        foreach (var segment in segments)
        {
            current = current.First(f => f.Name == segment).Children;
        }

        return current.FirstOrDefault(f => f.Name == name);
    }

    private static void CheckOccurrences(SourceShapeNode node, IReadOnlyList<XElement> occurrences, XElement parent, List<SourceXmlProblem> problems)
    {
        if (node.Required && occurrences.Count == 0)
        {
            Add(problems, parent, $"Falta o elemento '{node.Name}' (obrigatório no contrato de entrada) dentro de '{parent.Name.LocalName}'.");
        }

        if (!node.Repeated && occurrences.Count > 1)
        {
            Add(problems, occurrences[1], $"O elemento '{node.Name}' aparece mais de uma vez, mas não é repetível no contrato de entrada.");
        }
    }

    private static void ValidateExtensionElement(XElement element, SourceShapeNode node, int depth, int maxDepth, List<SourceXmlProblem> problems)
    {
        if (depth > maxDepth)
        {
            Add(problems, element, $"Bloco '{node.Name}' com profundidade acima de {maxDepth}.");
            return;
        }

        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration))
        {
            // Inclui xsi:nil/xsi:type: campo do contrato não é anulável nem muda de tipo.
            Add(problems, element, $"'{node.Name}' não aceita atributos (contrato de entrada).");
        }

        if (!node.IsObject)
        {
            if (element.HasElements)
            {
                Add(problems, element, $"'{node.Name}' é um campo e não pode ter elementos filhos.");
                return;
            }

            var text = element.Value.Trim();
            var invalid = node.ValueType switch
            {
                SourceValueType.Number => !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                SourceValueType.Date => !IsIsoDate(text),
                _ => false,
            };
            if (invalid && (node.Required || text.Length > 0))
            {
                Add(problems, element, $"'{node.Name}' deveria ser {(node.ValueType == SourceValueType.Number ? "número (ex.: 167.20)" : "data ISO (ex.: 2026-08-19)")}.");
            }

            return;
        }

        if (element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
        {
            Add(problems, element, $"O bloco '{node.Name}' não aceita texto, só elementos.");
        }

        var declared = node.Children.Select(c => c.ElementName).ToHashSet();
        foreach (var child in element.Elements())
        {
            if (!declared.Contains(child.Name))
            {
                Add(problems, child, $"'{child.Name.LocalName}' não está previsto no bloco '{node.Name}' do contrato de entrada.");
            }
        }

        var byName = element.Elements().ToLookup(e => e.Name);
        foreach (var childNode in node.Children)
        {
            var occurrences = byName[childNode.ElementName].ToList();
            CheckOccurrences(childNode, occurrences, element, problems);
            foreach (var occurrence in occurrences)
            {
                ValidateExtensionElement(occurrence, childNode, depth + 1, maxDepth, problems);
            }
        }
    }

    private static bool IsIsoDate(string text) =>
        DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-MM"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        || (text.Length > 10 && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && text[4] == '-' && text[10] == 'T');

    private static void Add(List<SourceXmlProblem> problems, XElement element, string message)
    {
        if (problems.Count < MaxReportedProblems)
        {
            problems.Add(new SourceXmlProblem(Line(element), Position(element), message));
        }
    }

    private static void AddText(Dictionary<string, object?> converted, XElement element, IReadOnlyList<SourceShapeNode> fields)
    {
        if (fields.FirstOrDefault(f => f.Name == SourceShapeNode.TextField) is { } textField)
        {
            converted[SourceShapeNode.TextField] = Scalar(element.Value, textField.ValueType);
        }
    }

    private static Dictionary<string, object?> ConvertObject(XElement element, IReadOnlyList<SourceShapeNode> fields, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new SourceXmlException([new SourceXmlProblem(Line(element), Position(element), $"XML de origem com profundidade acima de {maxDepth}.")]);
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != Xsi))
        {
            // Mesmo tipo que o editor mostra (revisão, aviso 4): atributo decimal chega como número.
            var name = SourceShapeNode.AttributePrefix + attribute.Name.LocalName;
            values[name] = Scalar(attribute.Value, fields.FirstOrDefault(f => f.Name == name)?.ValueType ?? SourceValueType.Text);
        }

        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
        {
            var field = fields.FirstOrDefault(f => f.Name == group.Key);
            var converted = group.Select(e => ConvertValue(e, field, depth + 1, maxDepth)).ToList();
            // Coleção pelo XSD (mesmo com um item só); sem declaração (xs:any), pela contagem.
            values[group.Key] = (field?.Repeated ?? converted.Count > 1) ? converted : converted[0];
        }

        return values;
    }

    private static object? ConvertValue(XElement element, SourceShapeNode? field, int depth, int maxDepth)
    {
        // xsi:nil fora de validação (xs:any lax/skip) pode ter qualquer texto: só "true"/"1" é nulo (revisão, aviso 1 — nunca 500).
        if (element.Attribute(Xsi + "nil")?.Value.Trim() is "true" or "1")
        {
            return null;
        }

        if (field?.IsObject ?? (element.HasElements || element.HasAttributes))
        {
            var fields = field?.Children ?? [];
            var converted = ConvertObject(element, fields, depth, maxDepth);
            AddText(converted, element, fields);
            return converted;
        }

        return Scalar(element.Value, field?.ValueType ?? SourceValueType.Text);
    }

    // Número em decimal (cultura invariante; já validado pelo XSD); INF/NaN de
    // float/double não cabem em decimal e ficam como texto. Data: texto ISO.
    private static object Scalar(string text, SourceValueType type) => type switch
    {
        SourceValueType.Number when decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
        SourceValueType.Number or SourceValueType.Date => text.Trim(),
        _ => text,
    };

    private static int Line(XElement element) => ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : 0;

    private static int Position(XElement element) => ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LinePosition : 0;
}
