using ModelContextProtocol.AspNetCore;
using PortableAgent.Sample.FlightBookingMcpServer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5103");
var state = new FlightBookingState();
var tools = new FlightBookingTools(state);
builder.Services.AddSingleton(state);
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithListToolsHandler((_, _) => ValueTask.FromResult(tools.List()))
    .WithCallToolHandler((request, _) => ValueTask.FromResult(tools.Call(request.Params!)));
var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
