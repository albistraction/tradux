namespace Tradux.Domain.Mapping;

/// <summary>
/// Mapeamento de um elemento de destino escalar (seção 9: "Mapeamento").
/// <see cref="Value"/> é a expressão que produz o valor; <see cref="Condition"/>
/// é o grupo de regras (seção 9: "Grupo de regras" — E/OU/negação, via os
/// operadores <c>and</c>/<c>or</c>/<c>not</c>) que controla SE o mapeamento
/// se aplica. Quando <see cref="Condition"/> é <c>null</c>, o mapeamento
/// sempre se aplica. Quando a condição avalia falsa, o campo é tratado como
/// se não tivesse mapeamento algum (mesma lógica de obrigatório/opcional).
/// </summary>
public sealed record FieldMapping(string DestinationElementName, RuleNode Value, RuleNode? Condition = null)
{
    /// <summary>Mapeamento direto de campo escalar (operador <c>copy</c> implícito).</summary>
    public static FieldMapping Direct(string sourcePath, string destinationElementName) =>
        new(destinationElementName, new FieldReferenceNode(sourcePath));
}
