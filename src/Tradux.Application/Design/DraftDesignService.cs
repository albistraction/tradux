using Tradux.Application.Generation;
using Tradux.Application.Ports;
using Tradux.Application.Security;
using Tradux.Application.Snapshots;
using Tradux.Application.Testing;
using Tradux.Contracts;
using Tradux.Contracts.Design;
using Tradux.Domain.Schema;
using Tradux.Domain.SourceData;
using Tradux.Engine;
using Tradux.Engine.Operators;
using Tradux.Xsd;

namespace Tradux.Application.Design;

/// <summary>
/// Casos de uso de desenho do editor visual — sem estado e sem persistência:
/// descrever as árvores de origem e de destino dos XSDs enviados, listar o
/// catálogo de operadores, comparar versões de XSD e testar um rascunho.
/// O rascunho nunca é gravado; teste e qualidade geram evento de auditoria
/// (seção 20) só com tipo e resultado.
/// </summary>
public sealed class DraftDesignService
{
    public const string ExpectedXmlInvalidCode = "EXPECTED_XML_INVALID";
    public const string ExpectedXmlMismatchCode = "EXPECTED_XML_MISMATCH";

    private readonly RuleOperatorCatalog _catalog;
    private readonly SnapshotCompiler _compiler;
    private readonly XsdImportSecurityOptions _importOptions;
    private readonly GenerationLimits _limits;
    private readonly AuditTrail? _audit;

    /// <param name="audit">Destino da auditoria de <see cref="TestAsync"/> e <see cref="QualityAsync"/>; sem ele, essas operações recusam.</param>
    public DraftDesignService(
        RuleOperatorCatalog? catalog = null, XsdImportSecurityOptions? importOptions = null, GenerationLimits? limits = null, IAuditSink? audit = null)
    {
        _limits = (limits ?? GenerationLimits.Default).Validated();
        _audit = audit is null ? null : new AuditTrail(audit);
        _catalog = catalog ?? RuleOperatorCatalog.CreateDefault();
        _importOptions = importOptions ?? XsdImportSecurityOptions.Default;
        _compiler = new SnapshotCompiler(_catalog, _importOptions);
    }

    /// <summary>Operadores do catálogo, com metadados, em ordem de nome.</summary>
    public IReadOnlyList<OperatorInfo> ListOperators() =>
        _catalog.Describe()
            .Select(o => new OperatorInfo(
                o.Definition.Name,
                o.Metadata.Category,
                o.Metadata.Description,
                o.Definition.MinArguments,
                o.Definition.MaxArguments == int.MaxValue ? null : o.Definition.MaxArguments,
                o.Metadata.Result.ToString(),
                o.Metadata.RepeatsLastParameter,
                o.Metadata.Parameters.Select(p => new OperatorParameterInfo(p.Name, p.Description, p.Kind.ToString())).ToList()))
            .ToList();

    /// <summary>
    /// Importa os XSDs enviados (em memória, mesmas proteções do pacote ZIP:
    /// sem path traversal, sem referência externa, sem DOCTYPE, com limites)
    /// e devolve a árvore de destino.
    /// </summary>
    /// <exception cref="ArgumentException">Base64 inválido ou XSD que não importa (mensagem explica o motivo).</exception>
    public DestinationTree DescribeSchema(SchemaUpload upload) => DestinationTreeBuilder.Build(Import(upload));

    /// <summary>
    /// Compara o XSD de destino do rascunho com um XSD novo (ex.: nova versão
    /// da prefeitura) e diz o que mudou e o que o mapeamento precisa (ADR-0017).
    /// Nada é gravado.
    /// </summary>
    /// <exception cref="ArgumentException">Pedido incompleto ou XSD que não importa (a mensagem diz qual).</exception>
    public SchemaChangeReport CompareSchemas(SchemaChangeRequest request)
    {
        if (request?.Before is null || request.After is null)
        {
            throw new ArgumentException("Informe 'before' (XSD atual) e 'after' (XSD novo).", nameof(request));
        }

        var before = Labeled("XSD atual", () => Import(request.Before));
        var after = Labeled("XSD novo", () => Import(request.After));
        return SchemaChangeAnalyzer.Analyze(before, after, MappedPaths(request.Mapping, before.Schema.RootElementName));
    }

    /// <summary>Compara o XSD de um template publicado (conteúdo do snapshot) com um XSD novo.</summary>
    /// <exception cref="ArgumentException">XSD novo ausente ou que não importa.</exception>
    public SchemaChangeReport CompareWithContent(SnapshotContent content, SchemaUpload? after)
    {
        if (after is null)
        {
            throw new ArgumentException("Informe 'after' (XSD novo).", nameof(after));
        }

        var current = Labeled("XSD do template", () => Import(content.SchemaFiles, content.SchemaEntryPoint, content.RootElementName));
        var next = Labeled("XSD novo", () => Import(after));
        return SchemaChangeAnalyzer.Analyze(current, next, MappedPaths(content.Mapping, current.Schema.RootElementName));
    }

    private static ImportedXsd Labeled(string label, Func<ImportedXsd> import)
    {
        try
        {
            return import();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"{label}: {ex.Message}", ex.ParamName, ex);
        }
    }

    private static IReadOnlyCollection<string> MappedPaths(Domain.Mapping.MappingTemplate? mapping, string rootElementName)
    {
        if (mapping is null)
        {
            return [];
        }

        RequireWellFormed(mapping, depth: 1);
        return Layers.MappingPaths.Flatten(mapping, rootElementName).Keys.ToList();
    }

    // Corpo JSON pode trazer nulos apesar dos tipos (revisão da Fase 14, A4): 400, nunca 500.
    private static void RequireWellFormed(Domain.Mapping.MappingTemplate template, int depth)
    {
        if (depth > 64)
        {
            throw new ArgumentException("Mapeamento com aninhamento acima de 64 níveis.", nameof(template));
        }

        if (template.Fields is null || template.Complex is null || template.Collections is null
            || template.Fields.Any(f => f?.DestinationElementName is null)
            || template.Complex.Any(c => c?.DestinationElementName is null || c.Content is null)
            || template.Collections.Any(c => c?.DestinationElementName is null || c.ItemContent is null))
        {
            throw new ArgumentException("Mapeamento malformado: listas e itens não podem ser nulos e cada item precisa de 'destinationElementName'.", nameof(template));
        }

        foreach (var complex in template.Complex)
        {
            RequireWellFormed(complex.Content, depth + 1);
        }

        foreach (var collection in template.Collections)
        {
            RequireWellFormed(collection.ItemContent, depth + 1);
        }
    }

    /// <summary>
    /// Importa o XSD de <b>origem</b> (ADR-0015) com as mesmas proteções e
    /// devolve a árvore de campos que o editor oferece para mapear — a mesma
    /// forma que o XML de entrada terá na geração.
    /// </summary>
    /// <exception cref="ArgumentException">Base64 inválido ou XSD que não importa.</exception>
    public SourceFieldTree DescribeSource(SchemaUpload upload)
    {
        if (upload is null)
        {
            throw new ArgumentException("Envie o XSD de origem.", nameof(upload));
        }

        SourceSchema source;
        try
        {
            source = XsdSchemaImporter.ImportSourceInMemory(SchemaUploads.Decode(upload), upload.EntryPoint, upload.RootElementName, _importOptions);
        }
        catch (Exception ex) when (XsdSchemaImporter.IsImportFailure(ex))
        {
            throw new ArgumentException($"Não foi possível importar o XSD de origem: {ex.Message}", nameof(upload), ex);
        }

        return new SourceFieldTree(source.Shape.RootElementName, source.Shape.TargetNamespace, source.ContentHash,
            new SourceFieldNode("(dados)", "", "object", Required: true, Children: SourceFields(source.Shape.Fields, "")));
    }

    private static List<SourceFieldNode> SourceFields(IReadOnlyList<SourceShapeNode> fields, string prefix) =>
        fields.Select(field =>
        {
            var path = prefix.Length == 0 ? field.Name : $"{prefix}.{field.Name}";
            var type = field.IsObject ? "object" : TypeName(field.ValueType);
            if (!field.Repeated)
            {
                return field.IsObject
                    ? new SourceFieldNode(field.Name, path, "object", field.Required, field.Nillable, SourceFields(field.Children, path))
                    : new SourceFieldNode(field.Name, path, type, field.Required, field.Nillable);
            }

            // Item de lista: escopo de um bloco "for each" (caminhos relativos ao item).
            var index = new SourceFieldNode("$index", "$index", "number", Required: true);
            var itemChildren = field.IsObject
                ? [.. SourceFields(field.Children, ""), index]
                : new List<SourceFieldNode> { new(SourceShapeNode.TextField, SourceShapeNode.TextField, type, Required: true), index };
            return new SourceFieldNode(field.Name, path, "list", field.Required, field.Nillable,
                Item: new SourceFieldNode("(item)", "", "object", Required: true, Children: itemChildren));
        }).ToList();

    private static string TypeName(SourceValueType type) => type switch
    {
        SourceValueType.Number => "number",
        SourceValueType.Date => "date",
        _ => "text",
    };

    /// <summary>
    /// Testa um rascunho: compila (obrigatórios sem mapeamento, operadores,
    /// conteúdo não suportado), gera com a entrada de exemplo (XML de origem ou
    /// JSON) e, se houver, compara com o XML esperado (comparação semântica:
    /// ignora prefixo de namespace e formatação).
    /// </summary>
    /// <exception cref="ArgumentException">Schema, XSD de origem ou entrada malformados, ou entrada acima dos limites.</exception>
    public async Task<DraftTestResult> TestAsync(DraftTestRequest request, AccessPrincipal principal, CancellationToken cancellationToken = default)
    {
        var result = Test(request);
        await AuditAsync(principal, "test", result.Publishable, cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal DraftTestResult Test(DraftTestRequest request)
    {
        // Corpo JSON sem "schema"/"mapping" chega com null apesar do tipo não anulável.
        if (request.Schema is null || request.Mapping is null)
        {
            throw new ArgumentException("Informe 'schema' e 'mapping'.", nameof(request));
        }

        var content = SnapshotJson.Rehydrate(SnapshotContent.FromUpload(request.Schema, request.Mapping, request.LookupTables, request.SourceSchema));

        SourceDataTree sourceData;
        if (request.SourceXml is { Length: > 0 } sourceXml)
        {
            if (request.SourceSchema is null)
            {
                throw new ArgumentException("Entrada em XML ('sourceXml') exige o XSD de origem ('sourceSchema').", nameof(request));
            }

            try
            {
                sourceData = SourceInputs.Xml(ImportSource(content), sourceXml, _limits);
            }
            catch (SourceXmlException ex)
            {
                return new DraftTestResult(false, SourceInputs.Diagnostics(ex), null, null, null);
            }
        }
        else
        {
            sourceData = SourceInputs.Json(request.Data, _limits);
        }

        Engine.Compilation.CompiledTemplate template;
        string contentHash;
        try
        {
            var (xsdHash, compiled) = _compiler.CompileContent(content);
            template = compiled;
            contentHash = SnapshotCompiler.ComputeContentHash(xsdHash, content);
        }
        catch (SnapshotCompilationException ex)
        {
            var diagnostic = Diagnostic.Critical(XmlGenerationService.SnapshotCompilationFailedCode, ex.Message,
                "A mensagem indica se o problema está no XSD enviado ou no mapeamento (operador, destino repetido ou conteúdo não suportado).");
            return new DraftTestResult(false, [diagnostic], null, null, null);
        }

        var compileDiagnostics = GenerationResultMapper.ToContract(template.CompileTimeDiagnostics);

        // Com erro crítico de compilação o motor não gera nada e devolveria os
        // mesmos diagnósticos de compilação — não repete (revisão da Fase 6, A3).
        if (template.HasCriticalCompileTimeDiagnostics)
        {
            return new DraftTestResult(false, compileDiagnostics, null, null, contentHash);
        }

        var outcome = XmlGenerationEngine.Generate(template, sourceData);
        var generation = GenerationResultMapper.ToContract(outcome);

        bool? matches = null;
        var extra = new List<Diagnostic>();
        if (request.ExpectedXml is { Length: > 0 } expected)
        {
            matches = CompareExpected(expected, outcome.Xml, extra);
        }

        var publishable = outcome.Success && matches != false;
        return new DraftTestResult(publishable, compileDiagnostics.Concat(extra).ToList(), generation, matches, contentHash);
    }

    /// <summary>
    /// Roda a suíte estrutural automática (ADR-0011 §3) sobre o rascunho — o
    /// mesmo teste que o gate de publicação exige. Sem estado.
    /// </summary>
    /// <exception cref="ArgumentException">Schema ou mapeamento malformados.</exception>
    public async Task<DraftQualityResult> QualityAsync(DraftQualityRequest request, AccessPrincipal principal, CancellationToken cancellationToken = default)
    {
        var result = Quality(request);
        await AuditAsync(principal, "quality", result.Passed, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private Task AuditAsync(AccessPrincipal principal, string kind, bool passed, CancellationToken cancellationToken) =>
        (_audit ?? throw new InvalidOperationException("DraftDesignService sem auditoria configurada não testa rascunhos."))
            .RecordAsync(principal.OrganizationId, principal.Actor, "template.test", $"design:{kind}",
                passed ? AuditTrail.Success : AuditTrail.Rejected, new() { ["kind"] = kind }, cancellationToken);

    internal DraftQualityResult Quality(DraftQualityRequest request)
    {
        if (request.Schema is null || request.Mapping is null)
        {
            throw new ArgumentException("Informe 'schema' e 'mapping'.", nameof(request));
        }

        var content = SnapshotJson.Rehydrate(SnapshotContent.FromUpload(request.Schema, request.Mapping, request.LookupTables));

        Engine.Compilation.CompiledTemplate template;
        try
        {
            template = _compiler.CompileContent(content).Template;
        }
        catch (SnapshotCompilationException ex)
        {
            return new DraftQualityResult(false,
                [Diagnostic.Critical(XmlGenerationService.SnapshotCompilationFailedCode, ex.Message)],
                Contracts.Quality.QualityReport.Empty);
        }

        var compileDiagnostics = GenerationResultMapper.ToContract(template.CompileTimeDiagnostics);
        if (template.HasCriticalCompileTimeDiagnostics)
        {
            return new DraftQualityResult(false, compileDiagnostics, Contracts.Quality.QualityReport.Empty);
        }

        var report = Application.Quality.StructuralTestRunner.Run(template, content.Mapping, content.LookupTables);
        return new DraftQualityResult(report.Passed, compileDiagnostics, report);
    }

    private static bool CompareExpected(string expected, string? actual, List<Diagnostic> diagnostics)
    {
        var comparison = XmlSemanticComparer.Compare(expected, actual);
        if (!comparison.Equal)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Critical,
                comparison.ExpectedInvalid ? ExpectedXmlInvalidCode : ExpectedXmlMismatchCode,
                comparison.Message!,
                comparison.XPath,
                null,
                comparison.ExpectedInvalid ? null
                    : actual is null ? "Corrija os diagnósticos da geração primeiro." : "Ajuste o mapeamento ou o XML esperado neste ponto."));
        }

        return comparison.Equal;
    }

    private ImportedXsd Import(SchemaUpload upload) =>
        Import(SchemaUploads.Decode(upload), upload.EntryPoint, upload.RootElementName);

    /// <exception cref="ArgumentException">XSD que não importa (a mensagem explica o motivo).</exception>
    private ImportedXsd Import(IReadOnlyDictionary<string, byte[]> files, string entryPoint, string? rootElementName)
    {
        try
        {
            return XsdSchemaImporter.ImportInMemory(files, entryPoint, rootElementName, _importOptions);
        }
        catch (Exception ex) when (XsdSchemaImporter.IsImportFailure(ex))
        {
            throw new ArgumentException($"Não foi possível importar o XSD: {ex.Message}", nameof(files), ex);
        }
    }

    /// <exception cref="ArgumentException">XSD de origem que não importa.</exception>
    private SourceSchema ImportSource(SnapshotContent content)
    {
        try
        {
            return _compiler.CompileSource(content)!;
        }
        catch (SnapshotCompilationException ex)
        {
            throw new ArgumentException($"Não foi possível importar o XSD de origem: {ex.InnerException?.Message ?? ex.Message}", "sourceSchema", ex);
        }
    }
}
