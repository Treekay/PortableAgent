using PortableAgent.Core.Execution;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Tools;

var runner = new AgentRunner(
    new ScriptedModelProvider(), new LocalToolProvider(), new LocalToolExecutor(), new RunLimits());
var result = await runner.RunAsync(new AgentRunRequest("Calculate 2 + 3."));
Console.WriteLine($"Status: {result.Status}");
Console.WriteLine(result.FinalText ?? result.Error);
return result.Status == RunStatus.Completed ? 0 : 1;
