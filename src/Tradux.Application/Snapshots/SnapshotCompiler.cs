using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tradux.Engine.Compilation;
using Tradux.Engine.Operators;
using Tradux.Xsd;

namespace Tradux.Application.Snapshots;

/// <summary>
/// Transforma o conteúdo autocontido de um snapshot em template executável:
/// importa os XSDs em memória (<see cref="XsdSchemaImporter.ImportInMemory"/>,
/// com as mesmas proteções do pacote ZIP) e compila o mapeamento
/// (<see cref="TemplateCompiler"/>, ADR-0001). Usado na publicação (para
/// validar antes de gravar) e no cache-miss (para reconstruir a entrada).
/// </summary>
public sealed class SnapshotCompiler
{
    private readonly RuleOperatorCatalog _operatorCatalog;
    private readonly XsdImportSecurityOptions _importOptions;

    public SnapshotCompiler(RuleOperatorCatalog? operatorCatalog = null, XsdImportSecurityOptions? importOptions = null)
    {
        _operatorCatalog = operatorCatalog ?? RuleOperatorCatalog.CreateDefault();
        _importOptions = importOptions ?? XsdImportSecurityOptions.Default;
    }

    public CompiledSnapshot Compile(TemplateSnapshot snapshot)
    {
        var (_, template) = CompileContent(snapshot.Content);
        return new CompiledSnapshot(snapshot.Key, snapshot.Version, snapshot.ContentHash, template) { Source = CompileSource(snapshot.Content) };
    }

    /// <summary>Importa e compila o conteúdo; devolve também o hash do conjunto XSD (entrada de <see cref="ComputeContentHash"/>).</summary>
    public (string XsdContentHash, CompiledTemplate Template) CompileContent(SnapshotContent content)
    {
        ImportedXsd xsd;
        try
        {
            xsd = XsdSchemaImporter.ImportInMemory(content.SchemaFiles, content.SchemaEntryPoint, content.RootElementName, _importOptions);
        }
        catch (Exception ex) when (XsdSchemaImporter.IsImportFailure(ex))
        {
            throw new SnapshotCompilationException($"Falha ao importar o XSD de destino do snapshot: {ex.Message}", ex);
        }

        try
        {
            return (xsd.ContentHash, TemplateCompiler.Compile(content.Mapping, xsd, _operatorCatalog, content.LookupTables));
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new SnapshotCompilationException($"Falha ao compilar o mapeamento do snapshot: {ex.Message}", ex);
        }
    }

    /// <summary>Importa o XSD de origem (mesmas proteções da importação de XSD); nulo se o conteúdo não tem.</summary>
    /// <exception cref="SnapshotCompilationException">XSD de origem inválido.</exception>
    public SourceSchema? CompileSource(SnapshotContent content)
    {
        if (content.SourceSchema is not { } source)
        {
            return null;
        }

        try
        {
            return XsdSchemaImporter.ImportSourceInMemory(source.Files, source.EntryPoint, source.RootElementName, _importOptions)
                .WithExtensions(source.Extensions);
        }
        catch (Exception ex) when (XsdSchemaImporter.IsImportFailure(ex))
        {
            throw new SnapshotCompilationException($"Falha ao importar o XSD de origem do snapshot: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Hash de conteúdo do snapshot (ADR-0003): SHA-256 sobre o hash do
    /// conjunto XSD, o mapeamento serializado e as tabelas de lookup com
    /// chaves ordenadas (+ XSD de origem, só quando presente — ADR-0015).
    /// Mesmo conteúdo → mesmo hash, sempre.
    /// </summary>
    public static string ComputeContentHash(string xsdContentHash, SnapshotContent content)
    {
        var mappingJson = JsonSerializer.Serialize(content.Mapping, SnapshotJson.Options);
        var sortedLookups = content.LookupTables
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new KeyValuePair<string, KeyValuePair<string, string>[]>(
                t.Key, t.Value.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray()))
            .ToArray();
        var lookupsJson = JsonSerializer.Serialize(sortedLookups, SnapshotJson.Options);

        var canonical = string.Join('\n', xsdContentHash, content.RootElementName ?? string.Empty, mappingJson, lookupsJson);
        if (content.SourceSchema is { } source)
        {
            canonical += "\nsource:" + source.Hash(); // só com origem: hashes anteriores à Fase 12 não mudam
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
