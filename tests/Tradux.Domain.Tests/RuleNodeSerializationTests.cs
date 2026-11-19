using System.Text.Json;
using Tradux.Domain.Mapping;
using Xunit;

namespace Tradux.Domain.Tests;

/// <summary>
/// Serialização de <see cref="RuleNode"/> é necessária desde a Fase 5 para
/// persistir um <see cref="MappingTemplate"/> publicado. O ponto delicado é
/// que <see cref="LiteralNode.Value"/> precisa voltar com o tipo CLR
/// original (não um <see cref="JsonElement"/> boxeado), porque os
/// operadores fazem <c>is string</c>/<c>is decimal</c> diretamente.
/// </summary>
public class RuleNodeSerializationTests
{
    [Fact]
    public void FieldReferenceNode_round_trips()
    {
        RuleNode original = new FieldReferenceNode("Numero");

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<RuleNode>(json);

        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData("texto")]
    [InlineData(true)]
    [InlineData(null)]
    public void LiteralNode_round_trips_preserving_clr_type(object? value)
    {
        RuleNode original = new LiteralNode(value);

        var json = JsonSerializer.Serialize(original);
        var restored = Assert.IsType<LiteralNode>(JsonSerializer.Deserialize<RuleNode>(json));

        Assert.Equal(value, restored.Value);
    }

    [Fact]
    public void LiteralNode_with_decimal_round_trips_as_decimal_not_json_element()
    {
        RuleNode original = new LiteralNode(10.50m);

        var json = JsonSerializer.Serialize(original);
        var restored = Assert.IsType<LiteralNode>(JsonSerializer.Deserialize<RuleNode>(json));

        var restoredValue = Assert.IsType<decimal>(restored.Value);
        Assert.Equal(10.50m, restoredValue);
    }

    [Fact]
    public void OperatorNode_round_trips_with_nested_arguments()
    {
        RuleNode original = OperatorNode.Of(
            "concat",
            new FieldReferenceNode("Prefixo"),
            OperatorNode.Of("trim", new FieldReferenceNode("Numero")),
            new LiteralNode("-fim"));

        var json = JsonSerializer.Serialize(original);
        var restored = Assert.IsType<OperatorNode>(JsonSerializer.Deserialize<RuleNode>(json));

        Assert.Equal("concat", restored.OperatorName);
        Assert.Equal(3, restored.Arguments.Count);
        Assert.IsType<FieldReferenceNode>(restored.Arguments[0]);
        Assert.IsType<OperatorNode>(restored.Arguments[1]);
        Assert.IsType<LiteralNode>(restored.Arguments[2]);
    }

    [Fact]
    public void FieldMapping_with_condition_round_trips_through_MappingTemplate()
    {
        var template = new MappingTemplate
        {
            Fields =
            [
                new FieldMapping(
                    "discriminacaoServico",
                    Value: new FieldReferenceNode("DiscriminacaoServico"),
                    Condition: OperatorNode.Of("notEmpty", new FieldReferenceNode("CnpjTomador"))),
            ],
        };

        var json = JsonSerializer.Serialize(template);
        var restored = JsonSerializer.Deserialize<MappingTemplate>(json)!;

        var field = Assert.Single(restored.Fields);
        Assert.Equal("discriminacaoServico", field.DestinationElementName);
        Assert.IsType<FieldReferenceNode>(field.Value);
        var condition = Assert.IsType<OperatorNode>(field.Condition);
        Assert.Equal("notEmpty", condition.OperatorName);
    }
}
