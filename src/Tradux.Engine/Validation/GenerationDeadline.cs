using System.Diagnostics;

namespace Tradux.Engine.Validation;

/// <summary>
/// Prazo de uma operação: geração, requisição de geração (com fallback entre
/// versões) ou suíte estrutural. O timeout de regex é por avaliação; sem um
/// prazo total, muitas avaliações logo abaixo do timeout somam minutos.
///
/// O prazo acompanha a operação também através de <c>await</c>
/// (<see cref="AsyncLocal{T}"/>). Escopos aninhados nunca o estendem: valem o
/// menor entre o prazo vigente e o novo. Ao estourar, a geração falha com
/// <c>XSD_VALIDATION_TIMEOUT</c> e a suíte reprova os casos restantes; nunca
/// há aceitação silenciosa.
/// </summary>
public static class GenerationDeadline
{
    /// <summary>Tempo máximo de uma geração (checagem de pattern e validação do XML gerado).</summary>
    private static readonly TimeSpan GenerationBudget = TimeSpan.FromSeconds(5);

    private static readonly AsyncLocal<long> Current = new();

    /// <summary>Verdadeiro quando a operação em curso passou do prazo.</summary>
    public static bool Expired => Current.Value != 0 && Stopwatch.GetTimestamp() > Current.Value;

    /// <summary>Tempo restante da operação em curso (<see cref="TimeSpan.MaxValue"/> fora de uma operação).</summary>
    public static TimeSpan Remaining => Current.Value == 0
        ? TimeSpan.MaxValue
        : TimeSpan.FromSeconds(Math.Max(0, Current.Value - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);

    /// <summary>Abre um prazo de <paramref name="budget"/> (padrão: 5 s, o de uma geração), limitado ao prazo já vigente.</summary>
    public static Scope Start(TimeSpan? budget = null) => new(budget ?? GenerationBudget);

    public readonly struct Scope : IDisposable
    {
        private readonly long _previous;

        public Scope(TimeSpan budget)
        {
            _previous = Current.Value;
            var end = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
            Current.Value = _previous != 0 ? Math.Min(_previous, end) : end;
        }

        public void Dispose() => Current.Value = _previous;
    }
}
