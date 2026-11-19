using System.Text.Json;
using Tradux.Domain.SourceData;
using Xunit;

namespace Tradux.Domain.Tests;

/// <summary>
/// Entrada JSON → árvore interna (seção 10, docs/api/README.md §1).
/// </summary>
public class SourceDataTreeJsonTests
{
    private static SourceDataTree Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return SourceDataTree.FromJson(document.RootElement.Clone());
    }

    [Fact]
    public void Numbers_become_decimal_preserving_scale()
    {
        var tree = Parse("""{ "Valor": 1500.50, "Inteiro": 3 }""");

        Assert.True(tree.TryGetValue("Valor", out var valor));
        Assert.Equal(1500.50m, Assert.IsType<decimal>(valor));
        Assert.Equal("1500.50", ((decimal)valor!).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(tree.TryGetValue("Inteiro", out var inteiro));
        Assert.Equal(3m, Assert.IsType<decimal>(inteiro));
    }

    [Fact]
    public void Explicit_null_is_distinct_from_absent_property()
    {
        var tree = Parse("""{ "Serie": null }""");

        Assert.True(tree.TryGetValue("Serie", out var serie));
        Assert.Null(serie);
        Assert.False(tree.TryGetValue("Numero", out _));
    }

    [Fact]
    public void Paths_are_case_sensitive_like_csharp_property_names()
    {
        var tree = Parse("""{ "numero": "1" }""");

        Assert.False(tree.TryGetValue("Numero", out _));
        Assert.True(tree.TryGetValue("numero", out _));
    }

    [Fact]
    public void Array_of_objects_becomes_list_whose_items_build_their_own_tree()
    {
        var tree = Parse("""{ "Itens": [ { "Descricao": "A", "Valor": 1 }, { "Descricao": "B" } ] }""");

        Assert.True(tree.TryGetValue("Itens", out var itens));
        var list = Assert.IsAssignableFrom<IReadOnlyList<object?>>(itens);
        Assert.Equal(2, list.Count);

        var itemTree = SourceDataTree.FromItem(list[0], new Dictionary<string, object?> { ["$index"] = 0m }, tree);
        Assert.True(itemTree.TryGetValue("Descricao", out var descricao));
        Assert.Equal("A", descricao);
        Assert.True(itemTree.TryGetValue("$index", out var index));
        Assert.Equal(0m, index);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("null")]
    public void Non_object_root_is_rejected(string json)
    {
        Assert.Throws<ArgumentException>(() => Parse(json));
    }

    [Fact]
    public void Number_out_of_decimal_range_is_rejected_not_silently_rounded()
    {
        Assert.Throws<ArgumentException>(() => Parse("""{ "Valor": 1e400 }"""));
    }
}

/// <summary>Caminhos com ponto (Fase 6): navegação de objetos aninhados.</summary>
public class SourceDataTreeNestedPathTests
{
    private static SourceDataTree FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return SourceDataTree.FromJson(document.RootElement.Clone());
    }

    [Fact]
    public void Dotted_path_reaches_nested_value()
    {
        var tree = FromJson("""{ "Tomador": { "Cnpj": "123", "Endereco": { "Cidade": "Recife" } } }""");

        Assert.True(tree.TryGetValue("Tomador.Cnpj", out var cnpj));
        Assert.Equal("123", cnpj);
        Assert.True(tree.TryGetValue("Tomador.Endereco.Cidade", out var cidade));
        Assert.Equal("Recife", cidade);
    }

    [Fact]
    public void Null_intermediate_is_present_and_null_missing_member_is_absent()
    {
        var tree = FromJson("""{ "Tomador": { "Cnpj": "123", "Endereco": null } }""");

        Assert.True(tree.TryGetValue("Tomador.Endereco.Cidade", out var cidade));
        Assert.Null(cidade);
        Assert.False(tree.TryGetValue("Tomador.NaoExiste", out _));
        Assert.False(tree.TryGetValue("NaoExiste.Cnpj", out _));
    }

    [Fact]
    public void Exact_key_with_a_dot_wins_over_navigation()
    {
        var tree = new SourceDataTree(new Dictionary<string, object?> { ["a.b"] = "exato" });

        Assert.True(tree.TryGetValue("a.b", out var value));
        Assert.Equal("exato", value);
    }

    [Fact]
    public void Text_and_collections_are_not_navigable()
    {
        var tree = FromJson("""{ "Texto": "abc", "Itens": [ { "A": 1 } ] }""");

        Assert.False(tree.TryGetValue("Texto.Length", out _));
        Assert.False(tree.TryGetValue("Itens.A", out _));
    }
}
