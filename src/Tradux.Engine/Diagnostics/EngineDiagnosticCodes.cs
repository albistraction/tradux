namespace Tradux.Engine.Diagnostics;

/// <summary>
/// Códigos que o motor emite em mais de um lugar ou compara. Os demais ficam
/// junto do único lugar que os emite.
/// </summary>
public static class EngineDiagnosticCodes
{
    public const string RequiredValueMissing = "REQUIRED_VALUE_MISSING";
    public const string CollectionTooFewItems = "COLLECTION_TOO_FEW_ITEMS";
    public const string ChoiceNoBranch = "CHOICE_NO_BRANCH";
    public const string CollectionSourceNotEnumerable = "COLLECTION_SOURCE_NOT_ENUMERABLE";
    public const string MappingMissingRequired = "MAPPING_MISSING_REQUIRED";
    public const string XsdValidationTimeout = "XSD_VALIDATION_TIMEOUT";

    /// <summary>
    /// Falta de dado — não de regra. Num ramo de choice ou elemento complexo opcional
    /// que acaba sem conteúdo, esses diagnósticos são descartados; erro de regra fica.
    /// </summary>
    internal static bool IsMissingData(string code) => code is RequiredValueMissing or CollectionTooFewItems or ChoiceNoBranch;
}
