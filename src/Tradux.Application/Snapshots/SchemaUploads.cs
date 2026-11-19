using Tradux.Contracts.Design;

namespace Tradux.Application.Snapshots;

/// <summary>XSDs como trafegam na API (Base64) ⇄ como o snapshot e as camadas os guardam (bytes).</summary>
public static class SchemaUploads
{
    /// <exception cref="ArgumentException">Sem arquivo, sem ponto de entrada ou Base64 inválido.</exception>
    internal static Dictionary<string, byte[]> Decode(SchemaUpload upload)
    {
        if (upload.Files is not { Count: > 0 })
        {
            throw new ArgumentException("Envie ao menos um arquivo XSD.", nameof(upload));
        }

        if (string.IsNullOrWhiteSpace(upload.EntryPoint))
        {
            throw new ArgumentException("Informe 'entryPoint' (nome do arquivo XSD principal).", nameof(upload));
        }

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in upload.Files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Name) || file.ContentBase64 is null)
            {
                throw new ArgumentException("Cada arquivo precisa de 'name' e 'contentBase64'.", nameof(upload));
            }

            try
            {
                files[file.Name] = Convert.FromBase64String(file.ContentBase64);
            }
            catch (FormatException)
            {
                throw new ArgumentException($"Conteúdo do arquivo '{file.Name}' não é Base64 válido.", nameof(upload));
            }
        }

        return files;
    }

    public static SchemaUpload Encode(IReadOnlyDictionary<string, byte[]> files, string entryPoint, string? rootElementName) =>
        new(files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new SchemaFileUpload(f.Key, Convert.ToBase64String(f.Value))).ToList(), entryPoint, rootElementName);
}
