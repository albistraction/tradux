using Tradux.Application.Layers;
using Tradux.Domain.Layers;
using Tradux.Tests.Shared;

namespace Tradux.Application.Tests;

/// <summary>Decisão sobre uma proposta de atualização: vale uma vez e o registro bate com o publicado.</summary>
public class UpdateApprovalTests
{
    private static readonly TemplateKey Key = new(SnapshotFixtures.Organization, "dps", SnapshotFixtures.Operation);

    private static async Task<(TestServices Services, TemplateUpdate Proposal)> ProposalAsync()
    {
        var services = new TestServices();
        var admin = services.Layered;
        var baseRef = (await admin.CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer(), "f")).Layer.Ref;
        var client = (await admin.CreateLayerVersionAsync(new NewLayerVersion
        {
            OrganizationId = SnapshotFixtures.Organization, LayerId = "cliente", Kind = LayerKind.Client, Parent = baseRef,
        }, "c")).Layer.Ref;
        Assert.True((await admin.BindAndPublishAsync(Key, client, UpdatePolicy.Manual, SnapshotFixtures.DpsScenarios(), "c")).Success);

        await admin.CreateLayerVersionAsync(LayeredTemplateServiceTests.BaseLayer() with
        {
            Overrides = LayerOverrides.FromMappingTemplate(SnapshotFixtures.SimpleDps("N-").Mapping, "DPS"),
        }, "f");
        var proposal = (await admin.ListUpdatesAsync(Key)).Single(u => u.Status == UpdateStatus.Proposed);
        return (services, proposal);
    }

    [Fact]
    public async Task A_decided_proposal_cannot_be_decided_again_and_the_record_matches_what_was_published()
    {
        var (services, proposal) = await ProposalAsync();
        var admin = services.Layered;

        var applied = await admin.ApproveUpdateAsync(Key, proposal.Id, "a");

        Assert.Equal(UpdateStatus.Applied, applied.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.RejectUpdateAsync(Key, proposal.Id, "b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => admin.ApproveUpdateAsync(Key, proposal.Id, "b"));
        var stored = (await admin.ListUpdatesAsync(Key)).Single(u => u.Id == proposal.Id);
        Assert.Equal((UpdateStatus.Applied, applied.AppliedSnapshotVersion), (stored.Status, stored.AppliedSnapshotVersion));
        Assert.Equal(applied.AppliedSnapshotVersion, await services.Repository.GetActiveVersionAsync(Key));
    }

    [Fact]
    public async Task Rollback_after_an_approved_update_reactivates_the_previous_version()
    {
        var (services, proposal) = await ProposalAsync();
        await services.Layered.ApproveUpdateAsync(Key, proposal.Id, "a");
        Assert.Equal(2, await services.Repository.GetActiveVersionAsync(Key));

        var rollback = await services.Layered.RollbackAsync(Key, null, "a");

        Assert.True(rollback.Success);
        Assert.Equal(1, await services.Repository.GetActiveVersionAsync(Key));
    }
}
