using System.Security.Cryptography;
using System.Text;

namespace Tradux.Application.Snapshots;

/// <summary>Identidade de um conjunto de XSDs: nome + SHA-256 de cada arquivo, ponto de entrada e raiz.</summary>
internal static class SchemaFilesHash
{
    public static string Canonical(IReadOnlyDictionary<string, byte[]> files, string entryPoint, string? rootElementName) =>
        string.Join('|', files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}:{Convert.ToHexStringLower(SHA256.HashData(f.Value))}"))
        + $"|{entryPoint}|{rootElementName}";

    public static string Of(IReadOnlyDictionary<string, byte[]> files, string entryPoint, string? rootElementName, string suffix = "") =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(files, entryPoint, rootElementName) + suffix)));
}
