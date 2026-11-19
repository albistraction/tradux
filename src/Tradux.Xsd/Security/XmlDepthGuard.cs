using System.Xml;

namespace Tradux.Xsd.Security;

/// <summary>
/// Recusa XSD com aninhamento XML muito profundo <b>antes</b> de entregá-lo ao
/// .NET: o pré-processador e a compilação de schema são recursivos, e mil níveis
/// de <c>complexType</c> anônimo bastam para estourar a pilha (o que derruba o
/// processo inteiro, sem 500). XSDs fiscais reais têm algumas dezenas de níveis.
/// A leitura é em fluxo, sem montar árvore.
/// </summary>
internal static class XmlDepthGuard
{
    /// <exception cref="InvalidOperationException">Profundidade acima do limite.</exception>
    public static void Check(byte[] content, string name, int maxDepth)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(content), settings);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.Depth > maxDepth)
                {
                    throw new InvalidOperationException(
                        $"'{name}' tem aninhamento XML acima de {maxDepth} níveis (linha {((IXmlLineInfo)reader).LineNumber}).");
                }
            }
        }
        catch (XmlException)
        {
            // Malformado: a importação normal relata o erro com a mensagem do parser.
        }
    }
}
