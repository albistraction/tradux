namespace Tradux.Xsd.Tests;

/// <summary>Os limites de leitura de XML de origem que a geração usa por padrão.</summary>
internal static class TestSourceXml
{
    public static SourceXmlLimits Limits { get; } = new(MaxCharacters: 1_048_576, MaxDepth: 32);
}
