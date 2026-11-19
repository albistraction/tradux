namespace Tradux.Engine.Operators;

/// <summary>
/// Sentinela retornado pelo operador <c>omit</c> (seção 13): força a
/// omissão do elemento de destino mesmo que ele seja obrigatório — uma
/// escolha deliberada do autor do mapeamento, diferente de "o valor de
/// origem calculou nulo" (que segue a regra normal de obrigatório/opcional/
/// nillable).
/// </summary>
public sealed class OmitMarker
{
    public static readonly OmitMarker Instance = new();

    private OmitMarker()
    {
    }
}
