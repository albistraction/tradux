namespace Tradux.Xsd.Tests;

internal static class SchemaLookup
{
    /// <summary>Primeiro elemento escalar com esse nome, em qualquer nível do conteúdo.</summary>
    public static SchemaElement? FindScalar(this DestinationSchema schema, string name) => Scalars(schema.RootContent).FirstOrDefault(e => e.Name == name);

    /// <summary>Elementos escalares diretamente no conteúdo da raiz, na ordem do schema.</summary>
    public static IEnumerable<SchemaElement> RootScalars(this DestinationSchema schema) =>
        schema.RootContent.Items.OfType<ScalarElementItem>().Select(item => item.Element);

    private static IEnumerable<SchemaElement> Scalars(SchemaParticle particle) => particle.Items.SelectMany(item => item switch
    {
        ScalarElementItem scalar => [scalar.Element],
        ComplexElementItem complex => Scalars(complex.Content),
        GroupItem group => Scalars(group.Group),
        _ => [],
    });
}
