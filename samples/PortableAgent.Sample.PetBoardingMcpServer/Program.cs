using ModelContextProtocol.AspNetCore;
using PortableAgent.Sample.PetBoardingMcpServer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5102");
var state = new PetBoardingState();
var tools = new PetBoardingTools(state);
builder.Services.AddSingleton(state);
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithListToolsHandler((_, _) => ValueTask.FromResult(tools.List()))
    .WithCallToolHandler((request, _) => ValueTask.FromResult(tools.Call(request.Params!)));
var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
