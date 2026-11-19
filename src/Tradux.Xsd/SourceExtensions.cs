using System.Xml;
using Tradux.Domain.SourceContracts;
using Tradux.Domain.SourceData;

namespace Tradux.Xsd;

/// <summary>Extensão inválida (caminho inexistente, nome repetido ou inválido, limites).</summary>
public sealed class SourceExtensionException(string message) : InvalidOperationException(message);

/// <summary>
/// Combina a forma do XSD base com as extensões (ADR-0016 §2). Formato legado:
/// só snapshots antigos trazem extensões (ADR-0025) — nada novo as grava. A forma
/// estendida obedece aos mesmos <see cref="ExpandedTreeLimits"/> da forma do XSD.
/// </summary>
public static class SourceExtensionMerger
{
    /// <exception cref="SourceExtensionException">Extensão inválida.</exception>
    public static SourceShape Apply(SourceShape shape, IReadOnlyList<SourceExtension> extensions)
    {
        var budget = ExpandedTreeLimits.MaxNodes - Count(shape.Fields);
        var fields = shape.Fields;
        foreach (var extension in extensions)
        {
            // Corpo JSON pode trazer nulos apesar dos tipos (revisão da Fase 13, achado 4).
            if (extension?.Node is null)
            {
                throw new SourceExtensionException("Extensão sem 'node' (informe nome e tipo do campo ou bloco).");
            }

            var segments = Segments(extension.ParentPath);
            var node = Build(extension.Node, depth: segments.Count + 1, ref budget, NamespaceAt(shape, segments));
            fields = Insert(fields, segments, 0, node, extension.ParentPath);
        }

        return shape with { Fields = fields };
    }

    private static int Count(IReadOnlyList<SourceShapeNode> nodes) => nodes.Sum(node => 1 + Count(node.Children));

    /// <summary>Segmentos do caminho do pai (vazio = raiz).</summary>
    public static IReadOnlyList<string> Segments(string? parentPath) =>
        parentPath is null || string.IsNullOrWhiteSpace(parentPath) ? [] : parentPath.Split('.', StringSplitOptions.TrimEntries);

    private static IReadOnlyList<SourceShapeNode> Insert(
        IReadOnlyList<SourceShapeNode> siblings, IReadOnlyList<string> path, int index, SourceShapeNode node, string parentPath)
    {
        if (index == path.Count)
        {
            // Conteúdo simples/misto: um filho mudaria o valor do texto (revisão da Fase 13, achado 3).
            if (siblings.Any(s => s.Name == SourceShapeNode.TextField))
            {
                throw new SourceExtensionException($"'{Display(parentPath)}' tem valor próprio (texto) e não aceita campos ou blocos dentro.");
            }

            if (siblings.Any(s => s.Name == node.Name))
            {
                throw new SourceExtensionException($"'{node.Name}' já existe em '{Display(parentPath)}'.");
            }

            return [.. siblings, node];
        }

        var position = siblings.ToList().FindIndex(s => s.Name == path[index]);
        if (position < 0 || !siblings[position].IsObject)
        {
            throw new SourceExtensionException($"Caminho '{Display(parentPath)}' não existe no contrato ou não é um bloco (parou em '{path[index]}').");
        }

        var parent = siblings[position];
        var updated = siblings.ToList();
        updated[position] = parent with { Children = Insert(parent.Children, path, index + 1, node, parentPath) };
        return updated;
    }

    /// <summary>
    /// Namespace dos elementos de extensão sob o pai (revisão da Fase 13, R1):
    /// o dos elementos locais do pai. É o do próprio pai quando o XSD é
    /// qualified e nenhum quando é unqualified.
    /// </summary>
    private static string NamespaceAt(SourceShape shape, IReadOnlyList<string> segments)
    {
        var parentNamespace = shape.TargetNamespace ?? "";
        var current = shape.Fields;
        foreach (var segment in segments)
        {
            var next = current.FirstOrDefault(f => f.Name == segment);
            if (next is null)
            {
                return parentNamespace; // caminho inválido: Insert recusa com a mensagem certa
            }

            parentNamespace = next.Namespace ?? parentNamespace;
            current = next.Children;
        }

        var locals = current.Where(c => c.Namespace is not null && !c.IsExtension).Select(c => c.Namespace!).ToList();
        return locals.Count == 0 || locals.Contains(parentNamespace) ? parentNamespace : locals[0];
    }

    private static SourceShapeNode Build(SourceExtensionNode node, int depth, ref int budget, string ns)
    {
        if (--budget < 0)
        {
            throw new SourceExtensionException($"Contrato com extensões acima de {ExpandedTreeLimits.MaxNodes} campos.");
        }

        if (depth > ExpandedTreeLimits.MaxDepth)
        {
            throw new SourceExtensionException($"Extensão com profundidade acima de {ExpandedTreeLimits.MaxDepth}.");
        }

        if (node is null)
        {
            throw new SourceExtensionException("Bloco com filho vazio (null).");
        }

        try
        {
            XmlConvert.VerifyNCName(string.IsNullOrEmpty(node.Name) ? " " : node.Name);
        }
        catch (XmlException)
        {
            throw new SourceExtensionException($"Nome '{node.Name}' inválido para elemento XML (use letras, dígitos, '_', '-' ou '.'; sem espaços nem ':').");
        }

        var children = new List<SourceShapeNode>();
        if (node.IsObject)
        {
            foreach (var child in node.Children ?? [])
            {
                var built = Build(child, depth + 1, ref budget, ns);
                if (children.Any(c => c.Name == built.Name))
                {
                    throw new SourceExtensionException($"'{built.Name}' repetido dentro do bloco '{node.Name}'.");
                }

                children.Add(built);
            }
        }
        else if (node.Children is { Count: > 0 })
        {
            throw new SourceExtensionException($"Campo '{node.Name}' não pode ter filhos (use bloco).");
        }

        return new SourceShapeNode(node.Name!, node.IsObject, node.IsObject ? SourceValueType.Text : node.ValueType,
            node.Repeated, node.Required, Nillable: false, children, IsExtension: true, Namespace: ns);
    }

    /// <summary>
    /// Forma canônica para hash e gravação (revisão da Fase 13, achado 8): bloco
    /// com lista de filhos (nunca nula) e tipo texto; campo sem filhos.
    /// </summary>
    public static IReadOnlyList<SourceExtension> Normalize(IReadOnlyList<SourceExtension>? extensions) =>
        (extensions ?? []).Select(e => e?.Node is null ? e! : e with { ParentPath = string.Join('.', Segments(e.ParentPath)), Node = Normalize(e.Node) }).ToList();

    private static SourceExtensionNode Normalize(SourceExtensionNode node) => node.IsObject
        ? node with { ValueType = SourceValueType.Text, Children = (node.Children ?? []).Select(c => c is null ? c! : Normalize(c)).ToList() }
        : node with { Children = null };

    private static string Display(string parentPath) => string.IsNullOrWhiteSpace(parentPath) ? "(raiz)" : parentPath;
}
