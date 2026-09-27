# Phase 4B implementation verification

Verified locally on 2026-09-27 with .NET SDK 10.0.400. Scope stops at Phase 4B.

## Build and tests

Commands completed successfully:

```text
dotnet build PortableAgent.sln
  0 warnings, 0 errors

dotnet test PortableAgent.sln
  PortableAgent.Core.Tests:        30 passed
  PortableAgent.IntegrationTests:  40 passed
  PortableAgent.Persistence.Tests: 48 passed
  Total: 118 passed, 0 failed, 0 skipped
```

The previous 70 tests retain their assertions. Changes are constructor identity and Store-signature adjustments; the pause-save failure double now fails the pause replacement, because initial creation happens earlier. Rejection also rediscovers current capabilities before continuing the model, so historical tool definitions cannot authorize later proposals.

New tests cover explicit DTO reconstruction, owned JSON and collections, invalid shapes/versions/enums, independent store instances, restart approve/reject, ordinal identity mismatch, two separate SQLite stores competing for the same version, exact frozen calls, saved budgets and CallIds, sequence continuity and pagination, policy/tool changes, all live events being durable before observation, and unique constraints. Real SQLite triggers inject initial, pause, approval, tool-start/tool-completion and terminal persistence failures. A committed Running claim is explicitly tested as unrecoverable.

## Projects and packages

New production project: `src/PortableAgent.Persistence`, referencing Core only. Its `Sqlite` directory contains the context, design-time factory, store, `Entities`, `Serialization`, and `Migrations`. New tests: `tests/PortableAgent.Persistence.Tests`, referencing Core, Infrastructure and Persistence. Console now references Persistence. Core has no EF/SQLite package references.

EF Core Sqlite and Design: **10.0.12**. Repository-local dotnet-ef: **10.0.12**, pinned in `dotnet-tools.json`.

Generated initial migration: **20260927044802_InitialPersistence**, with its EF designer and model snapshot. Explicit Console `init` calls MigrateAsync; no EnsureCreated flow. Contexts are per operation, pooling is off, foreign keys on, SQLite lock wait is bounded at five seconds, and no application retry strategy is configured.

## SQLite schema

All listed columns are NOT NULL. GUIDs, enum names and DateTimeOffset values are stored as TEXT.

| Table | Columns | Constraints |
| --- | --- | --- |
| Runs | RunId TEXT; RuntimeDefinitionId TEXT COLLATE BINARY; Lifecycle TEXT; Version INTEGER; UpdatedAt TEXT; StateJson TEXT | PK RunId; Version is EF concurrency token |
| ExecutionEvents | EventId TEXT; RunId TEXT; Sequence INTEGER; OccurredAt TEXT; EventType TEXT; PayloadJson TEXT | PK EventId; UNIQUE (RunId, Sequence); FK RunId → Runs.RunId, delete RESTRICT |

EF also owns its migration history and SQLite migration-lock bookkeeping. There is no separate approvals table. Event reads use Sequence, never timestamp ordering.

## Contracts and consistency

RuntimeDefinitionId is a required nonblank constructor string from trusted composition. The demo uses `flight-demo-v1`. It is persisted and compared ordinally; mismatch is Conflict with no mutation. Requests/approval commands do not carry it. It is not an authentication mechanism or automatic composition fingerprint.

State JSON uses `{ "schemaVersion": 1, "state": { ... } }`. Explicit DTOs map all RunState data, including messages, catalog, calls/results, pending/resolved approvals, limits, counters, used IDs and LastSequence. AgentMessage factories validate message shapes; JsonElements are cloned; UsedCallIds uses StringComparer.Ordinal. Unknown versions/enums and malformed snapshots fail. The relational envelope is cross-checked against JSON on read.

Final IRunStateStore operations, each also accepting CancellationToken:

```text
CreateAsync(RunState snapshot, IReadOnlyList<ExecutionEvent> events)
GetAsync(Guid runId)
TryReplaceAsync(Guid runId, long expectedVersion, RunState replacement,
                IReadOnlyList<ExecutionEvent> events)
AppendEventAsync(ExecutionEvent executionEvent)
ReadEventsAfterAsync(Guid runId, long afterSequence, int limit)
```

Event reads require afterSequence >= 0 and limit 1–1000. Create starts at version zero. Replace requires replacement.Version == expectedVersion + 1; in-memory and SQLite implementations atomically commit state plus supplied events.

Initial Running + RunStarted, paused state + ApprovalRequired, resolved approval + ApprovalResolved + RunResumed, and final state + terminal event each form a transaction. Pause allocates LastSequence before saving; failed pause commits neither checkpoint nor approval event, then attempts an atomic Failed terminal transition.

Approval ownership uses a conditional UPDATE on RunId, Version, RuntimeDefinitionId and AwaitingApproval lifecycle. Only one affected row wins. Approval events are inserted inside the same transaction; any insert failure rolls the UPDATE back. Losers return Conflict. No transaction spans model, tool or live sink calls.

Normal events persist before live delivery. A ToolExecutionStarted insert failure prevents execution. A ToolExecutionCompleted insert failure after executor return produces Failed with explicit uncertainty about the business effect and does not retry. SafeExecutionEventSink only handles observer failures; database errors are not swallowed. Failed terminal persistence returns Failed without emitting a fabricated, nonpersisted terminal event.

Cancellation before claim leaves approval pending; after committed claim it never restores Pending. Transaction commit is awaited without caller cancellation after a final cancellation check, avoiding cancellation masking a successfully committed ownership change. Database failure during command acquisition propagates to the caller; it is not mislabeled Accepted or Conflict.

## Actual separate-process approval demo

Each command below was a separate completed `dotnet run` invocation. `--no-build` reused the successfully built binaries, not a live Runtime. The database was retained between processes.

```text
> dotnet run --no-build --project src/PortableAgent.Console -- init
Database path: F:\code\PortableAgent\portable-agent.db
Migrations applied.

> dotnet run --no-build --project src/PortableAgent.Console -- pause
Database path: F:\code\PortableAgent\portable-agent.db
[1] Run started
[2] Discovering tools
[3] Discovered 2 tools
[4] Model turn 1 started
[5] Model turn 1 completed · ToolCalls
[6] Tool proposed · cancel_booking
[7] Evaluating policy
[8] Policy evaluated · RequireApproval
[9] Approval required · ce4dec89-62b1-4360-a260-fd1f22295963
RunId: 1191f1bb-5861-4bea-8287-6600939a4d03
Status: AwaitingApproval
ApprovalId: ce4dec89-62b1-4360-a260-fd1f22295963

> dotnet run --no-build --project src/PortableAgent.Console -- approve 1191f1bb-5861-4bea-8287-6600939a4d03 ce4dec89-62b1-4360-a260-fd1f22295963
Database path: F:\code\PortableAgent\portable-agent.db
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
RunId: 1191f1bb-5861-4bea-8287-6600939a4d03
Status: Completed
Booking NZ123 has been cancelled.
```

## Actual separate-process rejection demo

```text
> dotnet run --no-build --project src/PortableAgent.Console -- pause
Database path: F:\code\PortableAgent\portable-agent.db
[1] Run started
[2] Discovering tools
[3] Discovered 2 tools
[4] Model turn 1 started
[5] Model turn 1 completed · ToolCalls
[6] Tool proposed · cancel_booking
[7] Evaluating policy
[8] Policy evaluated · RequireApproval
[9] Approval required · 8cd8202c-ed48-49e9-b9cc-41958d4a4ae7
RunId: f0344f3d-452e-4dbb-a43e-a0428ec0151c
Status: AwaitingApproval
ApprovalId: 8cd8202c-ed48-49e9-b9cc-41958d4a4ae7

> dotnet run --no-build --project src/PortableAgent.Console -- reject f0344f3d-452e-4dbb-a43e-a0428ec0151c 8cd8202c-ed48-49e9-b9cc-41958d4a4ae7
Database path: F:\code\PortableAgent\portable-agent.db
[10] Approval resolved · Rejected
[11] Run resumed
[12] Discovering tools
[13] Discovered 2 tools
[14] Model turn 2 started
[15] Model turn 2 completed · Completed
[16] Run completed · 2 model turns · 0 tool call(s)
Approval command: Accepted
RunId: f0344f3d-452e-4dbb-a43e-a0428ec0151c
Status: Completed
Understood. I did not cancel the booking.
```

## Remaining limits and pre-MCP considerations

Only a committed AwaitingApproval checkpoint resumes. Persisting the initial Running record stores history; it does not make Running recoverable. Normal events do not continuously checkpoint the conversation, so Running StateJson can lag its event history.

After approval CAS commits, a crash before the executor starts leaves Running and a consumed approval; no recovery scanner or automatic retry exists. After an external side effect succeeds, a crash before persistence leaves an uncertain business outcome. There is no external exactly-once guarantee, compensation, model/tool mid-call recovery or event-sourced reconstruction. Business idempotency belongs in a later design. Failed sequence allocations may leave gaps; bounded history remains ordered.

RunState contains user/business execution data needed for recovery. ExecutionEvent payloads omit raw prompts, arguments, results, model responses and exception text. Storage retention, protection and authenticated access are future concerns.

Before MCP, specify trusted server-to-ToolId binding, model-visible name collisions, Schema compatibility and input validation, credentials and resource authorization, and remote cancellation/side-effect behavior. RuntimeDefinitionId and policy identities need deliberate version ownership; neither automatically detects semantic configuration changes. Snapshot v1 has no upgrade infrastructure. Slow live sinks still delay execution. SQLite is a local store with bounded locking, not a distributed execution coordinator.

No Phase 5 implementation was started.
