namespace Tradux.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Espera por algo que chega de forma assíncrona (Change Stream, telemetria)
/// sondando a cada 100 ms, em vez de um atraso fixo — a suíte inteira deixa a
/// máquina lenta. Quem chama faz o Assert sobre o valor devolvido.
/// </summary>
internal static class Eventually
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>Repete <paramref name="probe"/> até <paramref name="done"/> ou o prazo; devolve a última leitura.</summary>
    public static async Task<T> PollAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var last = await probe();
        while (!done(last) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(Interval);
            last = await probe();
        }

        return last;
    }

    /// <summary>Espera <paramref name="condition"/> valer; devolve o último valor dela.</summary>
    public static Task<bool> TrueAsync(Func<bool> condition, TimeSpan? timeout = null) =>
        PollAsync(() => Task.FromResult(condition()), ok => ok, timeout);
}
