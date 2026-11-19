using Tradux.Application;
using Tradux.Application.Layers;
using Tradux.Domain.Layers;

using Tradux.Application.Ports;
namespace Tradux.Application.Tests;

/// <summary>Repositórios em memória da Fase 7, com a mesma semântica dos adaptadores MongoDB.</summary>
internal sealed class InMemoryLayerRepository : ITemplateLayerRepository
{
    private readonly List<TemplateLayer> _layers = [];

    public Task<TemplateLayer?> GetAsync(LayerRef reference, CancellationToken cancellationToken = default)
    {
        lock (_layers)
        {
            return Task.FromResult(_layers.SingleOrDefault(l => l.Ref == reference));
        }
    }

    public Task<TemplateLayer?> GetLatestAsync(string organizationId, string layerId, CancellationToken cancellationToken = default)
    {
        lock (_layers)
        {
            return Task.FromResult(_layers
                .Where(l => l.Ref.OrganizationId == organizationId && l.Ref.LayerId == layerId)
                .MaxBy(l => l.Ref.Version));
        }
    }

    public Task<TemplateLayer> AddVersionAsync(TemplateLayer layerWithoutVersion, CancellationToken cancellationToken = default)
    {
        lock (_layers)
        {
            var next = _layers.Where(l => l.Ref.OrganizationId == layerWithoutVersion.Ref.OrganizationId && l.Ref.LayerId == layerWithoutVersion.Ref.LayerId)
                .Select(l => l.Ref.Version).DefaultIfEmpty(0).Max() + 1;
            var layer = layerWithoutVersion with { Ref = layerWithoutVersion.Ref with { Version = next } };
            _layers.Add(layer);
            return Task.FromResult(layer);
        }
    }

    public Task<TemplateLayer> ImportVersionAsync(TemplateLayer layer, CancellationToken cancellationToken = default)
    {
        lock (_layers)
        {
            if (_layers.SingleOrDefault(l => l.Ref == layer.Ref) is { } existing)
            {
                return Task.FromResult(existing);
            }

            _layers.Add(layer);
            return Task.FromResult(layer);
        }
    }
}

internal sealed class InMemoryBindingRepository : ITemplateBindingRepository
{
    private readonly Dictionary<TemplateKey, TemplateBinding> _bindings = [];

    public Task<TemplateBinding?> GetAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        lock (_bindings)
        {
            return Task.FromResult(_bindings.GetValueOrDefault(key));
        }
    }

    public Task UpsertAsync(TemplateBinding binding, CancellationToken cancellationToken = default)
    {
        lock (_bindings)
        {
            _bindings[binding.Key] = binding;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TemplateBinding>> FindByLineageAsync(LayerIdentity layer, CancellationToken cancellationToken = default)
    {
        lock (_bindings)
        {
            IReadOnlyList<TemplateBinding> found = _bindings.Values.Where(b => b.Lineage.Contains(layer)).ToList();
            return Task.FromResult(found);
        }
    }
}

internal sealed class InMemoryUpdateRepository : ITemplateUpdateRepository
{
    private readonly List<TemplateUpdate> _updates = [];

    public Task AddAsync(TemplateUpdate update, CancellationToken cancellationToken = default)
    {
        lock (_updates)
        {
            _updates.Add(update);
        }

        return Task.CompletedTask;
    }

    public Task<TemplateUpdate?> GetAsync(TemplateKey key, string updateId, CancellationToken cancellationToken = default)
    {
        lock (_updates)
        {
            return Task.FromResult(_updates.SingleOrDefault(u => u.Key == key && u.Id == updateId));
        }
    }

    public Task<bool> TransitionAsync(TemplateUpdate update, UpdateStatus expectedStatus, CancellationToken cancellationToken = default)
    {
        lock (_updates)
        {
            var index = _updates.FindIndex(u => u.Id == update.Id && u.Key == update.Key);
            if (index < 0 || _updates[index].Status != expectedStatus)
            {
                return Task.FromResult(false);
            }

            _updates[index] = update;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<TemplateUpdate>> ListAsync(TemplateKey key, CancellationToken cancellationToken = default)
    {
        lock (_updates)
        {
            IReadOnlyList<TemplateUpdate> list = _updates.Where(u => u.Key == key).OrderByDescending(u => u.CreatedAtUtc).ToList();
            return Task.FromResult(list);
        }
    }
}

internal sealed class InMemoryAuditSink : IAuditSink
{
    public List<AuditEvent> Events { get; } = [];

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        lock (Events)
        {
            Events.Add(auditEvent);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> ListAsync(string organizationId, int limit = 100, CancellationToken cancellationToken = default)
    {
        lock (Events)
        {
            IReadOnlyList<AuditEvent> list = Events.Where(e => e.OrganizationId == organizationId).Reverse().Take(limit).ToList();
            return Task.FromResult(list);
        }
    }
}
