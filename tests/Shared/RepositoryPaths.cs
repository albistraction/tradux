namespace Tradux.Tests.Shared;

/// <summary>
/// Raiz do repositório (onde está o <c>global.json</c>), achada a partir do
/// diretório de saída dos testes — para as fixtures em <c>docs/schemas/</c>.
/// Arquivo linkado nos projetos de teste que precisam dele.
/// </summary>
internal static class RepositoryPaths
{
    public static string Root { get; } = FindRepositoryRoot();

    public static string Combine(params string[] relative) => Path.Combine([Root, .. relative]);

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada (global.json ausente em nenhum ancestral).");
    }
}
