namespace Tradux.Engine.Tests;

/// <summary>Objeto de origem C# com uma coleção — exemplo realista para docs/schemas/exemplo-colecao.xsd (Fase 4).</summary>
internal sealed class PedidoSourceData
{
    public string? Numero { get; set; }
    public List<ItemPedidoSourceData> Itens { get; set; } = [];
    public decimal? ValorTotal { get; set; }
}

internal sealed class ItemPedidoSourceData
{
    public string? Descricao { get; set; }
    public decimal? ValorUnitario { get; set; }
}
