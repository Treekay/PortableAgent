# Phase 5B verification — domain MCP portability and remote approval

Verified on 2026-09-27 using .NET SDK 10.0.400 and official MCP C# SDK 2.2.0.

## Scope and unchanged boundaries

Added two independent ASP.NET Core sample projects:

- `samples/PortableAgent.Sample.PetBoardingMcpServer`: Program, Tools, State, project file.
- `samples/PortableAgent.Sample.FlightBookingMcpServer`: Program, Tools, State, project file.

Both reference only the official `ModelContextProtocol.AspNetCore` package, with no references to PortableAgent runtime projects. The official SDK handles Streamable HTTP and MCP requests. Each server explicitly publishes a static tool catalog and invokes its own business handlers. Explicit schemas keep the published contract stable without depending on generated method schemas.

Also added `src/PortableAgent.Console/McpDomainDemo.cs`, three test files (`DomainServerFixture`, `DomainPortabilityTests`, `RemoteApprovalTests`), solution/test-project references, and README documentation. The original status sample and old Console commands remain available.

Compared with the Phase 5A baseline `753739aaa9562124d3e5caab63bd110e379c6977`:

| Component | Changed production files |
| --- | ---: |
| PortableAgent.Core | 0 |
| PortableAgent.Persistence | 0 |
| PortableAgent.Adapters.Mcp | 0 |
| PetBoardingScriptedModelProvider | 0 |
| FlightBookingScriptedModelProvider | 0 |

Both domain compositions use the same `AgentRunner` and concrete `McpToolAdapter`, with the adapter passed as both provider and executor. Local executors are not involved in the MCP paths. No multi-source composition was introduced.

## Business ownership and contracts

Transport remains `HttpServerSessionMode.Stateless`. Business state lives independently in a server-owned singleton for the lifetime of its process.

Pet owns Cooper's fixed feeding record and an initially empty task collection. Creating a task appends a real record with sequential IDs (`task-1`, `task-2`, ...). Unknown pets and empty tasks produce MCP tool errors without mutation.

Flight owns NZ123: Auckland → Sydney, 2026-10-10, initially confirmed. Cancellation changes it to cancelled. Repeated cancellation returns cancelled without another state change. `CancelCallCount` counts entry into the cancellation business method, while `CancelMutationCount` counts confirmed → cancelled transitions. State access and mutations are synchronized within each service.

The samples have no database, approval state, Runtime identifiers or policy dependencies. An SDK protocol request ID is not a Runtime RunId or an idempotency key.

| Tool | Required string properties | Successful structured result |
| --- | --- | --- |
| get_care_records | petName | petName, records[{type, time, status}] |
| create_staff_task | petName, task | taskId, petName, task, status |
| get_booking | bookingId | bookingId, route, date, current status |
| cancel_booking | bookingId | bookingId, status |

All input schemas use `type: object`, the listed required string properties, and `additionalProperties: false`. Tests verify the exact schemas over real HTTP discovery; business handlers also check their argument sets. Success results contain structured JSON and an equivalent text representation. Invalid business inputs return `IsError=true`. The unchanged adapter maps this to `Executed / IsSuccess=false`; Runtime tool-error recovery remains out of scope.

## Trusted host configuration

| Composition | SourceId | RuntimeDefinitionId | Endpoint |
| --- | --- | --- | --- |
| Pet | pet-mcp | pet-mcp-demo-v1 | http://localhost:5102/mcp |
| Flight | flight-mcp | flight-mcp-demo-v1 | http://localhost:5103/mcp |
| Original status demo | sample-mcp | sample-mcp-demo-v1 | http://localhost:5101/mcp |

Pet `get_care_records` is Allow; `create_staff_task` is Deny. Flight `get_booking` is Allow; `cancel_booking` is RequireApproval with PolicyId `confirm-flight-cancellation-v1`.

Identities are explicit host configuration, not URL-derived. A material change of trusted endpoint/business binding requires the host to choose a new RuntimeDefinitionId.

## Actual cross-process demonstration

The built Flight server DLL ran as process **33036**, listening on port 5103. Pet ran as process **15204** on port 5102. The same Flight process stayed alive throughout read, reject, pause, approve and final inspection. Each Console invocation was a separate `dotnet PortableAgent.Console.dll ...` process that exited naturally. These are the built equivalents of the README `dotnet run --project ... -- ...` commands.

All Console commands used the same isolated working directory and printed this database path:

```text
F:\code\PortableAgent\TestResults\phase5b\portable-agent.db
```

The database, process logs and temporary inspection program are ignored validation artifacts. The servers were stopped only after the complete demonstration and inspection.

Read demonstrations completed before cancellation:

- Pet read Run `4d401aba-6d3b-4ce6-91da-8dfa9a20b94c`: “Yes. Cooper was fed at 08:00.”
- Pet denied write Run `64c1cc7f-9660-4fe8-9cd2-e33407e1b385`: Completed, 0 executed tools, no execution events.
- Flight read Run `606bf61c-35ee-4b77-9263-499aec2af1ef`: confirmed booking returned.

### Reject first, while the booking is confirmed

```text
RunId: 0a42227e-0198-4336-ab91-a5bd249ef455
ApprovalId: 9de67463-b4c5-4d5f-b9af-79bf2dbd5185
Process A: mcp-flight-pause → AwaitingApproval, then exit
Process B: mcp-flight-reject <RunId> <ApprovalId>

[10] Approval resolved · Rejected
[11] Run resumed
[12] Discovering tools
[13] Discovered 2 tools
[14] Model turn 2 started
[15] Model turn 2 completed · Completed
[16] Run completed · 2 model turns · 0 tool call(s)
Approval command: Accepted
Status: Completed
Understood. I did not cancel the booking.
```

A further read in another process, Run `a141fb77-b25b-4484-90c9-c2bdaebd5965`, still returned confirmed. Rejection allows discovery traffic; it does not invoke the remote cancellation operation.

### Fresh pause and fresh approval process

```text
RunId: 6a23d93e-f2f1-48cb-b8ce-7f6e76da01e0
ApprovalId: 97c0577d-f96f-42ed-b2fc-af4238d3b0bb
Process A: mcp-flight-pause
[9] Approval required · 97c0577d-f96f-42ed-b2fc-af4238d3b0bb
Status: AwaitingApproval
Process A exits.

Process B: mcp-flight-approve <RunId> <ApprovalId>
[10] Approval resolved · Approved
[11] Run resumed
[12] Discovering tools
[13] Discovered 2 tools
[14] Evaluating policy
[15] Policy evaluated · RequireApproval
[16] Tool execution started · cancel_booking
[17] Tool execution completed · cancel_booking · success=True
[18] Model turn 2 started
[19] Model turn 2 completed · Completed
[20] Run completed · 2 model turns · 1 tool call(s)
Approval command: Accepted
Status: Completed
Booking NZ123 has been cancelled.
```

The official server log had **2** `tools/call` handler entries before approval (the two explicit read commands), and **3** after approval. The pause and reject commands added discovery requests without tool calls. Approval added exactly one tool call. A subsequent temporary read-only inspector, using the unchanged adapter and official SDK without the scripted model, returned:

```json
{"bookingId":"NZ123","route":"Auckland → Sydney","date":"2026-10-10","status":"cancelled"}
```

That inspection is an additional read, outside the 2 → 3 count above. No server restart occurred between pause and approval. The existing Flight scripted read model remains unchanged and intentionally only accepts confirmed results.

## SQLite history inspection

A separate read-only SQLite connection inspected persisted snapshots and events after the Console processes exited:

| Run | Lifecycle | Sequence | RunStarted count | Model turns | Executed tool calls |
| --- | --- | --- | ---: | ---: | ---: |
| Reject `0a42227e…` | Completed | 1–16, continuous | 1 | 2 | 0 |
| Approve `6a23d93e…` | Completed | 1–20, continuous | 1 | 2 | 1 |

Both retained `flight-mcp-demo-v1`; `LastSequence` matched the last event. The approved snapshot's resolved approval and conversation both retained `call-1`, `cancel_booking`, and `{"bookingId":"NZ123"}`. The rejected result had disposition `RejectedByUser`; the approved result had disposition `Executed` and status cancelled.

## Independent network and business counters

Real Kestrel tests use random loopback ports. A test-only observer records incoming SDK JSON-RPC requests before forwarding them to the official handler. It does not implement MCP. The fixture independently inspects the real sample business state.

| Scenario | HTTP cancel_booking requests | CancelCallCount | CancelMutationCount | Booking |
| --- | ---: | ---: | ---: | --- |
| AwaitingApproval | 0 | 0 | 0 | confirmed |
| Fresh Runtime reject | 0 | 0 | 0 | confirmed |
| Fresh Runtime approve | 1 | 1 | 1 | cancelled |
| Duplicate approval submission | remains 1 | remains 1 | remains 1 | cancelled |
| Schema changed / tool removed | 0 | 0 | 0 | confirmed |
| Current policy Deny | 0 | 0 | 0 | confirmed |
| New RequireApproval PolicyId | 0 | 0 | 0 | confirmed, new approval pending |
| Two direct business cancellations | 2 | 2 | 1 | cancelled |

These three exact counters are measured in the in-process Kestrel integration fixtures; the separate manual process demo uses SDK server request logs, persisted events and direct MCP result inspection. No Runtime-specific diagnostic API was added to either sample server.

Pet denial independently asserts zero `create_staff_task` requests, an empty task collection, `DeniedByPolicy` in the saved conversation, and no tool-execution events. Direct business tests prove the same server really creates tasks when called.

## Test results

Commands executed successfully:

```text
dotnet build PortableAgent.sln
dotnet test PortableAgent.sln
```

Build: **0 warnings, 0 errors**. Tests: **184 passed, 0 failed, 0 skipped**.

| Project | Tests |
| --- | ---: |
| Core.Tests | 30 |
| IntegrationTests | 40 |
| Persistence.Tests | 48 |
| Adapters.Mcp.Tests | 66 |

All 164 previous tests remain unchanged and pass. Added 20 cases: both domain reads (2), Pet denial (1), exact schemas (2), real task creation (1), repeated cancellation and truthful reads (1), business validation errors (6), pause (1), fresh approval/frozen arguments/events/duplicate submission (1), rejection (1), changed schema/missing tool (2), changed policy Deny (1), changed PolicyId and subsequent approval (1).

The approval test verifies a new discovery request, structural equality of remote arguments with the stored pending approval, absence of Runtime approval metadata in the MCP request, and remote completion before the resumed model's first turn. Test-only catalog mutation supports compatibility tests; production sample catalogs remain static.

## Remaining boundaries and Phase 5C observations

- Business idempotency is not a crash-safe Runtime exactly-once guarantee.
- Only persisted AwaitingApproval checkpoints are resumable. A crash after approval CAS leaves Running history; there is no automatic Running recovery.
- A sent request followed by response loss does not establish that the business operation failed. No retry, compensation or distributed transaction was added.
- Executed MCP tool errors still fail the Run, without a model recovery turn. Richer failure semantics remain Phase 5C.
- Reject currently rediscovering tools is retained; rejection therefore still depends on successful discovery, even though it performs no cancellation request.
- Business state resets on server restart; the demonstrated restart is exclusively the Runtime process restart.
- Scripted models retain fixed demo assumptions. In particular, querying a cancelled booking through the existing Flight read script fails its business-result check; the MCP server itself returns the truthful cancelled state.
- No adapter abstraction mismatch was found. Multi-source aggregation, aliases, authentication, higher adapter concurrency, reconnect/timeout policy, rich content and other Phase 5C work were not implemented.
