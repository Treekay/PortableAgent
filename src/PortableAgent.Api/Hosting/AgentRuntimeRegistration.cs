using PortableAgent.Core.Execution;

namespace PortableAgent.Api.Hosting;

public sealed class AgentRuntimeRegistration(string agentId, string displayName, string runtimeDefinitionId,
    AgentRunner runner, params IAsyncDisposable[] resources) : IAsyncDisposable
{
    public string AgentId { get; } = agentId;
    public string DisplayName { get; } = displayName;
    public string RuntimeDefinitionId { get; } = runtimeDefinitionId;
    public AgentRunner Runner { get; } = runner;
    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        List<Exception> errors = [];
        foreach (var resource in resources.Reverse())
        {
            try { await resource.DisposeAsync(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
