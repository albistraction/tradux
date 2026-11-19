namespace Tradux.Engine.Tests;

/// <summary>
/// Objeto de origem C# de exemplo, com um campo por elemento escalar da
/// fixture docs/schemas/exemplo-simples.xsd — o "exemplo realista" exigido
/// pelo gate da Fase 2.
/// </summary>
internal sealed class DpsSourceData
{
    public string? Numero { get; set; }
    public string? Serie { get; set; }
    public DateOnly? DataEmissao { get; set; }
    public DateOnly? Competencia { get; set; }
    public decimal? ValorServico { get; set; }
    public decimal? ValorDesconto { get; set; }
    public string? CodigoServico { get; set; }
    public string? CnpjPrestador { get; set; }
    public string? CnpjTomador { get; set; }
    public string? DiscriminacaoServico { get; set; }
    public string? CodigoSituacao { get; set; }

    public static DpsSourceData Realista() => new()
    {
        Numero = "000123",
        Serie = "A1",
        DataEmissao = new DateOnly(2026, 9, 28),
        Competencia = new DateOnly(2026, 9, 1),
        ValorServico = 1500.50m,
        ValorDesconto = 100.00m,
        CodigoServico = "01.02.03",
        CnpjPrestador = "12345678000199",
        CnpjTomador = "98765432000188",
        DiscriminacaoServico = "Serviço de consultoria",
        CodigoSituacao = "1",
    };
}
