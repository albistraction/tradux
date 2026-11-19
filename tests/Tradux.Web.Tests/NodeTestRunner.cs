using System.Diagnostics;

namespace Tradux.Web.Tests;

/// <summary>
/// Roda os testes dos módulos JavaScript do editor (<c>*.test.mjs</c>, runner
/// nativo do Node, sem dependências npm) como parte do <c>dotnet test</c>.
/// Exige Node.js 20+ no PATH — ausência é falha explícita, não teste pulado.
/// </summary>
public class NodeTestRunner
{
    [Fact]
    public async Task Editor_javascript_modules_pass_their_node_tests()
    {
        var testsDir = Path.Combine(RepositoryRoot(), "tests", "Tradux.Web.Tests");
        var start = new ProcessStartInfo("node", ["--test", "--test-reporter=spec", "*.test.mjs"])
        {
            WorkingDirectory = testsDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Assert.Fail($"Node.js não encontrado no PATH (necessário para os testes do editor): {ex.Message}");
            return;
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await output;
        var stderr = await error;

        Assert.True(process.ExitCode == 0, $"node --test falhou (código {process.ExitCode}):\n{stdout}\n{stderr}");
        Assert.Contains("pass", stdout);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
