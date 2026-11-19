namespace Tradux.Domain.Schema;

/// <summary>
/// Se um campo de schema aceita nulo explícito (<c>nillable="true"</c> no
/// XSD). Independente de <see cref="Cardinality"/> — um campo pode ser
/// obrigatório (minOccurs=1) e ainda assim aceitar nulo explícito.
/// </summary>
public enum Nillability
{
    NotNillable,
    Nillable,
}
