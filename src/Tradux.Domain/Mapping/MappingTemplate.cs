namespace Tradux.Domain.Mapping;

/// <summary>
/// Conjunto de mapeamentos para os filhos de um elemento de destino
/// (o elemento raiz do template, ou o conteúdo de um elemento complexo
/// aninhado — ver <see cref="ComplexMapping"/>/<see cref="CollectionMapping"/>).
/// A ordem de emissão no XML final segue sempre a ordem do schema de
/// destino, nunca a ordem destas listas (seção 15) — quem garante isso é
/// <c>Tradux.Engine.Compilation.TemplateCompiler</c>, ao percorrer o
/// schema e procurar, para cada elemento, o mapeamento correspondente aqui.
/// </summary>
public sealed class MappingTemplate
{
    public IReadOnlyList<FieldMapping> Fields { get; init; } = [];
    public IReadOnlyList<ComplexMapping> Complex { get; init; } = [];
    public IReadOnlyList<CollectionMapping> Collections { get; init; } = [];
}
