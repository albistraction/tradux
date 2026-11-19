using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Tradux.Engine.Validation;

/// <summary>
/// Pré-checagem dos patterns do XSD com timeout explícito (2ª revisão da
/// Fase 9, B1). O validador XSD do .NET avalia patterns com o timeout
/// padrão do processo — infinito, a menos que o host o fixe antes de
/// qualquer Regex existir. Aqui o mesmo motor de regex do .NET avalia o
/// valor com timeout próprio: se estoura, o valor faria o validador
/// travar — a geração falha (<c>XSD_VALIDATION_TIMEOUT</c>) sem chegar a
/// validar. Pattern com sintaxe só do XSD (ex.: <c>\i</c>) não compila
/// como .NET e fica sem pré-checagem (o timeout global do host cobre).
/// </summary>
public static class PatternGuard
{
    private static readonly ConcurrentDictionary<string, Regex?> Compiled = new(StringComparer.Ordinal);

    /// <summary>Tempo máximo por pattern e valor.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Falso = a avaliação de algum pattern estourou o tempo para este valor.</summary>
    public static bool CompletesInTime(string value, IReadOnlyList<IReadOnlyList<string>> patternSteps)
    {
        if (GenerationDeadline.Expired)
        {
            return false; // orçamento total da geração esgotado
        }

        foreach (var pattern in patternSteps.SelectMany(step => step))
        {
            if (Compile(pattern) is not { } regex)
            {
                continue;
            }

            try
            {
                _ = regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }

    private const int MaxCachedPatterns = 1024;

    private static Regex? Compile(string pattern)
    {
        // XSDs de rascunho com patterns sempre novos não crescem a memória sem
        // teto (2ª revisão da Fase 9, A3).
        if (Compiled.Count >= MaxCachedPatterns && !Compiled.ContainsKey(pattern))
        {
            Compiled.Clear();
        }

        return Compiled.GetOrAdd(pattern, CompileNew);
    }

    private static Regex? CompileNew(string p)
    {
        try
        {
            // XSD: pattern ancorado no valor inteiro.
            return new Regex($@"\A(?:{p})\z", RegexOptions.CultureInvariant, Timeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
