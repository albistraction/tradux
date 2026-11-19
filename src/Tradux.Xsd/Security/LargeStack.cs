using System.Runtime.ExceptionServices;

namespace Tradux.Xsd.Security;

/// <summary>
/// Segunda proteção contra estouro de pilha na importação de XSD (que derruba o
/// processo, sem 400 nem 500): roda o trabalho numa thread com pilha de 64 MB.
/// A primeira proteção são os limites verificados antes de compilar
/// (<see cref="XmlDepthGuard"/>, <see cref="ReferenceGraphGuard"/>,
/// <see cref="ContentModelSizeChecker"/>); esta só dá folga para caminhos
/// recursivos do .NET que não conhecemos. A pilha é reservada, não alocada.
/// </summary>
internal static class LargeStack
{
    private const int StackBytes = 64 * 1024 * 1024;

    public static T Run<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, StackBytes)
        {
            IsBackground = true,
            Name = "tradux-xsd-import",
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
