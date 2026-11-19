using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Tradux.Knowledge;

/// <summary>
/// Política de chunking por tipo documental (ADR-0013 §9;
/// docs/rag/CHUNKING_POLICY.md). Determinística: mesmo conteúdo, mesmos
/// chunks e mesmos hashes.
/// </summary>
public static partial class Chunker
{
    public const int MaxChunkChars = 1200;

    public static IReadOnlyList<KnowledgeChunk> Chunk(KnowledgeSource source)
    {
        var pieces = source.SourceType switch
        {
            SourceTypes.Glossary => Glossary(source.Content),
            SourceTypes.Xsd => Xsd(source.Content),
            SourceTypes.OperatorCatalog => Operators(source.Content),
            _ => Markdown(source.Content),
        };

        return pieces
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select((p, index) =>
            {
                var text = p.Text.Trim();
                var metadata = new ChunkMetadata(
                    source.SourceId, source.SourceType, source.Version, source.TenantScope, source.AccessLevel, source.Provider,
                    source.Municipality, source.Operation, source.SchemaVersion, source.Language, KnowledgeHash.Of(p.Anchor + "\n" + text),
                    source.EffectiveDate, p.Anchor);
                return new KnowledgeChunk($"{source.SourceId}#{index + 1}", text, metadata);
            })
            .ToList();
    }

    private sealed record Piece(string Anchor, string Text);

    // Markdown: por seção (#, ##, ###), âncora = caminho de títulos; seção longa
    // é dividida por parágrafos com sobreposição de um parágrafo.
    private static IEnumerable<Piece> Markdown(string content)
    {
        var headings = new List<string>();
        var body = new StringBuilder();
        var pieces = new List<Piece>();

        void Flush()
        {
            var anchor = headings.Count == 0 ? "(início)" : string.Join(" > ", headings);
            foreach (var part in SplitLong(body.ToString()))
            {
                pieces.Add(new Piece(anchor, (headings.Count > 0 ? headings[^1] + "\n" : "") + part));
            }

            body.Clear();
        }

        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var match = Heading().Match(line);
            if (match.Success && match.Groups[1].Value.Length <= 3)
            {
                Flush();
                var level = match.Groups[1].Value.Length;
                while (headings.Count >= level)
                {
                    headings.RemoveAt(headings.Count - 1);
                }

                headings.Add(match.Groups[2].Value.Trim());
                continue;
            }

            body.AppendLine(line);
        }

        Flush();
        return pieces;
    }

    private static IEnumerable<string> SplitLong(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return text;
            yield break;
        }

        var paragraphs = Regex.Split(text, @"\n\s*\n").Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var current = new StringBuilder();
        string? previous = null;
        foreach (var paragraph in paragraphs)
        {
            if (current.Length > 0 && current.Length + paragraph.Length > MaxChunkChars)
            {
                yield return current.ToString();
                current.Clear();
                if (previous is not null && previous.Length < MaxChunkChars / 2)
                {
                    current.AppendLine(previous).AppendLine(); // sobreposição
                }
            }

            current.AppendLine(paragraph).AppendLine();
            previous = paragraph;
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    // Glossário: um chunk por termo ("- **Termo**: definição", com continuação indentada).
    private static IEnumerable<Piece> Glossary(string content)
    {
        var pieces = new List<Piece>();
        string? term = null;
        var text = new StringBuilder();
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            var match = GlossaryTerm().Match(line);
            if (match.Success)
            {
                if (term is not null)
                {
                    pieces.Add(new Piece(term, text.ToString()));
                }

                term = match.Groups[1].Value.Trim();
                text.Clear().AppendLine(line.TrimStart('-', ' '));
            }
            else if (term is not null && line.StartsWith("  ", StringComparison.Ordinal))
            {
                text.AppendLine(line.Trim());
            }
            else if (term is not null)
            {
                pieces.Add(new Piece(term, text.ToString()));
                term = null;
                text.Clear();
            }
        }

        if (term is not null)
        {
            pieces.Add(new Piece(term, text.ToString()));
        }

        return pieces.Count > 0 ? pieces : Markdown(content);
    }

    // XSD: por declaração de topo, com a documentação e as restrições.
    private static IEnumerable<Piece> Xsd(string content)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return [new Piece("(xsd inválido)", content[..Math.Min(content.Length, MaxChunkChars)])];
        }

        return document.Root?.Elements()
            .Where(e => e.Name.LocalName is "element" or "simpleType" or "complexType" or "group" or "attributeGroup")
            .Select(e =>
            {
                var name = e.Attribute("name")?.Value ?? "(anônimo)";
                var text = new StringBuilder($"xs:{e.Name.LocalName} {name}\n");
                foreach (var doc in e.Descendants().Where(d => d.Name.LocalName == "documentation"))
                {
                    text.AppendLine(Regex.Replace(doc.Value, @"\s+", " ").Trim());
                }

                foreach (var child in e.Descendants().Where(d => d.Attribute("name") is not null || d.Attribute("value") is not null || d.Attribute("base") is not null))
                {
                    text.Append(child.Name.LocalName).Append(' ')
                        .AppendLine(string.Join(" ", child.Attributes().Where(a => a.Name.LocalName is "name" or "type" or "value" or "base" or "minOccurs" or "maxOccurs" or "nillable")
                            .Select(a => $"{a.Name.LocalName}={a.Value}")));
                }

                var body = text.ToString();
                return new Piece($"{e.Name.LocalName}:{name}", body.Length > MaxChunkChars * 2 ? body[..(MaxChunkChars * 2)] : body);
            }) ?? [];
    }

    // Catálogo de operadores: um chunk por operador (JSON gerado do catálogo tipado).
    private static IEnumerable<Piece> Operators(string content)
    {
        using var document = JsonDocument.Parse(content);
        foreach (var op in document.RootElement.EnumerateArray())
        {
            var name = op.GetProperty("name").GetString()!;
            var text = new StringBuilder($"operador {name} ({op.GetProperty("category").GetString()})\n{op.GetProperty("description").GetString()}\n");
            if (op.TryGetProperty("parameters", out var parameters))
            {
                foreach (var p in parameters.EnumerateArray())
                {
                    text.AppendLine($"parâmetro {p.GetProperty("name").GetString()} ({p.GetProperty("kind").GetString()}): {p.GetProperty("description").GetString()}");
                }
            }

            yield return new Piece($"operador:{name}", text.ToString());
        }
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^-\s+\*\*(.+?)\*\*")]
    private static partial Regex GlossaryTerm();
}
