using System.Linq.Expressions;
using System.Reflection;
using Tradux.Domain.Mapping;

namespace Tradux.Engine.Operators;

/// <summary>
/// Catálogo de operadores disponíveis para compilação (seção 13 do
/// requisito de origem). <see cref="CreateDefault"/> registra os operadores
/// iniciais da seção 13 (mais ifElse e truncate); os metadados deles para o
/// editor ficam em <see cref="RuleOperatorMetadata.BuiltIn"/>, com as mesmas chaves. Novos operadores — inclusive
/// de terceiros — são adicionados via <see cref="Register(RuleOperatorDefinition)"/>, sem alterar
/// nada deste arquivo ("extensão segura por novos operadores C#").
/// </summary>
public sealed class RuleOperatorCatalog
{
    private readonly Dictionary<string, RuleOperatorDefinition> _operators = new(StringComparer.Ordinal);

    public void Register(RuleOperatorDefinition definition) => _operators[definition.Name] = definition;

    /// <summary>Metadados opcionais de operadores registrados (customizados); os iniciais usam <see cref="RuleOperatorMetadata.BuiltIn"/>.</summary>
    private readonly Dictionary<string, RuleOperatorMetadata> _metadata = new(StringComparer.Ordinal);

    /// <summary>Registra um operador junto com a descrição que o editor visual mostra.</summary>
    public void Register(RuleOperatorDefinition definition, RuleOperatorMetadata metadata)
    {
        Register(definition);
        _metadata[definition.Name] = metadata;
    }

    /// <summary>
    /// Todos os operadores registrados com seus metadados, em ordem de nome —
    /// é o que o editor visual lista (seção 12: o usuário só compõe regras a
    /// partir do catálogo; nunca escreve código).
    /// </summary>
    public IReadOnlyList<(RuleOperatorDefinition Definition, RuleOperatorMetadata Metadata)> Describe() =>
        _operators.Values
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => (d, _metadata.TryGetValue(d.Name, out var custom) ? custom
                : RuleOperatorMetadata.BuiltIn.TryGetValue(d.Name, out var builtIn) ? builtIn
                : RuleOperatorMetadata.Generic(d)))
            .ToList();

    public RuleOperatorDefinition Resolve(string name) =>
        _operators.TryGetValue(name, out var definition)
            ? definition
            : throw new NotSupportedException(
                $"Operador '{name}' não está registrado neste catálogo. Operadores customizados devem ser " +
                $"registrados via {nameof(RuleOperatorCatalog)}.{nameof(Register)} antes de compilar o template.");

    public static RuleOperatorCatalog CreateDefault()
    {
        var catalog = new RuleOperatorCatalog();

        Register1(catalog, "isEmpty", nameof(RuleOperatorMethods.IsEmpty));
        Register1(catalog, "notEmpty", nameof(RuleOperatorMethods.NotEmpty));
        Register1(catalog, "not", nameof(RuleOperatorMethods.Not));

        Register1(catalog, "copy", nameof(RuleOperatorMethods.Copy));
        Register1(catalog, "trim", nameof(RuleOperatorMethods.Trim));

        // "constant" é "copy" aplicado a um literal; a seção 13 o lista pelo nome.
        Register1(catalog, "constant", nameof(RuleOperatorMethods.Copy));

        Register2(catalog, "equals", nameof(RuleOperatorMethods.AreEqual));
        Register2(catalog, "notEquals", nameof(RuleOperatorMethods.NotEquals));
        Register2(catalog, "greaterThan", nameof(RuleOperatorMethods.GreaterThan));
        Register2(catalog, "lessThan", nameof(RuleOperatorMethods.LessThan));
        Register2(catalog, "setDefault", nameof(RuleOperatorMethods.SetDefault));
        Register2(catalog, "formatDate", nameof(RuleOperatorMethods.FormatDate));
        Register2(catalog, "formatNumber", nameof(RuleOperatorMethods.FormatNumber));

        Register2(catalog, "truncate", nameof(RuleOperatorMethods.Truncate));

        // Se/senão: só o ramo escolhido é avaliado (o outro pode falhar sem dado, ex.: substring)
        RegisterIfElse(catalog);

        Register3(catalog, "substring", nameof(RuleOperatorMethods.Substring));
        Register3(catalog, "replace", nameof(RuleOperatorMethods.Replace));

        RegisterVariadic(catalog, "and", nameof(RuleOperatorMethods.And));
        RegisterVariadic(catalog, "or", nameof(RuleOperatorMethods.Or));
        RegisterVariadic(catalog, "coalesce", nameof(RuleOperatorMethods.Coalesce));
        RegisterVariadic(catalog, "concat", nameof(RuleOperatorMethods.Concat));

        RegisterPad(catalog, "padLeft", nameof(RuleOperatorMethods.PadLeft));
        RegisterPad(catalog, "padRight", nameof(RuleOperatorMethods.PadRight));

        Register0(catalog, "setNull", nameof(RuleOperatorMethods.SetNull));
        Register0(catalog, "omit", nameof(RuleOperatorMethods.Omit));

        // Tabelas DE-PARA (nome da tabela é literal, resolvido em tempo de compilação)
        RegisterLookup(catalog);

        return catalog;
    }

    private static MethodInfo Method(string name) =>
        typeof(RuleOperatorMethods).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Método '{name}' não encontrado em {nameof(RuleOperatorMethods)}.");

    private static void Register0(RuleOperatorCatalog catalog, string name, string methodName)
    {
        var method = Method(methodName);
        catalog.Register(new RuleOperatorDefinition(name, 0, 0, (_, _, _) => Expression.Call(method)));
    }

    private static void Register1(RuleOperatorCatalog catalog, string name, string methodName)
    {
        var method = Method(methodName);
        catalog.Register(new RuleOperatorDefinition(name, 1, 1, (_, args, _) => Expression.Call(method, args[0])));
    }

    private static void Register2(RuleOperatorCatalog catalog, string name, string methodName)
    {
        var method = Method(methodName);
        catalog.Register(new RuleOperatorDefinition(name, 2, 2, (_, args, _) => Expression.Call(method, args[0], args[1])));
    }

    private static void Register3(RuleOperatorCatalog catalog, string name, string methodName)
    {
        var method = Method(methodName);
        catalog.Register(new RuleOperatorDefinition(name, 3, 3, (_, args, _) => Expression.Call(method, args[0], args[1], args[2])));
    }

    private static void RegisterVariadic(RuleOperatorCatalog catalog, string name, string methodName)
    {
        var method = Method(methodName);
        catalog.Register(new RuleOperatorDefinition(name, 1, int.MaxValue, (_, args, _) =>
            Expression.Call(method, Expression.NewArrayInit(typeof(object), args))));
    }

    private static void RegisterPad(RuleOperatorCatalog catalog, string name, string methodName)
    {
        var method = Method(methodName);
        catalog.Register(new RuleOperatorDefinition(name, 2, 3, (_, args, _) =>
        {
            var paddingChar = args.Count == 3 ? args[2] : Expression.Constant(null, typeof(object));
            return Expression.Call(method, args[0], args[1], paddingChar);
        }));
    }

    private static void RegisterIfElse(RuleOperatorCatalog catalog)
    {
        var test = typeof(ScalarValues).GetMethod(nameof(ScalarValues.ToBool), BindingFlags.Public | BindingFlags.Static)!;
        catalog.Register(new RuleOperatorDefinition("ifElse", 3, 3, (_, args, _) =>
            Expression.Condition(Expression.Call(test, args[0]), Expression.Convert(args[1], typeof(object)), Expression.Convert(args[2], typeof(object)))));
    }

    private static void RegisterLookup(RuleOperatorCatalog catalog)
    {
        var method = Method(nameof(RuleOperatorMethods.Lookup));
        var withDefault = Method(nameof(RuleOperatorMethods.LookupOrDefault));

        // 3º argumento opcional: valor padrão quando a chave é nula ou não está na tabela.
        catalog.Register(new RuleOperatorDefinition("lookup", 2, 3, (rawArgs, compiledArgs, context) =>
        {
            if (rawArgs[1] is not LiteralNode { Value: string tableName })
            {
                throw new NotSupportedException(
                    "O segundo argumento de 'lookup' precisa ser um nome de tabela literal " +
                    "(LiteralNode com string) — não uma expressão calculada.");
            }

            if (!context.LookupTables.TryGetValue(tableName, out var table))
            {
                throw new InvalidOperationException(
                    $"Tabela de lookup '{tableName}' não foi registrada " +
                    "(ver parâmetro lookupTables de TemplateCompiler.Compile).");
            }

            var tableConstant = Expression.Constant(table, typeof(IReadOnlyDictionary<string, string>));
            return compiledArgs.Count == 3
                ? Expression.Call(withDefault, compiledArgs[0], tableConstant, compiledArgs[2])
                : Expression.Call(method, compiledArgs[0], tableConstant);
        }));
    }
}
