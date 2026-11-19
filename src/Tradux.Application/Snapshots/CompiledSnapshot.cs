using Tradux.Engine.Compilation;

namespace Tradux.Application.Snapshots;

/// <summary>
/// Entrada imutável do cache (ADR-0005): identifica a versão exata e carrega
/// o template já compilado (ADR-0001). Trocar a versão ativa troca a
/// referência inteira, nunca edita uma entrada em uso.
/// </summary>
public sealed record CompiledSnapshot(TemplateKey Key, int Version, string ContentHash, CompiledTemplate Template)
{
    /// <summary>XSD de origem compilado (ADR-0015); nulo = template sem entrada XML.</summary>
    public Xsd.SourceSchema? Source { get; init; }
}
