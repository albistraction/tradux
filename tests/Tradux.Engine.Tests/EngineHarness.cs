using System.Text.Json;
using Tradux.Domain.Mapping;
using Tradux.Domain.SourceData;
using Tradux.Engine.Compilation;
using Tradux.Xsd;

namespace Tradux.Engine.Tests;

/// <summary>Compila contra um XSD de fixture e gera a partir de um objeto de teste, que entra como JSON (forma da API).</summary>
internal static class EngineHarness
{
    public static CompiledTemplate Compile(string xsdFilePath, MappingTemplate mapping, string? rootElementName = null) =>
        TemplateCompiler.Compile(mapping, XsdFiles.Import(xsdFilePath, rootElementName));

    public static GenerationOutcome Generate(object source, CompiledTemplate template) =>
        XmlGenerationEngine.Generate(template, Source(source));

    public static SourceDataTree Source(object source) => SourceDataTree.FromJson(JsonSerializer.SerializeToElement(source));
}
