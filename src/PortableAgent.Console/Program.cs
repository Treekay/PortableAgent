using PortableAgent.Core.Execution;
using PortableAgent.Console;
using PortableAgent.Infrastructure.Models;
using PortableAgent.Infrastructure.Tools.PetBoarding;
using PortableAgent.Infrastructure.Tools.FlightBooking;

var eventSink = new ConsoleExecutionEventSink();
var petRunner = new AgentRunner(new PetBoardingScriptedModelProvider(),
    new PetBoardingToolProvider(), new PetBoardingToolExecutor(), new RunLimits(), eventSink);
var flightRunner = new AgentRunner(new FlightBookingScriptedModelProvider(),
    new FlightBookingToolProvider(), new FlightBookingToolExecutor(), new RunLimits(), eventSink);

Console.WriteLine("=== Pet Boarding ===");
var petRequest = new AgentRunRequest("Has Cooper eaten today?");
Console.WriteLine(petRequest.UserMessage);
var petResult = await petRunner.RunAsync(petRequest);
Console.WriteLine(petResult.FinalText ?? $"{petResult.Status}: {petResult.Error}");
Console.WriteLine();
Console.WriteLine("=== Flight Booking ===");
var flightRequest = new AgentRunRequest("Show my booking.");
Console.WriteLine(flightRequest.UserMessage);
var flightResult = await flightRunner.RunAsync(flightRequest);
Console.WriteLine(flightResult.FinalText ?? $"{flightResult.Status}: {flightResult.Error}");
return petResult.Status == RunStatus.Completed && flightResult.Status == RunStatus.Completed ? 0 : 1;
