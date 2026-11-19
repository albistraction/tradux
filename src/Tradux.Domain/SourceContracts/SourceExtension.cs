using Tradux.Domain.SourceData;

namespace Tradux.Domain.SourceContracts;

// Formato legado (ADR-0016): extensões do contrato de entrada gravadas por
// snapshots antigos. Só leitura — nada novo as grava (ADR-0025).

/// <summary>
/// Nó montado pelo autor na árvore do contrato de entrada: campo
/// (texto/número/data) ou bloco (com filhos), obrigatório e/ou repetível.
/// </summary>
public sealed record SourceExtensionNode(
    string Name,
    bool IsObject,
    SourceValueType ValueType = SourceValueType.Text,
    bool Required = false,
    bool Repeated = false,
    IReadOnlyList<SourceExtensionNode>? Children = null);

/// <summary>
/// Extensão pendurada num objeto da árvore do XSD base: <see cref="ParentPath"/>
/// é o caminho de nomes locais separados por ponto (<c>""</c> = raiz; dentro
/// de lista, passa pelo nome da lista e vale para cada item).
/// </summary>
public sealed record SourceExtension(string ParentPath, SourceExtensionNode Node);
