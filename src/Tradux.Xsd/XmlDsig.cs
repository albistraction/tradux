using System.Text;
using System.Xml;
using System.Xml.Schema;

namespace Tradux.Xsd;

/// <summary>
/// Tudo o que a importação sabe sobre a assinatura XML-DSig (<c>ds:Signature</c>).
/// Quem chama o serviço assina o XML convertido (ADR-0025); o motor nunca a mapeia
/// nem gera. Quando o XSD a exige — como o da Agili,
/// <c>choice(ChaveDigital | dsig:Signature)</c> obrigatório —, o XML antes da
/// assinatura precisa ser válido sem ela (<see cref="LeaveToCaller"/>).
/// </summary>
internal static class XmlDsig
{
    public const string Namespace = "http://www.w3.org/2000/09/xmldsig#";

    private const string SignatureName = "Signature";

    private const string BuiltInResource = "Tradux.Xsd.xmldsig-core-schema.xsd";

    private const string BuiltInUri = "tradux-builtin:///xmldsig-core-schema.xsd";

    public static bool IsSignature(XmlQualifiedName name) => name.Namespace == Namespace && name.Name == SignatureName;

    /// <summary>Elementos globais do xmldsig (Signature, KeyInfo…) nunca são a raiz de um documento.</summary>
    public static bool IsDsigElement(XmlSchemaElement element) => element.QualifiedName.Namespace == Namespace;

    /// <summary>
    /// XSD que usa <c>ds:Signature</c> sem mandar o xmldsig (nem por import nem junto):
    /// entra a cópia embutida do W3C, a mesma dos pacotes fiscais.
    /// </summary>
    /// <returns>Os bytes adicionados (entram no hash), ou nulo se nada foi adicionado.</returns>
    public static byte[]? AddBuiltInIfReferenced(XmlSchemaSet schemaSet, IEnumerable<byte[]> files, XmlReaderSettings readerSettings)
    {
        if (schemaSet.Contains(Namespace) || !files.Any(content => Encoding.UTF8.GetString(content).Contains(Namespace)))
        {
            return null;
        }

        using var stream = typeof(XmlDsig).Assembly.GetManifestResourceStream(BuiltInResource)
            ?? throw new InvalidOperationException("Schema xmldsig embutido não encontrado.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var xmlDsig = buffer.ToArray();
        using var reader = XmlReader.Create(new MemoryStream(xmlDsig), readerSettings, BuiltInUri);
        schemaSet.Add(targetNamespace: null, schemaDocument: reader);
        return xmlDsig;
    }

    /// <summary>
    /// A referência a <c>ds:Signature</c> vira opcional, e o <c>xs:choice</c> que tem a
    /// assinatura como ramo também (sem mapeamento, nenhum ramo sai e quem chama assina).
    /// Aplicado antes da compilação, vale para a árvore do editor, o compilador e a
    /// validação do XML gerado.
    /// </summary>
    public static void LeaveToCaller(XmlSchemaSet schemaSet)
    {
        foreach (XmlSchema schema in schemaSet.Schemas())
        {
            if (schema.TargetNamespace == Namespace)
            {
                continue; // o próprio xmldsig: estrutura interna da assinatura
            }

            foreach (var item in schema.Items)
            {
                switch (item)
                {
                    case XmlSchemaComplexType type:
                        Visit(type);
                        break;
                    case XmlSchemaGroup group:
                        Visit(group.Particle);
                        break;
                    case XmlSchemaElement element:
                        Visit(element.SchemaType as XmlSchemaComplexType);
                        break;
                }
            }
        }
    }

    private static void Visit(XmlSchemaComplexType? type)
    {
        if (type is null)
        {
            return;
        }

        Visit(type.Particle);
        switch (type.ContentModel?.Content)
        {
            case XmlSchemaComplexContentExtension extension:
                Visit(extension.Particle);
                break;
            case XmlSchemaComplexContentRestriction restriction:
                Visit(restriction.Particle);
                break;
        }
    }

    private static void Visit(XmlSchemaParticle? particle)
    {
        switch (particle)
        {
            case XmlSchemaElement element when IsSignature(element.RefName):
                element.MinOccurs = 0;
                break;
            case XmlSchemaElement element:
                Visit(element.SchemaType as XmlSchemaComplexType);
                break;
            case XmlSchemaGroupBase group:
                foreach (var child in group.Items.OfType<XmlSchemaParticle>())
                {
                    Visit(child);
                }

                if (group is XmlSchemaChoice && group.Items.OfType<XmlSchemaElement>().Any(e => IsSignature(e.RefName)))
                {
                    group.MinOccurs = 0;
                }

                break;
        }
    }
}
