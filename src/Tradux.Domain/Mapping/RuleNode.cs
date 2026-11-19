using System.Text.Json.Serialization;

namespace Tradux.Domain.Mapping;

/// <summary>
/// Nó de uma expressão de regra (seção 13 do requisito de origem). É dado
/// puro — uma configuração declarativa, nunca código executável do usuário
/// (seção 12/28: "nenhum código arbitrário é executado"). O motor
/// (<c>Tradux.Engine.Compilation.TemplateCompiler</c>) compila esta
/// árvore, uma única vez, em um delegate real via
/// <see cref="System.Linq.Expressions"/> — ele nunca a percorre e
/// reinterpreta a cada geração (ADR-0001).
///
/// Anotado para serialização polimórfica com <c>System.Text.Json</c> (parte
/// do runtime .NET, não um pacote de infraestrutura) — necessário para
/// persistir um <c>MappingTemplate</c> publicado. Isso não é uma dependência de
/// framework/infraestrutura na acepção do ADR-0007/CONTAINERS.md (que veta
/// ASP.NET Core, MongoDB e frontend em Domain) — é serialização de biblioteca
/// padrão do próprio modelo de valor do domínio.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FieldReferenceNode), "field")]
[JsonDerivedType(typeof(LiteralNode), "literal")]
[JsonDerivedType(typeof(OperatorNode), "operator")]
public abstract record RuleNode;

/// <summary>De qual escopo da entrada um <see cref="FieldReferenceNode"/> lê (ADR-0020).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FieldScope>))]
public enum FieldScope
{
    /// <summary>Escopo atual: a raiz, ou o item do bloco "for each" em que a regra está.</summary>
    [JsonStringEnumMemberName("current")]
    Current,

    /// <summary>Raiz da entrada, mesmo dentro de um bloco "for each".</summary>
    [JsonStringEnumMemberName("root")]
    Root,
}

/// <summary>
/// Referência a um caminho da árvore de dados de origem. <see cref="Scope"/>
/// diz de onde ler; o padrão (escopo atual) não é serializado, então o
/// conteúdo e o hash de templates existentes não mudam.
/// </summary>
public sealed record FieldReferenceNode(
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] FieldScope Scope = FieldScope.Current) : RuleNode;

/// <summary>Um valor constante embutido na regra (operador <c>constant</c> da seção 13).</summary>
public sealed record LiteralNode([property: JsonConverter(typeof(LiteralValueJsonConverter))] object? Value) : RuleNode;

/// <summary>
/// Aplicação de um operador do catálogo (seção 13) a um número fixo de
/// argumentos, cada um por sua vez uma <see cref="RuleNode"/> (permitindo
/// composição, ex.: <c>concat(trim(A), " ", trim(B))</c>).
/// </summary>
public sealed record OperatorNode(string OperatorName, IReadOnlyList<RuleNode> Arguments) : RuleNode
{
    /// <summary>
    /// Forma conveniente para compor operadores inline em código C#
    /// (<c>OperatorNode.Of("concat", a, b)</c>). Não é um segundo
    /// constructor de propósito — <c>System.Text.Json</c> não sabe escolher
    /// entre dois construtores sem <c>[JsonConstructor]</c>, e o construtor
    /// posicional do record precisa continuar sendo o único, para gerar as
    /// propriedades <see cref="OperatorName"/>/<see cref="Arguments"/> que a
    /// serialização usa.
    /// </summary>
    public static OperatorNode Of(string operatorName, params RuleNode[] arguments) => new(operatorName, arguments);
}
