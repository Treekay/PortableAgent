using PortableAgent.Core.Policies;
using PortableAgent.Core.Tools;

namespace PortableAgent.Infrastructure.Policies;

public sealed class InMemoryPolicyEvaluator(IReadOnlyDictionary<ToolId, PolicyDecision> policies) : IPolicyEvaluator
{
    private readonly Dictionary<ToolId, PolicyDecision> _policies = new(policies);

    public ValueTask<PolicyDecision> EvaluateAsync(PolicyEvaluationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_policies.GetValueOrDefault(context.Tool.Id)
            ?? new PolicyDecision(PolicyOutcome.Deny, Reason: "Tool has no configured policy."));
    }
}
