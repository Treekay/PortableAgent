namespace PortableAgent.Core.Policies;

public interface IPolicyEvaluator
{
    ValueTask<PolicyDecision> EvaluateAsync(PolicyEvaluationContext context, CancellationToken cancellationToken);
}
