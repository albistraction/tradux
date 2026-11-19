using Tradux.Application;
using Tradux.Application.Layers;
using Tradux.Application.Ports;
using Tradux.Application.Publishing;
using Tradux.Application.Snapshots;

using Tradux.Application.Testing;
namespace Tradux.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Publica direto no MongoDB, com cliente próprio — como outra instância do
/// serviço faria —, pelos mesmos gates de qualquer publicação
/// (<see cref="ContentPublicationService"/>). A API em teste só fica sabendo
/// pelo Change Stream, pelo poll ou ao ler com o cache frio.
/// </summary>
public sealed class DirectPublisher(ITemplateSnapshotRepository snapshots, IAuditSink audit)
{
    private readonly ContentPublicationService _service = new(snapshots, new SnapshotCompiler(), audit);

    /// <summary>Publica e exige que os gates aprovem.</summary>
    public async Task<PublicationResult> PublishAsync(
        string organizationId, string templateId, string operation, SnapshotContent content, IReadOnlyList<FunctionalScenario> scenarios, string actor)
    {
        var result = await TryPublishAsync(organizationId, templateId, operation, content, scenarios, actor);
        Assert.True(result.Success, string.Join(", ", result.Diagnostics.Select(d => d.Code)));
        return result;
    }

    public Task<PublicationResult> TryPublishAsync(
        string organizationId, string templateId, string operation, SnapshotContent content, IReadOnlyList<FunctionalScenario> scenarios, string actor) =>
        _service.PublishAsync(new TemplateKey(organizationId, templateId, operation), content, scenarios, actor);
}
