using System.Globalization;
using System.Xml;
using System.Xml.Schema;

namespace Tradux.Xsd.Security;

/// <summary>
/// Recusa, na importação, patterns que tornariam a validação XSD cara
/// demais (ADR-0012 §9): o validador do .NET não tem timeout de regex e roda
/// em toda geração. Bloqueia quantificador acima do limite
/// (<c>(a?){2000000000}</c>), pattern longo demais e o formato clássico de
/// retrocesso exponencial — grupo com quantificador interno repetido por
/// quantificador ilimitado (<c>(a*)*</c>, <c>(a+)+</c>, <c>(\d{1,3})*</c>).
/// Heurística conservadora: não pega toda regex patológica (ex.:
/// alternativas sobrepostas), mas nenhum layout fiscal real usa esses formatos.
/// </summary>
public static class PatternSafetyChecker
{
    private const string XsdNamespace = "http://www.w3.org/2001/XMLSchema";

    /// <exception cref="XmlSchemaException">Algum pattern viola os limites.</exception>
    public static void Check(XmlSchemaSet schemaSet, XsdImportSecurityOptions options)
    {
        var visited = new HashSet<XmlSchema>(ReferenceEqualityComparer.Instance);
        foreach (var schema in schemaSet.Schemas().OfType<XmlSchema>())
        {
            Visit(schema, options, visited);
        }
    }

    private static void Visit(XmlSchema schema, XsdImportSecurityOptions options, HashSet<XmlSchema> visited)
    {
        if (!visited.Add(schema))
        {
            return;
        }

        using var buffer = new MemoryStream();
        schema.Write(buffer);
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "pattern" && reader.NamespaceURI == XsdNamespace
                && reader.GetAttribute("value") is { } pattern
                && Problem(pattern, options) is { } problem)
            {
                throw new XmlSchemaException($"Pattern recusado por segurança ({problem}): '{Shorten(pattern)}'.");
            }
        }

        foreach (var external in schema.Includes.OfType<XmlSchemaExternal>())
        {
            if (external.Schema is { } nested)
            {
                Visit(nested, options, visited);
            }
        }
    }

    /// <summary>Motivo da recusa, ou nulo se o pattern é aceito.</summary>
    public static string? Problem(string pattern, XsdImportSecurityOptions options)
    {
        if (pattern.Length > options.MaxPatternLength)
        {
            return $"mais de {options.MaxPatternLength} caracteres";
        }

        // Pilha de grupos: se o grupo tem quantificador VARIÁVEL dentro (?, *, +,
        // {n,m} com n ≠ m) — repetir isso (máximo > 1) cria caminhos ambíguos:
        // (a*){1,1000}, (a?){1000}, (.*a){20} (2ª revisão da Fase 9, B1).
        // Contagem fixa dentro, como (\d{3}\.){2}, não é ambígua e passa.
        var groups = new Stack<bool>();
        var insideCurrent = false;
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inClass)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == ']')
                {
                    inClass = false;
                    if (AtomQuantifier(pattern, ref i, options, ref insideCurrent) is { } classProblem)
                    {
                        return classProblem;
                    }
                }

                continue;
            }

            switch (c)
            {
                case '\\':
                    // Escape (\d, \., \p{..}) é um átomo: o quantificador vem depois dele.
                    i++;
                    if (i < pattern.Length && pattern[i] is 'p' or 'P' && i + 1 < pattern.Length && pattern[i + 1] == '{')
                    {
                        i = Math.Max(i, pattern.IndexOf('}', i));
                    }

                    if (AtomQuantifier(pattern, ref i, options, ref insideCurrent) is { } escapeProblem)
                    {
                        return escapeProblem;
                    }

                    break;
                case '[':
                    inClass = true;
                    break;
                case '(':
                    groups.Push(insideCurrent);
                    insideCurrent = false;
                    break;
                case ')':
                {
                    var innerQuantified = insideCurrent;
                    insideCurrent = groups.Count > 0 ? groups.Pop() : false;
                    var (length, unbounded, max) = Quantifier(pattern, i + 1);
                    if (max > options.MaxPatternQuantifier)
                    {
                        return $"quantificador acima de {options.MaxPatternQuantifier}";
                    }

                    if (innerQuantified && (unbounded || max > 1))
                    {
                        return "repetição sobre grupo com quantificador variável (retrocesso exponencial)";
                    }

                    if (IsVariable(pattern, i + 1, length) || innerQuantified)
                    {
                        insideCurrent = true;
                    }

                    i += length;
                    break;
                }

                default:
                    if (AtomQuantifier(pattern, ref i, options, ref insideCurrent) is { } atomProblem)
                    {
                        return atomProblem;
                    }

                    break;
            }
        }

        return null;
    }

    // Quantificador logo após um átomo terminado em "i": valida e avança.
    private static string? AtomQuantifier(string pattern, ref int i, XsdImportSecurityOptions options, ref bool insideCurrent)
    {
        var (length, _, max) = Quantifier(pattern, i + 1);
        if (max > options.MaxPatternQuantifier)
        {
            return $"quantificador acima de {options.MaxPatternQuantifier}";
        }

        if (IsVariable(pattern, i + 1, length))
        {
            insideCurrent = true;
        }

        i += length;
        return null;
    }

    // Quantificador variável: ?, *, + ou {n,m}/{n,} com mínimo diferente do máximo.
    private static bool IsVariable(string pattern, int start, int length)
    {
        if (length == 0)
        {
            return false;
        }

        if (pattern[start] != '{')
        {
            return true;
        }

        var parts = pattern[(start + 1)..(start + length - 1)].Split(',');
        return parts.Length == 2 && parts[0] != parts[1];
    }

    // Quantificador que começa em "start": tamanho, se é ilimitado e o maior número citado.
    private static (int Length, bool Unbounded, long Max) Quantifier(string pattern, int start)
    {
        if (start >= pattern.Length)
        {
            return (0, false, 0);
        }

        switch (pattern[start])
        {
            case '*' or '+':
                return (1, true, 0);
            case '?':
                return (1, false, 1);
            case '{':
                var end = pattern.IndexOf('}', start);
                if (end < 0)
                {
                    return (0, false, 0);
                }

                var parts = pattern[(start + 1)..end].Split(',');
                long max = 0;
                foreach (var part in parts.Where(p => p.Length > 0))
                {
                    max = Math.Max(max, long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue);
                }

                var unbounded = parts.Length == 2 && parts[1].Length == 0;
                return (end - start + 1, unbounded, max);
            default:
                return (0, false, 0);
        }
    }

    private static string Shorten(string pattern) => pattern.Length <= 80 ? pattern : pattern[..80] + "…";
}
