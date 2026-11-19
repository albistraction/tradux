using System.Text;
using System.Text.RegularExpressions;

namespace Tradux.Application.Quality;

/// <summary>Pattern do XSD fora do subconjunto que o gerador entende (ex.: <c>\p{..}</c>, <c>\i</c>, subtração de classe).</summary>
public sealed class UnsupportedPatternException(string message) : Exception(message);

/// <summary>
/// Gera texto que casa com um pattern do XSD (ADR-0011) — o subconjunto
/// usado em layouts fiscais: literais e escapes, <c>.</c>, <c>\d \w \s</c>
/// (e negações), classes com faixas e negação, grupos, alternância e
/// quantificadores <c>? * + {n} {n,} {n,m}</c>. Dois modos: o menor valor
/// (limite inferior de cada quantificador, primeira alternativa) e o maior
/// (limite superior, limitado, última alternativa). Determinístico. Fora do
/// subconjunto lança <see cref="UnsupportedPatternException"/> — o chamador
/// registra "não coberto" em vez de inventar valor.
/// </summary>
public static class PatternValueGenerator
{
    private const int OpenEndedExtra = 2;

    /// <summary>
    /// Teto do valor gerado (revisão da Fase 8, B3): repetição aninhada como
    /// <c>((d{100}){100}){1000}</c> não pode alocar centenas de MB — acima do
    /// teto o pattern é "não coberto".
    /// </summary>
    public const int MaxGeneratedLength = SyntheticValues.MaxTextLength;

    /// <summary>
    /// Orçamento de passos por geração (2ª revisão da Fase 8): repetição de
    /// conteúdo que não emite nada — <c>(a?){2000000000}</c> no modo mínimo —
    /// não pode prender a CPU. Cada iteração de repetição e cada caractere
    /// gasta um passo; acima do orçamento o pattern é "não coberto".
    /// </summary>
    public const int MaxSteps = MaxGeneratedLength * 16;

    public static string Generate(string pattern, bool maximal)
    {
        var parser = new Parser(pattern);
        var node = parser.ParseAlternation();
        if (!parser.AtEnd)
        {
            throw new UnsupportedPatternException($"Pattern '{pattern}': caractere inesperado na posição {parser.Position}.");
        }

        var output = new Output();
        node.Emit(output, maximal);
        return output.Text.ToString();
    }

    /// <summary>Verifica se o valor casa com todos os passos (cada passo: basta um pattern) — padrão XSD é ancorado.</summary>
    /// <exception cref="RegexMatchTimeoutException">Avaliação lenta, ou prazo da suíte esgotado (valor fica indisponível).</exception>
    public static bool Matches(string value, IReadOnlyList<IReadOnlyList<string>> patternSteps)
    {
        if (Engine.Validation.GenerationDeadline.Expired)
        {
            throw new RegexMatchTimeoutException(value, "(prazo da suíte esgotado)", TimeSpan.Zero);
        }

        return patternSteps.All(step => step.Any(p => Anchored(p).IsMatch(value)));
    }

    private static Regex Anchored(string pattern) =>
        new($"^(?:{pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private abstract class Node
    {
        public abstract void Emit(Output output, bool maximal);
    }

    private sealed class Sequence(List<Node> items) : Node
    {
        public override void Emit(Output output, bool maximal)
        {
            foreach (var item in items)
            {
                item.Emit(output, maximal);
            }
        }
    }

    private sealed class Alternation(List<Node> options) : Node
    {
        public override void Emit(Output output, bool maximal) => options[maximal ? ^1 : 0].Emit(output, maximal);
    }

    private sealed class Repeat(Node inner, int min, int? max) : Node
    {
        public override void Emit(Output output, bool maximal)
        {
            var count = maximal ? max ?? min + OpenEndedExtra : min;
            for (var i = 0; i < count; i++)
            {
                output.Step();
                inner.Emit(output, maximal);
            }
        }
    }

    private sealed class CharNode(char value) : Node
    {
        public override void Emit(Output output, bool maximal)
        {
            output.Step();
            if (output.Text.Length >= MaxGeneratedLength)
            {
                throw new UnsupportedPatternException($"valor gerado passaria de {MaxGeneratedLength} caracteres.");
            }

            output.Text.Append(value);
        }
    }

    private sealed class Output
    {
        private int _steps;

        public StringBuilder Text { get; } = new();

        public void Step()
        {
            if (++_steps > MaxSteps)
            {
                throw new UnsupportedPatternException($"geração passaria de {MaxSteps} passos (repetição grande demais).");
            }
        }
    }

    private sealed class Parser(string pattern)
    {
        // Candidatos, em ordem de preferência, para classes de caractere:
        // dígito primeiro (casa com mais layouts fiscais), depois letras.
        private const string Candidates = "1023456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz -_./:@";

        private int _position;

        public int Position => _position;

        public bool AtEnd => _position >= pattern.Length;

        private char Peek => pattern[_position];

        public Node ParseAlternation()
        {
            var options = new List<Node> { ParseSequence() };
            while (!AtEnd && Peek == '|')
            {
                _position++;
                options.Add(ParseSequence());
            }

            return options.Count == 1 ? options[0] : new Alternation(options);
        }

        private Node ParseSequence()
        {
            var items = new List<Node>();
            while (!AtEnd && Peek != '|' && Peek != ')')
            {
                items.Add(ParseQuantified(ParseAtom()));
            }

            return new Sequence(items);
        }

        private Node ParseAtom()
        {
            var c = pattern[_position++];
            switch (c)
            {
                case '(':
                    var inner = ParseAlternation();
                    Expect(')');
                    return inner;
                case '[':
                    return new CharNode(ParseClass());
                case '.':
                    return new CharNode('1');
                case '\\':
                    return new CharNode(ParseEscape(inClass: false));
                case '*' or '+' or '?' or '{' or ')':
                    throw Unsupported($"quantificador ou fechamento sem átomo '{c}'");
                default:
                    return new CharNode(c);
            }
        }

        private Node ParseQuantified(Node atom)
        {
            if (AtEnd)
            {
                return atom;
            }

            switch (Peek)
            {
                case '?':
                    _position++;
                    return new Repeat(atom, 0, 1);
                case '*':
                    _position++;
                    return new Repeat(atom, 0, null);
                case '+':
                    _position++;
                    return new Repeat(atom, 1, null);
                case '{':
                    _position++;
                    var min = ReadInt();
                    int? max = min;
                    if (!AtEnd && Peek == ',')
                    {
                        _position++;
                        max = !AtEnd && Peek == '}' ? null : ReadInt();
                    }

                    Expect('}');
                    return new Repeat(atom, min, max);
                default:
                    return atom;
            }
        }

        private int ReadInt()
        {
            var start = _position;
            while (!AtEnd && char.IsAsciiDigit(Peek))
            {
                _position++;
            }

            if (start == _position)
            {
                throw Unsupported("número esperado no quantificador");
            }

            return int.Parse(pattern.Substring(start, _position - start), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Classe [..]: acha o primeiro candidato que casa com a própria classe,
        // conferido pelo Regex do .NET (mesma semântica para este subconjunto).
        private char ParseClass()
        {
            var start = _position - 1;
            var depth = 0;
            while (!AtEnd)
            {
                var c = pattern[_position++];
                if (c == '\\')
                {
                    if (AtEnd)
                    {
                        break;
                    }

                    var escaped = pattern[_position];
                    if (escaped is 'p' or 'P' or 'i' or 'I' or 'c' or 'C')
                    {
                        throw Unsupported($"escape '\\{escaped}' em classe");
                    }

                    _position++;
                    continue;
                }

                if (c == '[')
                {
                    depth++; // subtração de classe (XSD "[a-z-[aeiou]]")
                    throw Unsupported("subtração de classe");
                }

                if (c == ']' && depth == 0)
                {
                    var classText = pattern[start.._position];
                    var regex = new Regex($"^{classText}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
                    foreach (var candidate in Candidates)
                    {
                        if (regex.IsMatch(candidate.ToString()))
                        {
                            return candidate;
                        }
                    }

                    throw Unsupported($"classe '{classText}' sem caractere candidato");
                }
            }

            throw Unsupported("classe sem ']'");
        }

        private char ParseEscape(bool inClass)
        {
            if (AtEnd)
            {
                throw Unsupported("escape no fim");
            }

            var c = pattern[_position++];
            return c switch
            {
                'd' => '1',
                'D' or 'w' => 'A',
                'W' => '-',
                's' => ' ',
                'S' => 'A',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'p' or 'P' or 'i' or 'I' or 'c' or 'C' => throw Unsupported($"escape '\\{c}'"),
                _ when char.IsAsciiLetterOrDigit(c) && !inClass => throw Unsupported($"escape '\\{c}'"),
                _ => c,
            };
        }

        private void Expect(char expected)
        {
            if (AtEnd || Peek != expected)
            {
                throw Unsupported($"'{expected}' esperado");
            }

            _position++;
        }

        private UnsupportedPatternException Unsupported(string reason) =>
            new($"Pattern '{pattern}' fora do subconjunto suportado: {reason}.");
    }
}
