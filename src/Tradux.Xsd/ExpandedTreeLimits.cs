namespace Tradux.Xsd;

/// <summary>
/// Limites da árvore <b>expandida</b> que a importação monta: o destino, para o
/// gerador e o editor, e a forma de origem. Os guardas anteriores à compilação
/// limitam o XSD como texto (profundidade XML, altura de referências, tamanho
/// dos blocos), mas tipos reutilizados se multiplicam na expansão: altura ×
/// aninhamento dá profundidade, e leque^altura dá número de nós. Por isso a
/// árvore é limitada direto, ao ser montada, e tudo que a percorre depois
/// (editor, comparação, testes estruturais) fica limitado também. Cada nível de
/// partícula conta, seja elemento ou grupo sequence/choice/all.
/// XSDs fiscais reais ficam em poucas dezenas de níveis e alguns milhares de nós.
/// </summary>
public static class ExpandedTreeLimits
{
    public const int MaxDepth = 128;

    public const int MaxNodes = 20_000;

    internal static NotSupportedException TooDeep(string where) =>
        new($"O XSD {where} se expande em mais de {MaxDepth} níveis de elementos/grupos; simplifique o aninhamento.");

    internal static NotSupportedException TooLarge(string where) =>
        new($"O XSD {where} se expande em mais de {MaxNodes} elementos (tipos reutilizados se multiplicam); simplifique.");
}
