namespace PortableAgent.Api.Contracts;

public sealed record StartRunRequest(string? AgentId, string? Message);
public sealed record ApprovalRequest(string? Decision);
public sealed record RunAccepted(Guid RunId, string Status, string RunUrl)
{
    public string EventsUrl => $"{RunUrl}/events";
}
public sealed record ApprovalAccepted(Guid RunId, Guid ApprovalId, string Status, string RunUrl)
{
    public string EventsUrl => $"{RunUrl}/events";
}
