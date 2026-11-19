namespace Tradux.Xsd.Security;

/// <summary>
/// Timeout padrão de regex do processo (ADR-0012 §9). A validação XSD do
/// .NET avalia patterns com o timeout padrão do processo — por padrão
/// infinito. O host da API chama <see cref="Configure"/>
/// no início, antes de qualquer <c>Regex</c> ser criada: o valor é lido uma
/// única vez pelo runtime.
/// </summary>
public static class RegexTimeout
{
    public const string AppContextKey = "REGEX_DEFAULT_MATCH_TIMEOUT";

    /// <summary>
    /// Por avaliação. Patterns fiscais reais levam microssegundos; com o limite
    /// de avaliações por valor e por tag (ContentModelSizeChecker), o pior caso de
    /// uma leitura fica em segundos e o prazo total da operação cobre o resto.
    /// </summary>
    public static readonly TimeSpan Default = TimeSpan.FromMilliseconds(250);

    /// <summary>Fixa o timeout (variável de ambiente <c>TRADUX_REGEX_TIMEOUT_MS</c> sobrepõe o padrão de 250 ms).</summary>
    public static void Configure(TimeSpan? timeout = null)
    {
        var value = timeout
            ?? (int.TryParse(Environment.GetEnvironmentVariable("TRADUX_REGEX_TIMEOUT_MS"), out var ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : Default);
        AppContext.SetData(AppContextKey, value);
    }
}
