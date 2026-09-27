namespace PortableAgent.Api.Endpoints;

public sealed class ApiProblem(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public IResult Result() => Results.Problem(statusCode: Status, title: Code, detail: Message,
        extensions: new Dictionary<string, object?> { ["code"] = Code });
}
