using ModelContextProtocol.AspNetCore;
using PortableAgent.Sample.McpServer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5101");
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<ServerStatusTool>();
var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
