namespace Tradux.Xsd;

/// <summary>
/// Regras de partícula compartilhadas por motor, editor e qualidade (ADR-0018).
/// Grupos <c>sequence</c>/<c>choice</c>/<c>all</c> são resolvidos pelo motor;
/// só conteúdo <b>opaco</b> não é: <c>xs:any</c>, conteúdo simples com
/// atributos ou vazio, que a importação representa como grupo sem itens.
/// </summary>
public static class ParticleRules
{
    /// <summary>Nome do modelo de conteúdo <see cref="ParticleKind.Choice"/> na árvore do editor e nos relatórios.</summary>
    public const string ChoiceModel = "choice";

    /// <summary>Verdadeiro quando o conteúdo tem parte opaca: o motor não o gera e o editor não o mapeia.</summary>
    public static bool ContainsOpaque(SchemaParticle particle) =>
        particle.Items.Any(item => item is GroupItem group && (group.Group.Items.Count == 0 || ContainsOpaque(group.Group)));

    /// <summary>O conteúdo de um elemento é mapeável se o pai é e ele não tem parte opaca.</summary>
    public static bool ContentMappable(bool parentMappable, SchemaParticle content) => parentMappable && !ContainsOpaque(content);

    /// <summary>Um grupo (choice/all/sequence aninhada) é mapeável se o pai é e o grupo não é vazio (opaco).</summary>
    public static bool GroupMappable(bool parentMappable, SchemaParticle group) => parentMappable && group.Items.Count > 0;

    /// <summary>Nome do modelo de conteúdo: <c>sequence</c>, <c>choice</c> ou <c>all</c>.</summary>
    public static string ContentModel(ParticleKind kind) => kind == ParticleKind.Choice ? ChoiceModel : kind.ToString().ToLowerInvariant();
}
