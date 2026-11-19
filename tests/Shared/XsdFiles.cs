using System.Text;
using System.Text.RegularExpressions;
using Tradux.Xsd;

namespace Tradux.Tests.Shared;

/// <summary>
/// Lê um XSD de fixture e os arquivos que ele inclui/importa (relativos, dentro
/// do diretório dele) para o dicionário que a importação recebe — o mesmo
/// caminho em memória de upload e snapshot. Referência remota ou para fora do
/// diretório não é seguida: a importação é quem recusa.
/// </summary>
internal static partial class XsdFiles
{
    public static Dictionary<string, byte[]> Load(string entryPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(entryPath))!;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pending = new Queue<string>([Path.GetFileName(entryPath)]);
        while (pending.TryDequeue(out var name))
        {
            if (files.ContainsKey(name))
            {
                continue;
            }

            var bytes = File.ReadAllBytes(Path.Combine(root, name));
            files[name] = bytes;
            var directory = Path.GetDirectoryName(name) ?? "";
            foreach (Match match in SchemaLocation().Matches(Encoding.UTF8.GetString(bytes)))
            {
                var location = match.Groups[1].Value;
                if (Uri.TryCreate(location, UriKind.Absolute, out _))
                {
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(root, directory, location));
                if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    pending.Enqueue(Path.GetRelativePath(root, full).Replace('\\', '/'));
                }
            }
        }

        return files;
    }

    public static ImportedXsd Import(string entryPath, string? rootElementName = null, XsdImportSecurityOptions? options = null) =>
        XsdSchemaImporter.ImportInMemory(Load(entryPath), Path.GetFileName(entryPath), rootElementName, options);

    [GeneratedRegex("""<(?:\w+:)?(?:include|import|redefine)\b[^>]*\bschemaLocation\s*=\s*["']([^"']+)["']""")]
    private static partial Regex SchemaLocation();
}
