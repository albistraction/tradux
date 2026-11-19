namespace Tradux.Xsd;

/// <summary>
/// Um atributo do elemento raiz de destino. O motor só emite atributos com
/// valor fixo (<see cref="FixedValue"/>): constantes do schema, sem mapeamento.
/// </summary>
public sealed class SchemaAttribute
{
    public required string Name { get; init; }
    public string? FixedValue { get; init; }
}
