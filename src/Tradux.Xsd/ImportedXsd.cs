using System.Xml.Schema;

namespace Tradux.Xsd;

/// <summary>
/// Resultado da importação: o modelo canônico já resolvido, o
/// <see cref="XmlSchemaSet"/> compilado original (reutilizado pelo
/// validador para validar o XML final contra o schema exato), e
/// <see cref="ContentHash"/> — hash SHA-256 do conteúdo canônico de todos os
/// arquivos efetivamente carregados (seção 11 do requisito de origem:
/// "versione o conjunto e gere hash do conteúdo"). Duas importações do
/// mesmo conjunto de arquivos produzem sempre o mesmo hash (determinismo
/// exigido pelo gate da Fase 3).
/// </summary>
public sealed class ImportedXsd
{
    public required DestinationSchema Schema { get; init; }
    public required XmlSchemaSet SchemaSet { get; init; }
    public required string ContentHash { get; init; }
}
