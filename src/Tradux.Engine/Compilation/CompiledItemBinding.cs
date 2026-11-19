using Tradux.Domain.SourceData;
using Tradux.Xsd;

namespace Tradux.Engine.Compilation;

/// <summary>Um item de destino já compilado — folha escalar, elemento complexo ou coleção. Compare com <see cref="Tradux.Xsd.SchemaParticleItem"/>, sua contraparte não compilada.</summary>
public abstract class CompiledItemBinding
{
    public required string Name { get; init; }
}

/// <summary>Campo escalar compilado (ADR-0001: <see cref="Resolve"/> já é um delegate pronto, não uma árvore reinterpretada).</summary>
public sealed class CompiledScalarBinding : CompiledItemBinding
{
    public required SchemaElement Element { get; init; }

    /// <summary>Descrição da origem do valor, só para diagnóstico (ex.: caminho de origem ou "constante").</summary>
    public required string SourceDescription { get; init; }

    public required Func<SourceDataTree, object?> Resolve { get; init; }
}

/// <summary>Elemento complexo não repetível compilado — <see cref="Content"/> é a partícula compilada de seus filhos.</summary>
public sealed class CompiledComplexBinding : CompiledItemBinding
{
    public required ComplexElementItem SchemaItem { get; init; }
    public required CompiledParticle Content { get; init; }
}

/// <summary>Grupo sem elemento próprio (sequence/choice/all aninhado): transparente no XML e no mapeamento.</summary>
public sealed class CompiledGroupBinding : CompiledItemBinding
{
    public required CompiledParticle Group { get; init; }
}

/// <summary>Bloco de iteração compilado (seção 14): resolve a coleção de origem e aplica <see cref="ItemContent"/> a cada item.</summary>
public sealed class CompiledCollectionBinding : CompiledItemBinding
{
    public required ComplexElementItem SchemaItem { get; init; }
    public required Func<SourceDataTree, object?> ResolveCollection { get; init; }

    /// <summary>Objeto único no caminho conta como 1 item (opção explícita do mapeamento, ADR-0020).</summary>
    public bool SingleObjectAsItem { get; init; }

    public Func<SourceDataTree, object?>? Filter { get; init; }
    public Func<SourceDataTree, object?>? OrderByKey { get; init; }
    public required CompiledParticle ItemContent { get; init; }
}
