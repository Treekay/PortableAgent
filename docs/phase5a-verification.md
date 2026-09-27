# Phase 5A verification

Phase 5A only: one official MCP adapter, one independent stateless HTTP sample server, one read-only tool. Phase 5B/5C were not started.

## Packages and boundaries

Exact direct packages:

- `PortableAgent.Adapters.Mcp` → `ModelContextProtocol.Core` **2.2.0**.
- `PortableAgent.Sample.McpServer` → `ModelContextProtocol.AspNetCore` **2.2.0**, transitively `ModelContextProtocol` **2.2.0**.

The existing .NET 10 target is retained. No MCP package or source changes were made to `PortableAgent.Core`. `PortableAgent.Persistence` and its schema/migrations were also unchanged. The previous 118 tests were unchanged.

```text
src/
  PortableAgent.Adapters.Mcp/
    McpSourceDefinition.cs
    McpToolAdapter.cs
    McpToolMapper.cs
    McpResultJson.cs
  PortableAgent.Infrastructure/Models/
    ServerStatusScriptedModelProvider.cs
  PortableAgent.Console/
    Program.cs                     adds mcp-status, retains Phase 4B commands
samples/
  PortableAgent.Sample.McpServer/
    Program.cs
    ServerStatusTool.cs
    Properties/launchSettings.json
tests/
  PortableAgent.Adapters.Mcp.Tests/
    AdapterTests.cs
    LifecycleTests.cs
    RuntimeIntegrationTests.cs
    ScriptedModelTests.cs
    TestServerFixture.cs
```

The adapter references Core. The sample server references no PortableAgent runtime project. The test project references the adapter, Infrastructure, Persistence and the sample server so it can register the actual sample tool in a real Kestrel host.

## Configuration and lifecycle

Final configuration:

```csharp
public sealed record McpSourceDefinition(string SourceId, Uri Endpoint);
```

SourceId must be nonblank; Endpoint must be an absolute HTTP/HTTPS URI containing the complete MCP route. The Console composition fixes `sample-mcp`, `http://localhost:5101/mcp`, and `RuntimeDefinitionId = sample-mcp-demo-v1`. These values never come from model output or tool arguments.

McpToolAdapter implements IToolProvider, IToolExecutor and IAsyncDisposable. It validates configuration at construction, initializes the official HTTP transport/client lazily at first discovery, then reuses the client. One SemaphoreSlim coordinates initialization, discovery, execution and disposal. Waiting operations honor caller cancellation. Disposal waits for the current operation, disposes the client, and attempts transport/owned HttpClient cleanup in finally even if client disposal fails. Subsequent operations throw ObjectDisposedException; the adapter does not recreate disposed resources.

The client explicitly selects StreamableHttp, disables standalone GET and stream reconnect attempts, and disables automatic HTTP redirects. The independent sample server uses HttpServerSessionMode.Stateless and MapMcp("/mcp"), listening at localhost:5101. Legacy SSE, auth, sampling, prompts, resources and tasks are not enabled.

## Tool mapping

Every discovery performs SDK ListToolsAsync, including its pagination. Configured SourceId + remote Name form ToolId; remote Name is also ModelName; missing description maps to empty string; InputSchema is cloned. Duplicate ToolId or ModelName fails ordinal checks. Failed refresh cannot silently return or authorize execution from an old catalog.

The cached catalog only checks consistency. Execution uses the trusted definition supplied by Runner and remote name `registeredTool.Id.Name`. It verifies discovery, SourceId, registered identity, ModelName, InputSchema and proposed ToolName. Wrong routing and invalid arguments fail before tools/call reaches the server.

Arguments must be an object and have no duplicate property names, including nested objects. Each value is cloned into the official CallToolRequestParams dictionary. Nested JSON and large numbers remain structured values. Empty arguments send an empty object. Core CallId stays local, is copied to ToolResult, and is not assigned to JSON-RPC request IDs or treated as a remote idempotency key.

StructuredContent preserves the complete JSON value without wrapping: object, array, string, number, boolean and explicit null. No outputSchema validation is added. When absent, ordered text blocks become `{ "content": [{ "type": "text", "text": "..." }] }`; empty content becomes `{ "content": [] }`. Text that looks like JSON remains text. Non-text blocks fail explicitly even when StructuredContent is also present.

### SDK 2.2.0 explicit-null mismatch

A real HTTP test initially exposed that the SDK convenience CallToolAsync deserializes both a missing structuredContent property and a present JSON null into C# null. Falling back to Content in the latter case would violate the approved JSON-kind requirement.

The adapter therefore uses the official McpClient typed `SendRequestAsync<CallToolRequestParams, CallToolResult>` with `RequestMethods.ToolsCall`, SDK default JSON options, and one `JsonConverter<JsonElement?>` that handles a present null as a JsonElement with ValueKind.Null. Missing properties retain the nullable default. This is JSON value conversion, not a protocol implementation: framing, request/response correlation, errors, transport, negotiation and cancellation are still handled by the official SDK. No Core or persistence change was required. Both explicit null and absent StructuredContent are covered over real HTTP.

## Error and event behavior

A mappable IsError=true response produces Executed + IsSuccess=false; Error uses text content or a fixed fallback. The unchanged Runner emits ToolExecutionCompleted(success=false), then RunFailed, without a second model call.

Protocol exceptions, HTTP failures and malformed responses remain exceptions. The Runner returns Failed without a fake ToolResult or ToolExecutionCompleted. Caller cancellation reaches the SDK operation and the stateless server handler, and the Runner returns Cancelled. The adapter does not automatically replay calls or reconnect a failed client. Initialization failure cleans up the partial transport; a later explicit operation can attempt initialization again.

No MCP-specific Core events were added. SDK diagnostics remain separate from persisted Runtime events. RunState contains only Core tool definitions, calls, conversation and existing bookkeeping, never MCP clients/session IDs/connections.

## Build and tests

Commands completed successfully:

```text
dotnet build PortableAgent.sln
  0 warnings, 0 errors

dotnet test PortableAgent.sln
  Core.Tests:          30 passed
  IntegrationTests:   40 passed
  Persistence.Tests:  48 passed
  Adapters.Mcp.Tests: 46 passed
  Total:             164 passed, 0 failed, 0 skipped
```

The 46 new cases cover actual sample discovery/call, refresh and failed refresh, duplicate names, all six structured JSON categories, text/empty fallback, rich-content rejection with and without structured content, nested argument fidelity, invalid and duplicate arguments, trusted routing, IsError, HTTP/protocol/malformed failures, unavailable server, remote cancellation, lazy initialization, client reuse, concurrent discovery, cancellable gate waiting, disposal, script rejection of incorrect results, the unchanged Runtime loop, generic events and SQLite persistence.

Tests use real SDK servers on random loopback Kestrel ports. Test-only handlers and HTTP fault injection are separate from the sample's single production/demo tool. The SQLite test owns a unique temporary file, reads through a new Store after the adapter is disposed, and cleans up its files. Tests do not rely on the manual demo server or database.

## Actual two-process demonstration

The sample server was launched as its separately built executable in a hidden process; Console ran as another process. This is equivalent to the documented Terminal A command `dotnet run --project samples/PortableAgent.Sample.McpServer`. The sample server process was stopped after verification.

Server startup:

```text
Now listening on: http://localhost:5101
Application started. Press Ctrl+C to shut down.
Hosting environment: Production
Content root path: F:\code\PortableAgent\samples\PortableAgent.Sample.McpServer
```

Console command and actual output:

```text
> dotnet run --no-build --project src/PortableAgent.Console -- mcp-status
Database path: F:\code\PortableAgent\portable-agent.db
[1] Run started
[2] Discovering tools
[3] Discovered 1 tools
[4] Model turn 1 started
[5] Model turn 1 completed · ToolCalls
[6] Tool proposed · get_server_status
[7] Evaluating policy
[8] Policy evaluated · Allow
[9] Tool execution started · get_server_status
[10] Tool execution completed · get_server_status · success=True
[11] Model turn 2 started
[12] Model turn 2 completed · Completed
[13] Run completed · 2 model turns · 1 tool call(s)
RunId: 54230f67-cc35-4502-a832-7cc45ed222a0
Status: Completed
The sample service is online.
```

The independent server logged request handlers for server/discover, tools/list and tools/call, including `"get_server_status" completed. IsError = False.` This verifies the tool call crossed HTTP instead of using an in-process executor shortcut.

## Actual SQLite-backed Run

A separate read-only SQLite connection inspected the Console demo's database after the client exited:

```text
RunId:               54230f67-cc35-4502-a832-7cc45ed222a0
RuntimeDefinitionId: sample-mcp-demo-v1
Lifecycle:           Completed
Version:             1
ModelTurns:          2
ToolCalls:           1
LastSequence:        13
ToolId:              sample-mcp / get_server_status
```

All 13 persisted events had the same RunId and contiguous Sequence 1–13. They included ToolCallProposed at 6, ToolExecutionStarted at 9, ToolExecutionCompleted at 10, and RunCompleted at 13. The automated SQLite test also verifies the exact event types and IDs against the live sink and reads the structured tool result from the final snapshot through a fresh SqliteRunStateStore.

## Phase 5B/5C friction, deliberately deferred

- The SDK's explicit-null deserialization difference requires the small typed-request mapping adjustment described above; reassess it on SDK upgrade.
- The Runtime deliberately terminates on executed tool failure. Returning that failure to the model requires an explicit Phase 5C semantic review.
- A request sent before network/protocol failure may already have executed remotely. Neither cancellation nor failure proves an external write did not happen. There is no automatic retry, remote exactly-once guarantee or compensation.
- One client/source and serialized operations simplify lifecycle behavior but limit throughput. A long request delays discovery and disposal; callers should cancel active Runs before disposing the host.
- SourceId and RuntimeDefinitionId are host-maintained identities, not automatic endpoint or semantic configuration fingerprints. Endpoint rebinding, authentication, multiple sources, aliases and richer schema/version decisions need later design.
- Persistence remains compatible with fresh adapter discovery and frozen Core calls; write approval over MCP and Pet/Flight MCP servers are intentionally not implemented in 5A.
