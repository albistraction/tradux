using System.Security.Cryptography;
using System.Xml;
using System.Xml.Schema;
using Tradux.Domain.Schema;
using Tradux.Xsd.Security;

namespace Tradux.Xsd;

/// <summary>
/// Importa um conjunto de XSDs em memória (nome relativo → conteúdo; seção 11
/// do requisito de origem) e produz o modelo canônico de schema —
/// <c>xs:sequence</c>, <c>xs:choice</c> e <c>xs:all</c>, possivelmente
/// aninhados. Um só caminho para upload, snapshot e testes: nada é lido do
/// disco e nenhuma referência sai do conjunto.
/// </summary>
public static class XsdSchemaImporter
{
    /// <summary>
    /// Exceções com que a importação recusa um XSD (malformado, inseguro, não
    /// suportado ou acima dos limites) — o chamador as converte na sua falha.
    /// </summary>
    public static bool IsImportFailure(Exception ex) =>
        ex is InvalidOperationException or NotSupportedException or System.Xml.XmlException or System.Xml.Schema.XmlSchemaException;

    /// <summary>
    /// Importa o XSD de <b>destino</b>: nomes relativos sem <c>..</c>, limites de
    /// quantidade/tamanho, <c>DOCTYPE</c> recusado (salvo o caso restrito de
    /// <see cref="Security.SafeDoctype"/>), nenhuma referência para fora do
    /// conjunto. Mesmo conjunto de arquivos → mesmo <see cref="ImportedXsd.ContentHash"/>
    /// (ADR-0004: snapshot autocontido).
    /// </summary>
    public static ImportedXsd ImportInMemory(
        IReadOnlyDictionary<string, byte[]> files,
        string entryPointFileName,
        string? rootElementName = null,
        XsdImportSecurityOptions? options = null)
    {
        options ??= XsdImportSecurityOptions.Default;
        return LargeStack.Run(() =>
        {
            var (schemaSet, contentHash) = LoadSchemaSet(ValidateEntries(files, options), entryPointFileName, options);
            try
            {
                return Build(schemaSet, rootElementName, contentHash);
            }
            catch (OverflowException ex)
            {
                // minOccurs/maxOccurs acima de int (ex.: xs:any maxOccurs="99999999999"): erro de schema, 400 — nunca 500.
                throw new XmlSchemaException($"Cardinalidade acima do suportado ({int.MaxValue}) no XSD: {ex.Message}", ex);
            }
        });
    }

    /// <summary>
    /// Importa o XSD de <b>origem</b> (ADR-0015 §2) com as mesmas proteções de
    /// <see cref="ImportInMemory"/> (limites, nomes, DOCTYPE proibido, nada fora
    /// do conjunto, patterns perigosos), mas sem as restrições do modelo de
    /// destino: a forma da origem aceita qualquer tipo simples (numéricos viram
    /// número; date/dateTime/gYearMonth, data; o resto, texto), conteúdo
    /// simples com atributos (<c>$value</c>), tipos vazios e <c>xs:any</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Limites, nomes ou elemento raiz.</exception>
    /// <exception cref="XmlSchemaException">XSD inválido.</exception>
    /// <exception cref="NotSupportedException">Elemento raiz sem tipo complexo.</exception>
    public static SourceSchema ImportSourceInMemory(
        IReadOnlyDictionary<string, byte[]> files,
        string entryPointFileName,
        string? rootElementName = null,
        XsdImportSecurityOptions? options = null)
    {
        options ??= XsdImportSecurityOptions.Default;
        return LargeStack.Run(() =>
        {
            var (schemaSet, contentHash) = LoadSchemaSet(ValidateEntries(files, options), entryPointFileName, options);
            var root = ResolveRootElement(schemaSet, rootElementName);
            return new SourceSchema(schemaSet, contentHash, SourceShape.From(root));
        });
    }

    private static Dictionary<string, byte[]> ValidateEntries(IReadOnlyDictionary<string, byte[]> files, XsdImportSecurityOptions options)
    {
        if (files.Count > options.MaxPackageEntryCount)
        {
            throw new InvalidOperationException(
                $"Conjunto de XSDs tem {files.Count} arquivos, acima do limite de {options.MaxPackageEntryCount}.");
        }

        long total = 0;
        long expandedTotal = 0;
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, content) in files)
        {
            var normalizedName = NormalizeAndValidateEntryName(name);
            if (content.LongLength > options.MaxFileBytes)
            {
                throw new InvalidOperationException(
                    $"Arquivo '{name}' ({content.LongLength} bytes) excede o limite de {options.MaxFileBytes} bytes.");
            }

            total += content.LongLength;
            if (total > options.MaxPackageTotalBytes)
            {
                throw new InvalidOperationException(
                    $"Conjunto de XSDs excede o limite de {options.MaxPackageTotalBytes} bytes.");
            }

            // DOCTYPE do xmldsig oficial (só entidade interna literal) é normalizado; o resto continua recusado.
            var normalized = Security.SafeDoctype.Normalize(content, normalizedName, options.MaxFileBytes);
            expandedTotal += normalized.LongLength;
            if (expandedTotal > options.MaxPackageTotalBytes)
            {
                throw new InvalidOperationException($"Conjunto de XSDs excede o limite de {options.MaxPackageTotalBytes} bytes depois de expandir entidades.");
            }

            entries[normalizedName] = normalized;
        }

        return entries;
    }

    private static (XmlSchemaSet SchemaSet, string ContentHash) LoadSchemaSet(
        Dictionary<string, byte[]> entries, string entryPointFileName, XsdImportSecurityOptions options)
    {
        var normalizedEntryPoint = entryPointFileName.Replace('\\', '/');
        if (!entries.TryGetValue(normalizedEntryPoint, out var entryPointBytes))
        {
            throw new InvalidOperationException(
                $"Arquivo principal '{entryPointFileName}' não encontrado no conjunto de XSDs.");
        }

        foreach (var (name, content) in entries)
        {
            Security.XmlDepthGuard.Check(content, name, options.MaxXmlDepth);
        }

        var resolver = new InMemoryPackageXmlResolver(entries);
        var schemaSet = new XmlSchemaSet { XmlResolver = resolver };
        AttachResolverFailureSurfacing(schemaSet);
        var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = resolver };

        using (var entryReader = XmlReader.Create(
            new MemoryStream(entryPointBytes), readerSettings, InMemoryPackageXmlResolver.EntryPointUri(normalizedEntryPoint).ToString()))
        {
            schemaSet.Add(targetNamespace: null, schemaDocument: entryReader);
        }

        // Arquivo enviado junto, mas que o principal não importa (ex.: XSD de provedor que usa
        // dsig:Signature sem xs:import do xmldsig): entra no conjunto se o namespace dele ainda não
        // está lá. Nada vem de fora do conjunto; o .NET resolve a referência e só avisa.
        var extraEntries = new List<string>();
        foreach (var name in entries.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            if (name == normalizedEntryPoint || resolver.LoadedEntriesInOrder.Contains(name))
            {
                continue;
            }

            var targetNamespace = TargetNamespaceOf(entries[name], readerSettings);
            if (targetNamespace is null || schemaSet.Contains(targetNamespace))
            {
                continue;
            }

            using var extraReader = XmlReader.Create(
                new MemoryStream(entries[name]), readerSettings, InMemoryPackageXmlResolver.EntryPointUri(name).ToString());
            schemaSet.Add(targetNamespace: null, schemaDocument: extraReader);
            extraEntries.Add(name);
        }

        var builtInXmlDsig = XmlDsig.AddBuiltInIfReferenced(schemaSet, entries.Values, readerSettings);

        XmlDsig.LeaveToCaller(schemaSet);
        CompileSchemaSet(schemaSet, options);

        var loadedEntries = new[] { normalizedEntryPoint }.Concat(resolver.LoadedEntriesInOrder).Concat(extraEntries).Distinct();
        var contentHash = ComputeContentHash(
            loadedEntries.OrderBy(p => p, StringComparer.Ordinal).Select(name => entries[name])
                .Concat(builtInXmlDsig is null ? [] : [builtInXmlDsig]));

        return (schemaSet, contentHash);
    }

    /// <summary>targetNamespace do xs:schema (vazio se não houver); null se não for um XSD.</summary>
    private static string? TargetNamespaceOf(byte[] content, XmlReaderSettings settings)
    {
        using var reader = XmlReader.Create(new MemoryStream(content), settings);
        if (reader.MoveToContent() != XmlNodeType.Element
            || reader.LocalName != "schema"
            || reader.NamespaceURI != XmlSchema.Namespace)
        {
            return null;
        }

        return reader.GetAttribute("targetNamespace") ?? string.Empty;
    }

    /// <summary>
    /// Por padrão, o compilador de XSD do .NET trata uma falha ao resolver
    /// <c>xs:include</c>/<c>xs:import</c> (inclusive as exceções lançadas
    /// pelo nosso <see cref="XmlResolver"/> de segurança) como um evento de
    /// validação recuperável, não como uma exceção propagada — o schema
    /// resultante fica silenciosamente incompleto. Isso mascararia toda
    /// mitigação de segurança em <c>InMemoryPackageXmlResolver</c>. Este handler força a falha de
    /// segurança a propagar como exceção real.
    /// </summary>
    private static void AttachResolverFailureSurfacing(XmlSchemaSet schemaSet)
    {
        schemaSet.ValidationEventHandler += (_, e) =>
        {
            for (Exception? exception = e.Exception; exception is not null; exception = exception.InnerException)
            {
                if (exception is InvalidOperationException resolverFailure)
                {
                    throw resolverFailure;
                }
            }

            // Erro de schema (ex.: grupo/tipo não declarado, referência circular) não é engolido:
            // o schema ficaria incompleto e a mensagem final seria enganosa.
            if (e.Severity == XmlSeverityType.Error)
            {
                throw e.Exception ?? new XmlSchemaException(e.Message);
            }
        };
    }

    private static string NormalizeAndValidateEntryName(string entryFullName)
    {
        var normalized = entryFullName.Replace('\\', '/');

        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"Arquivo '{entryFullName}' do conjunto de XSDs usa caminho absoluto — não permitido.");
        }

        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new InvalidOperationException(
                $"Arquivo '{entryFullName}' do conjunto de XSDs contém '..' — path traversal bloqueado " +
                "(seção 11 do requisito de origem).");
        }

        return normalized;
    }

    private static string ComputeContentHash(IEnumerable<byte[]> orderedFileContents)
    {
        using var sha256 = SHA256.Create();
        using var combined = new MemoryStream();
        foreach (var content in orderedFileContents)
        {
            combined.Write(content);
        }

        return Convert.ToHexStringLower(sha256.ComputeHash(combined.ToArray()));
    }

    private static ImportedXsd Build(XmlSchemaSet schemaSet, string? rootElementName, string contentHash)
    {
        var rootElement = ResolveRootElement(schemaSet, rootElementName);
        if (rootElement.ElementSchemaType is not XmlSchemaComplexType complexType)
        {
            throw new NotSupportedException(
                $"Elemento raiz '{rootElement.Name}' não tem um tipo complexo — não suportado.");
        }

        var rootContent = complexType.ContentTypeParticle switch
        {
            XmlSchemaGroupBase group => BuildParticle(group, new BuildContext(complexType), depth: 1),
            _ => throw new NotSupportedException(
                "O conteúdo do tipo complexo raiz precisa ser xs:sequence, xs:choice ou xs:all."),
        };

        var schema = new DestinationSchema
        {
            RootElementName = rootElement.Name ?? throw new InvalidOperationException("Elemento raiz sem nome."),
            TargetNamespace = rootElement.QualifiedName.Namespace is { Length: > 0 } ns ? ns : null,
            RootContent = rootContent,
            Attributes = ExtractAttributes(complexType),
        };

        return new ImportedXsd { Schema = schema, SchemaSet = schemaSet, ContentHash = contentHash };
    }

    private static XmlSchemaElement ResolveRootElement(XmlSchemaSet schemaSet, string? rootElementName)
    {
        var globalElements = schemaSet.GlobalElements.Values.OfType<XmlSchemaElement>().ToList();

        if (rootElementName is not null)
        {
            return globalElements.FirstOrDefault(e => e.Name == rootElementName)
                ?? throw new InvalidOperationException($"Elemento global '{rootElementName}' não encontrado no schema.");
        }

        if (globalElements.Count == 1)
        {
            return globalElements[0];
        }

        // Sem os elementos do xmldsig, sobrando um só, é ele (ex.: DPS_v1.01.xsd → DPS).
        var candidates = globalElements.Where(e => !XmlDsig.IsDsigElement(e)).ToList();
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (candidates.Count == 0)
        {
            // Ex.: tiposSimples_v1.01.xsd escolhido como principal — só tipos, nenhum elemento que possa ser a raiz.
            throw new InvalidOperationException(
                "O arquivo principal não declara nenhum elemento que possa ser a raiz (só tipos ou só assinatura); escolha o XSD do documento (ex.: DPS_v1.01.xsd) como arquivo principal.");
        }

        throw new InvalidOperationException(
            "O schema declara mais de um elemento global; informe o elemento raiz (rootElementName) — candidatos: " +
            string.Join(", ", candidates.Select(e => e.QualifiedName.Name).Order(StringComparer.Ordinal).Take(20)) + ".");
    }

    /// <summary>Estado da montagem do destino: tipos em visita (recursão), nós gastos (orçamento).</summary>
    private sealed class BuildContext(XmlSchemaComplexType root)
    {
        public HashSet<XmlSchemaComplexType> Visiting { get; } = new(ReferenceEqualityComparer.Instance) { root };

        private int _nodes;

        public void Enter(int depth)
        {
            if (depth > ExpandedTreeLimits.MaxDepth)
            {
                throw ExpandedTreeLimits.TooDeep("de destino");
            }

            if (++_nodes > ExpandedTreeLimits.MaxNodes)
            {
                throw ExpandedTreeLimits.TooLarge("de destino");
            }
        }
    }

    private static SchemaParticle BuildParticle(XmlSchemaGroupBase group, BuildContext context, int depth)
    {
        context.Enter(depth);
        var kind = group switch
        {
            XmlSchemaSequence => ParticleKind.Sequence,
            XmlSchemaChoice => ParticleKind.Choice,
            XmlSchemaAll => ParticleKind.All,
            _ => throw new NotSupportedException($"Grupo de partícula '{group.GetType().Name}' não suportado."),
        };

        return new SchemaParticle
        {
            Kind = kind,
            Cardinality = new Cardinality(
                MinOccurs: checked((int)group.MinOccurs),
                MaxOccurs: group.MaxOccursString == "unbounded" ? null : checked((int)group.MaxOccurs)),
            Items = group.Items.OfType<XmlSchemaParticle>().Select(item => BuildParticleItem(item, context, depth + 1)).ToList(),
        };
    }

    private static SchemaParticleItem BuildParticleItem(XmlSchemaParticle particle, BuildContext context, int depth) => particle switch
    {
        XmlSchemaElement element => BuildElementItem(element, context, depth),
        XmlSchemaGroupBase group => new GroupItem { Group = BuildParticle(group, context, depth) },
        // xs:any: conteúdo opaco, nunca mapeável nem gerado — um grupo vazio com a
        // cardinalidade dele. O elemento que o contém fica só leitura no editor.
        XmlSchemaAny any => new GroupItem
        {
            Group = new SchemaParticle
            {
                Kind = ParticleKind.Sequence,
                Cardinality = new Cardinality(
                    MinOccurs: checked((int)any.MinOccurs),
                    MaxOccurs: any.MaxOccursString == "unbounded" ? null : checked((int)any.MaxOccurs)),
                Items = [],
            },
        },
        _ => throw new NotSupportedException(
            $"Item de partícula '{particle.GetType().Name}' não suportado."),
    };

    private static SchemaParticleItem BuildElementItem(XmlSchemaElement element, BuildContext context, int depth)
    {
        context.Enter(depth);
        var visiting = context.Visiting;
        // Elemento por referência (ref="ds:Signature") não tem Name: o nome vem da referência resolvida na compilação.
        var name = element.QualifiedName.Name is { Length: > 0 } qualified ? qualified : element.Name ?? throw new InvalidOperationException("Elemento sem nome.");
        var cardinality = new Cardinality(
            MinOccurs: checked((int)element.MinOccurs),
            MaxOccurs: element.MaxOccursString == "unbounded" ? null : checked((int)element.MaxOccurs));
        var nillability = element.IsNillable ? Nillability.Nillable : Nillability.NotNillable;

        if (XmlDsig.IsSignature(element.QualifiedName))
        {
            // A estrutura interna do xmldsig não é do DE-PARA: quem chama o serviço assina depois.
            return new ComplexElementItem
            {
                Name = name,
                Cardinality = cardinality,
                Nillability = nillability,
                Content = OpaqueContent,
                SignedByCaller = true,
            };
        }

        if (element.ElementSchemaType is XmlSchemaComplexType complexType)
        {
            // Conteúdo que o modelo de destino não expressa (simples com atributos, vazio —
            // ex.: ds:SignatureValue): elemento OPACO — aparece no editor, só leitura;
            // obrigatório vira MAPPING_MISSING_REQUIRED (nunca silêncio).
            // Tipo que contém a si mesmo: o XML de destino é gerado pela árvore inteira do schema,
            // então recursão não tem fim — recusa com 400 (antes estourava a pilha).
            if (!visiting.Add(complexType))
            {
                throw new NotSupportedException(
                    $"O XSD de destino tem tipo recursivo em '{name}' (o tipo contém a si mesmo); isso não é suportado no destino.");
            }

            var nestedContent = complexType.ContentTypeParticle is XmlSchemaGroupBase group
                ? BuildParticle(group, context, depth + 1)
                : OpaqueContent;
            visiting.Remove(complexType);

            return new ComplexElementItem
            {
                Name = name,
                Cardinality = cardinality,
                Nillability = nillability,
                Content = nestedContent,
            };
        }

        return new ScalarElementItem
        {
            Element = new SchemaElement
            {
                Name = name,
                Cardinality = cardinality,
                Nillability = nillability,
                ScalarType = ResolveScalarType(element.ElementSchemaType),
                DefaultValue = element.DefaultValue,
                Facets = XsdFacetReader.Read(element.ElementSchemaType as XmlSchemaSimpleType),
            },
        };
    }

    private static IReadOnlyList<SchemaAttribute> ExtractAttributes(XmlSchemaComplexType complexType)
    {
        var result = new List<SchemaAttribute>();
        foreach (var value in complexType.AttributeUses.Values)
        {
            if (value is not XmlSchemaAttribute attribute)
            {
                continue;
            }

            result.Add(new SchemaAttribute
            {
                Name = attribute.QualifiedName.Name is { Length: > 0 } qualified ? qualified : attribute.Name ?? throw new InvalidOperationException("Atributo sem nome."),
                FixedValue = attribute.FixedValue,
            });
        }

        return result;
    }

    // O compilador do .NET lê facetas de tamanho como int: um XSD válido com
    // maxLength="99999999999" estoura (OverflowException) em vez de erro de
    // schema. Vira erro de importação — 400, nunca 500 (revisão da Fase 8, B3).
    private static void CompileSchemaSet(XmlSchemaSet schemaSet, XsdImportSecurityOptions options)
    {
        try
        {
            // Antes de compilar: o autômato de conteúdo do .NET explode com milhares de elementos num bloco.
            Security.ContentModelSizeChecker.Check(schemaSet, options);
            schemaSet.Compile();
            Security.PatternSafetyChecker.Check(schemaSet, options);
        }
        catch (OverflowException ex)
        {
            throw new XmlSchemaException($"Faceta numérica acima do suportado pelo validador (máx. {int.MaxValue}): {ex.Message}", ex);
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException ex)
        {
            // default/fixed validado contra pattern patológico na compilação do
            // schema (2ª revisão da Fase 9, A1): erro de importação, não 500.
            throw new XmlSchemaException("Um pattern do XSD excedeu o tempo limite ao validar um valor default/fixed do próprio schema.", ex);
        }
    }

    private static SchemaScalarType ResolveScalarType(XmlSchemaType? schemaType)
    {
        if (schemaType is not XmlSchemaSimpleType simpleType || simpleType.Datatype is null)
        {
            throw new NotSupportedException(
                $"Tipo de elemento '{schemaType?.Name}' não é um tipo simples escalar suportado.");
        }

        // Decimal/date/gYearMonth/dateTime têm formatação própria no motor; os demais tipos simples
        // (integer e derivados, boolean, base64Binary, anyURI…) são escritos como o
        // texto recebido e a validação XSD do XML gerado aplica o tipo real — nunca em silêncio.
        return simpleType.Datatype.TypeCode switch
        {
            XmlTypeCode.Decimal => SchemaScalarType.Decimal,
            XmlTypeCode.Date => SchemaScalarType.Date,
            XmlTypeCode.GYearMonth => SchemaScalarType.YearMonth,
            XmlTypeCode.DateTime => SchemaScalarType.DateTime,
            _ => SchemaScalarType.String,
        };
    }

    /// <summary>Conteúdo opaco: um grupo vazio — o elemento fica não mapeável (ver uso).</summary>
    private static SchemaParticle OpaqueContent => new()
    {
        Kind = ParticleKind.Sequence,
        Cardinality = Cardinality.Single,
        Items = [new GroupItem { Group = new SchemaParticle { Kind = ParticleKind.Sequence, Cardinality = Cardinality.Optional, Items = [] } }],
    };
}
