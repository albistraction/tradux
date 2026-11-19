using System.Linq.Expressions;
using Tradux.Domain.Mapping;

namespace Tradux.Engine.Operators;

/// <summary>
/// Declaração de um operador do catálogo (seção 13 do requisito de origem:
/// "cada operador declara tipos aceitos, retorno, parâmetros, validações...
/// e compatibilidade com coleções"). <see cref="Build"/> recebe os
/// argumentos já compilados (expressões de tipo <c>object</c>) e monta a
/// expressão final — tipicamente uma chamada a um método estático de
/// <see cref="RuleOperatorMethods"/>. Recebe também os nós de regra
/// originais (para operadores como <c>lookup</c>, cujo nome de tabela
/// precisa ser um literal resolvido em tempo de compilação, não uma
/// expressão executável) e o <see cref="OperatorBuildContext"/>.
/// </summary>
/// <remarks>
/// Novos operadores são adicionáveis por qualquer desenvolvedor criando uma
/// nova instância e registrando-a em um <see cref="RuleOperatorCatalog"/> —
/// nunca é preciso alterar o núcleo do motor (seção 13: "extensão segura por
/// novos operadores C#").
/// </remarks>
public sealed record RuleOperatorDefinition(
    string Name,
    int MinArguments,
    int MaxArguments,
    Func<IReadOnlyList<RuleNode>, IReadOnlyList<Expression>, OperatorBuildContext, Expression> Build);
