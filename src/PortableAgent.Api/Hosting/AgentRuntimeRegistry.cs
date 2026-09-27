using PortableAgent.Api.Contracts;

namespace PortableAgent.Api.Hosting;

public sealed class AgentRuntimeRegistry : IAsyncDisposable
{
    private readonly Dictionary<string, AgentRuntimeRegistration> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentRuntimeRegistration> _definitions = new(StringComparer.Ordinal);

    public AgentRuntimeRegistry(IEnumerable<AgentRuntimeRegistration> registrations)
    {
        foreach (var registration in registrations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.AgentId);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.DisplayName);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.RuntimeDefinitionId);
            if (!_agents.TryAdd(registration.AgentId, registration) || !_definitions.TryAdd(registration.RuntimeDefinitionId, registration))
                throw new ArgumentException("AgentId and RuntimeDefinitionId must be unique using ordinal comparison.");
        }
    }

    public AgentRuntimeRegistration? ByAgent(string id) => _agents.GetValueOrDefault(id);
    public AgentRuntimeRegistration? ByDefinition(string id) => _definitions.GetValueOrDefault(id);
    public AgentDto[] List() => _agents.Values.Select(r => new AgentDto(r.AgentId, r.DisplayName)).ToArray();

    public async ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        foreach (var registration in _agents.Values)
        {
            try { await registration.DisposeAsync(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
