using System.Runtime.CompilerServices;
using Tradux.Xsd.Security;

namespace Tradux.Api.IntegrationTests.Infrastructure;

/// <summary>
/// A API roda dentro deste processo de teste: o timeout global de regex
/// (ADR-0012 §9) precisa ser fixado antes de qualquer <c>Regex</c> existir,
/// como o <c>Program</c> faz em produção.
/// </summary>
internal static class RegexTimeoutInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => RegexTimeout.Configure();
}
