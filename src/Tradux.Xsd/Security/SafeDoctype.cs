using System.Text;

namespace Tradux.Xsd.Security;

/// <summary>
/// Remove, de forma restrita, o <c>&lt;!DOCTYPE&gt;</c> de um XSD enviado —
/// o caso do <c>xmldsig-core-schema.xsd</c> oficial do W3C, que acompanha
/// quase todo pacote de layout fiscal e só declara entidades internas com texto
/// literal (<c>&amp;dsig;</c> = URL do namespace). Aceita apenas:
/// identificador externo (ignorado — nunca buscado), <c>ATTLIST</c>
/// (ignorado), comentários e <c>ENTITY</c> geral ou de parâmetro com valor
/// literal sem <c>&amp;</c>, <c>%</c> ou <c>&lt;</c> (sem aninhamento: nada de
/// "billion laughs"); entidades predefinidas (<c>lt</c>, <c>amp</c>…) não
/// podem ser redeclaradas. As referências <c>&amp;nome;</c> do documento são
/// trocadas pelo literal — fora de comentários e CDATA — com o limite de
/// tamanho conferido <b>durante</b> a expansão (nunca monta mais que o limite
/// em memória). Qualquer outra coisa — entidade <c>SYSTEM</c>/<c>PUBLIC</c>,
/// referência a entidade de parâmetro, <c>ELEMENT</c>, <c>NOTATION</c>, PI,
/// codificação diferente de UTF-8/ISO-8859-1 — continua recusada (XXE,
/// seção 11). Leitura linear, sem regex. O resultado passa pela carga com
/// <c>DtdProcessing.Prohibit</c> como qualquer outro arquivo.
/// </summary>
internal static class SafeDoctype
{
    private const string Marker = "<!DOCTYPE";
    private static readonly HashSet<string> Predefined = new(StringComparer.Ordinal) { "lt", "gt", "amp", "apos", "quot" };

    /// <returns>Os bytes sem DOCTYPE (ou os mesmos, se não havia).</returns>
    /// <exception cref="InvalidOperationException">DOCTYPE fora do caso permitido ou resultado acima do limite.</exception>
    public static byte[] Normalize(byte[] content, string name, long maxBytes)
    {
        // Busca do marcador em leitura compatível com ASCII: sem DOCTYPE, nada muda.
        if (!Encoding.Latin1.GetString(content).Contains(Marker, StringComparison.Ordinal))
        {
            return content;
        }

        var encoding = SupportedEncoding(content, name);
        string text;
        try
        {
            text = encoding.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            // Bytes que não são UTF-8 válido num arquivo com DOCTYPE: recusa (nunca troca por U+FFFD em silêncio).
            throw Refused(name, "bytes inválidos para a codificação declarada");
        }

        var start = text.IndexOf(Marker, StringComparison.Ordinal);
        RequirePrologOnly(text, start, name);
        var reader = new Reader(text, name);
        reader.Position = start + Marker.Length;
        var subsetEnd = ScanDoctype(reader, out var subsetStart);
        var entities = subsetStart < 0 ? [] : ParseSubset(text, subsetStart, subsetEnd, name);

        var output = new StringBuilder(text.Length);
        output.Append(text, 0, start);
        Expand(text, reader.Position + 1, entities, output, maxBytes, name);
        var result = encoding.GetBytes(output.ToString());
        if (result.LongLength > maxBytes)
        {
            throw TooLarge(name, maxBytes);
        }

        return result;
    }

    // Só o DOCTYPE do prólogo vale: antes dele, apenas BOM, declaração XML, espaços, comentários e PIs.
    private static void RequirePrologOnly(string text, int start, string name)
    {
        var i = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        while (i < start)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (string.CompareOrdinal(text, i, "<?", 0, 2) == 0)
            {
                var close = text.IndexOf("?>", i + 2, StringComparison.Ordinal);
                i = close < 0 || close > start ? throw Refused(name, "DOCTYPE fora do prólogo") : close + 2;
            }
            else if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                var close = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = close < 0 || close > start ? throw Refused(name, "DOCTYPE fora do prólogo") : close + 3;
            }
            else
            {
                throw Refused(name, "DOCTYPE fora do prólogo");
            }
        }
    }

    /// <summary>Do nome do DOCTYPE até o '&gt;' final; devolve o fim do subconjunto interno (ou -1).</summary>
    private static int ScanDoctype(Reader reader, out int subsetStart)
    {
        subsetStart = -1;
        var subsetEnd = -1;
        while (true)
        {
            var c = reader.Next("DOCTYPE sem fim");
            if (c is '"' or '\'')
            {
                reader.SkipQuoted(c);
            }
            else if (c == '[' && subsetStart < 0)
            {
                subsetStart = reader.Position + 1;
                subsetEnd = FindSubsetEnd(reader);
                reader.Position = subsetEnd;
            }
            else if (c == '>')
            {
                return subsetEnd;
            }
        }
    }

    private static int FindSubsetEnd(Reader reader)
    {
        reader.Position++;
        while (true)
        {
            var c = reader.Peek("subconjunto interno do DOCTYPE sem fim");
            if (c is '"' or '\'')
            {
                reader.SkipQuoted(c);
                reader.Position++;
            }
            else if (reader.StartsWith("<!--"))
            {
                reader.SkipPast("-->", "comentário sem fim no DOCTYPE");
            }
            else if (c == ']')
            {
                return reader.Position;
            }
            else
            {
                reader.Position++;
            }
        }
    }

    // Subconjunto: só comentário, ATTLIST e ENTITY interna literal. Qualquer outra coisa é recusada.
    private static Dictionary<string, string> ParseSubset(string text, int start, int end, string name)
    {
        var entities = new Dictionary<string, string>(StringComparer.Ordinal);
        var reader = new Reader(text[start..end], name) { Position = 0 };
        while (true)
        {
            reader.SkipWhitespace();
            if (reader.AtEnd)
            {
                return entities;
            }

            if (reader.StartsWith("<!--"))
            {
                reader.SkipPast("-->", "comentário sem fim no DOCTYPE");
            }
            else if (reader.StartsWith("<!ATTLIST"))
            {
                reader.Position += "<!ATTLIST".Length;
                reader.SkipToClose();
            }
            else if (reader.StartsWith("<!ENTITY"))
            {
                reader.Position += "<!ENTITY".Length;
                var (entity, value, parameter) = ParseEntity(reader);
                if (!parameter)
                {
                    entities[entity] = value; // entidade de parâmetro literal: declarada, nunca usada fora do DTD
                }
            }
            else
            {
                throw Refused(name, "declaração não permitida no DOCTYPE (só ENTITY interna literal, ATTLIST e comentários)");
            }
        }
    }

    private static (string Name, string Value, bool Parameter) ParseEntity(Reader reader)
    {
        reader.RequireWhitespace();
        var parameter = false;
        if (reader.Peek("ENTITY incompleta") == '%')
        {
            parameter = true;
            reader.Position++;
            reader.RequireWhitespace();
        }

        var entity = reader.ReadName();
        if (Predefined.Contains(entity))
        {
            throw reader.Refuse($"entidade predefinida '{entity}' redeclarada");
        }

        reader.RequireWhitespace();
        var quote = reader.Peek("ENTITY incompleta");
        if (quote is not ('"' or '\''))
        {
            throw reader.Refuse($"entidade '{entity}' externa (SYSTEM/PUBLIC) ou sem valor literal");
        }

        var valueStart = reader.Position + 1;
        reader.SkipQuoted(quote);
        var value = reader.Text[valueStart..reader.Position];
        reader.Position++;
        if (value.IndexOfAny(['&', '%', '<', '>', '"', '\'']) >= 0)
        {
            throw reader.Refuse($"entidade '{entity}' com referência, aspas ou marcação no valor");
        }

        reader.SkipWhitespace();
        if (reader.Peek("ENTITY incompleta") != '>')
        {
            throw reader.Refuse($"entidade '{entity}' com conteúdo além do valor");
        }

        reader.Position++;
        return (entity, value, parameter);
    }

    // Copia o documento trocando &nome; declarado pelo literal; comentários e CDATA ficam como estão.
    private static void Expand(string text, int from, Dictionary<string, string> entities, StringBuilder output, long maxBytes, string name)
    {
        var lookup = entities.GetAlternateLookup<ReadOnlySpan<char>>();
        var i = from;
        while (i < text.Length)
        {
            if (output.Length > maxBytes)
            {
                throw TooLarge(name, maxBytes);
            }

            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0 || string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                var closer = text[i + 2] == '-' ? "-->" : "]]>";
                var close = text.IndexOf(closer, i, StringComparison.Ordinal);
                var stop = close < 0 ? text.Length : close + closer.Length;
                Append(output, text, i, stop - i, maxBytes, name);
                i = stop;
                continue;
            }

            if (text[i] == Marker[0] && string.CompareOrdinal(text, i, Marker, 0, Marker.Length) == 0)
            {
                throw Refused(name, "mais de um DOCTYPE");
            }

            if (text[i] == '&')
            {
                var nameEnd = i + 1;
                while (nameEnd < text.Length && nameEnd - i <= 64 && (char.IsAsciiLetterOrDigit(text[nameEnd]) || text[nameEnd] is '_' or '.' or '-'))
                {
                    nameEnd++;
                }

                // Sem alocar por '&': a busca usa a fatia do texto (só nomes simples terminados em ';').
                if (nameEnd > i + 1 && nameEnd < text.Length && text[nameEnd] == ';'
                    && lookup.TryGetValue(text.AsSpan(i + 1, nameEnd - i - 1), out var value))
                {
                    Append(output, value, 0, value.Length, maxBytes, name);
                    i = nameEnd + 1;
                    continue;
                }
            }

            output.Append(text[i]);
            i++;
        }
    }

    private static void Append(StringBuilder output, string source, int start, int count, long maxBytes, string name)
    {
        if (output.Length + (long)count > maxBytes)
        {
            throw TooLarge(name, maxBytes); // antes de alocar: nada de expansão acima do limite em memória
        }

        output.Append(source, start, count);
    }

    // Com DOCTYPE só UTF-8 (com ou sem BOM) ou ISO-8859-1 — outra codificação seria regravada com perda.
    private static Encoding SupportedEncoding(byte[] content, string name)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        }

        var head = Encoding.Latin1.GetString(content, 0, Math.Min(content.Length, 200)).TrimStart();
        var declared = DeclaredEncoding(head);
        return declared switch
        {
            null => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            _ when declared.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || declared.Equals("utf8", StringComparison.OrdinalIgnoreCase)
                => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            _ when declared.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Equals("ISO88591", StringComparison.OrdinalIgnoreCase)
                => Encoding.Latin1,
            _ => throw Refused(name, $"codificação '{declared}' não suportada em arquivo com DOCTYPE (use UTF-8 ou ISO-8859-1)"),
        };
    }

    private static string? DeclaredEncoding(string head)
    {
        if (!head.StartsWith("<?xml", StringComparison.Ordinal))
        {
            return null;
        }

        var end = head.IndexOf("?>", StringComparison.Ordinal);
        var declaration = end < 0 ? head : head[..end];
        var at = declaration.IndexOf("encoding", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var quote = declaration.IndexOfAny(['"', '\''], at);
        var close = quote < 0 ? -1 : declaration.IndexOf(declaration[quote], quote + 1);
        return close < 0 ? null : declaration[(quote + 1)..close];
    }

    private static InvalidOperationException TooLarge(string name, long maxBytes) =>
        new($"'{name}' excede o limite de {maxBytes} bytes ao expandir as entidades do DOCTYPE.");

    private static InvalidOperationException Refused(string name, string reason) =>
        new($"'{name}' contém DOCTYPE não permitido ({reason}) — mitigação de XXE, seção 11 do requisito de origem.");

    /// <summary>Leitor linear com posição; toda falha vira recusa com mensagem clara.</summary>
    private sealed class Reader(string text, string name)
    {
        public string Text { get; } = text;
        public int Position { get; set; }
        public bool AtEnd => Position >= Text.Length;

        public char Next(string missing)
        {
            Position++;
            return Peek(missing);
        }

        public char Peek(string missing) => AtEnd ? throw Refuse(missing) : Text[Position];

        public bool StartsWith(string value) => string.CompareOrdinal(Text, Position, value, 0, value.Length) == 0;

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(Text[Position]))
            {
                Position++;
            }
        }

        public void RequireWhitespace()
        {
            if (AtEnd || !char.IsWhiteSpace(Text[Position]))
            {
                throw Refuse("declaração malformada");
            }

            SkipWhitespace();
        }

        public string ReadName()
        {
            var start = Position;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(Text[Position]) || Text[Position] is '_' or '.' or '-'))
            {
                Position++;
            }

            return Position == start || !(char.IsAsciiLetter(Text[start]) || Text[start] == '_')
                ? throw Refuse("nome de entidade inválido")
                : Text[start..Position];
        }

        // Posiciona na aspa de fechamento.
        public void SkipQuoted(char quote)
        {
            var close = Text.IndexOf(quote, Position + 1);
            Position = close < 0 ? throw Refuse("texto entre aspas sem fim") : close;
        }

        public void SkipPast(string closer, string missing)
        {
            var close = Text.IndexOf(closer, Position + closer.Length, StringComparison.Ordinal);
            Position = close < 0 ? throw Refuse(missing) : close + closer.Length;
        }

        // Até o '>' que fecha a declaração, respeitando aspas.
        public void SkipToClose()
        {
            while (true)
            {
                var c = Peek("declaração sem fim");
                if (c is '"' or '\'')
                {
                    SkipQuoted(c);
                }
                else if (c == '>')
                {
                    Position++;
                    return;
                }
                else if (c == '<')
                {
                    throw Refuse("declaração malformada");
                }

                Position++;
            }
        }

        public InvalidOperationException Refuse(string reason) => Refused(name, reason);
    }
}
