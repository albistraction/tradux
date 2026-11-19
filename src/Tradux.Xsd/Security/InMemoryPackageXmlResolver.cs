using System.Xml;

namespace Tradux.Xsd.Security;

/// <summary>
/// Resolve <c>xs:include</c>/<c>xs:import</c> inteiramente em memória contra
/// o conjunto de XSDs enviado — já normalizado por <see cref="SafeDoctype"/> —
/// e nunca toca o disco, então
/// path traversal via <c>schemaLocation</c> não tem nenhum alvo real para
/// escapar (a única superfície é o dicionário de entradas em memória).
/// Ainda assim, bloqueia explicitamente tentativas de subir acima da raiz
/// virtual do pacote (seção 11 do requisito de origem).
/// </summary>
internal sealed class InMemoryPackageXmlResolver : XmlResolver
{
    private const string Scheme = "pkg";
    private readonly IReadOnlyDictionary<string, byte[]> _entries;
    private readonly List<string> _loadedEntriesInOrder = [];

    public InMemoryPackageXmlResolver(IReadOnlyDictionary<string, byte[]> entries)
    {
        _entries = entries;
    }

    public IReadOnlyList<string> LoadedEntriesInOrder => _loadedEntriesInOrder;

    public static Uri EntryPointUri(string entryPointPath) => new($"{Scheme}:///{entryPointPath.Replace('\\', '/')}");

    public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
    {
        if (relativeUri is null)
        {
            throw new ArgumentNullException(nameof(relativeUri));
        }

        if (Uri.TryCreate(relativeUri, UriKind.Absolute, out var asAbsolute) && asAbsolute.Scheme != Scheme)
        {
            throw new InvalidOperationException(
                $"Referência de schema para fora do pacote não é permitida: '{relativeUri}' (seção 11 do requisito de origem).");
        }

        var basePath = baseUri is { Scheme: Scheme } ? baseUri.AbsolutePath.TrimStart('/') : string.Empty;
        var combined = CombineRelative(basePath, relativeUri);
        return new Uri($"{Scheme}:///{combined}");
    }

    public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
    {
        var key = Uri.UnescapeDataString(absoluteUri.AbsolutePath.TrimStart('/'));
        if (!_entries.TryGetValue(key, out var bytes))
        {
            throw new InvalidOperationException(
                $"Entrada '{key}' referenciada via include/import não encontrada no pacote (ou fora dos limites permitidos).");
        }

        _loadedEntriesInOrder.Add(key);
        return new MemoryStream(bytes);
    }

    private static string CombineRelative(string basePath, string relativeUri)
    {
        var baseDirectory = basePath.Contains('/') ? basePath[..basePath.LastIndexOf('/')] : string.Empty;
        var segments = new List<string>(baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries));

        foreach (var segment in relativeUri.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Referência include/import tenta escapar da raiz do pacote (path traversal bloqueado — seção 11 do requisito de origem).");
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }
}
