using System.Xml;
using System.Xml.Schema;

namespace Tradux.Xsd;

/// <summary>Repassa tudo ao leitor interno (inclusive posição); subclasses interceptam <see cref="Read"/>.</summary>
internal abstract class DelegatingXmlReader(XmlReader inner) : XmlReader, IXmlLineInfo
{
    protected XmlReader Inner { get; } = inner;

    public override int AttributeCount => Inner.AttributeCount;
    public override string BaseURI => Inner.BaseURI;
    public override int Depth => Inner.Depth;
    public override bool EOF => Inner.EOF;
    public override bool HasValue => Inner.HasValue;
    public override bool IsDefault => Inner.IsDefault;
    public override bool IsEmptyElement => Inner.IsEmptyElement;
    public override string LocalName => Inner.LocalName;
    public override string Name => Inner.Name;
    public override string NamespaceURI => Inner.NamespaceURI;
    public override XmlNameTable NameTable => Inner.NameTable;
    public override XmlNodeType NodeType => Inner.NodeType;
    public override string Prefix => Inner.Prefix;
    public override ReadState ReadState => Inner.ReadState;
    public override IXmlSchemaInfo? SchemaInfo => Inner.SchemaInfo;
    public override string Value => Inner.Value;
    public override string XmlLang => Inner.XmlLang;
    public override XmlSpace XmlSpace => Inner.XmlSpace;

    public override string GetAttribute(int i) => Inner.GetAttribute(i);
    public override string? GetAttribute(string name) => Inner.GetAttribute(name);
    public override string? GetAttribute(string name, string? namespaceURI) => Inner.GetAttribute(name, namespaceURI);
    public override string? LookupNamespace(string prefix) => Inner.LookupNamespace(prefix);
    public override void MoveToAttribute(int i) => Inner.MoveToAttribute(i);
    public override bool MoveToAttribute(string name) => Inner.MoveToAttribute(name);
    public override bool MoveToAttribute(string name, string? ns) => Inner.MoveToAttribute(name, ns);
    public override bool MoveToElement() => Inner.MoveToElement();
    public override bool MoveToFirstAttribute() => Inner.MoveToFirstAttribute();
    public override bool MoveToNextAttribute() => Inner.MoveToNextAttribute();
    public override bool ReadAttributeValue() => Inner.ReadAttributeValue();
    public override void ResolveEntity() => Inner.ResolveEntity();
    public override bool Read() => Inner.Read();

    public bool HasLineInfo() => Inner is IXmlLineInfo info && info.HasLineInfo();
    public int LineNumber => Inner is IXmlLineInfo info ? info.LineNumber : 0;
    public int LinePosition => Inner is IXmlLineInfo info ? info.LinePosition : 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Aplica profundidade e quantidade de elementos <b>durante</b> a leitura
/// (revisão da Fase 13, achados 1 e 12): nada recursivo nem quadrático chega
/// a ver uma árvore acima dos limites.
/// </summary>
internal sealed class LimitedXmlReader(XmlReader inner, SourceXmlLimits limits, Deadline deadline) : DelegatingXmlReader(inner)
{
    private int _elements;

    public override bool Read()
    {
        if (!Inner.Read())
        {
            return false;
        }

        if (deadline.Expired)
        {
            throw Exceeded($"Validação do XML de origem excedeu o tempo limite ({limits.MaxValidationTime.TotalSeconds:0.#} s): o XSD de origem tem restrições caras (pattern, xs:unique/xs:key) para este volume.");
        }

        if (Inner.NodeType == XmlNodeType.Element)
        {
            // Profundidade 0 = raiz; a conversão aceita até MaxDepth níveis de objeto + o valor.
            if (Inner.Depth > limits.MaxDepth)
            {
                throw Exceeded($"XML de origem com profundidade acima de {limits.MaxDepth}.");
            }

            if (++_elements > limits.MaxElements)
            {
                throw Exceeded($"XML de origem com mais de {limits.MaxElements} elementos.");
            }
        }

        return true;
    }

    private SourceXmlException Exceeded(string message) => new([new SourceXmlProblem(LineNumber, LinePosition, message)]);
}

/// <summary>
/// Pula as subárvores de extensão (ADR-0016 §3) para validar o restante pelo
/// XSD base em uma passagem só, sem alterar o documento. Elementos são
/// reconhecidos pela posição de início (única por elemento no texto lido).
/// </summary>
internal sealed class SkippingXmlReader(XmlReader inner, IReadOnlyCollection<(int Line, int Position)> skip) : DelegatingXmlReader(inner)
{
    public override bool Read()
    {
        if (!Inner.Read())
        {
            return false;
        }

        while (Inner.NodeType == XmlNodeType.Element && skip.Contains((LineNumber, LinePosition)))
        {
            Inner.Skip();
            if (Inner.ReadState != ReadState.Interactive)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Prazo absoluto (relógio monotônico) de uma leitura.</summary>
internal readonly struct Deadline(TimeSpan budget)
{
    private readonly long _end = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    public bool Expired => System.Diagnostics.Stopwatch.GetTimestamp() > _end;
}
