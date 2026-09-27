# Phase 5C verification — tool failure recovery and remote outcomes

Verified on 2026-09-27 against Phase 5B baseline `8f991797b54eb8585f6c4d2f027dd70f4d22e786`.

## Production scope

Exactly two production files changed:

- `src/PortableAgent.Core/Execution/AgentRunner.cs`
- `src/PortableAgent.Core/Tools/ToolContracts.cs`

Persistence production files changed: **0**. MCP adapter production files changed: **0**. Console, sample servers and all production scripted models remain unchanged. Test-only model scripts and an optional test-server call handler exercise failure scenarios.

`ToolResult` keeps its existing fields: CallId, IsSuccess, Output, Error, Disposition. No public result hierarchy, retry classification, outcome-unknown type or MCP-specific Core type was added.

Final disposition values, retaining previous numeric values:

```text
Executed
DeniedByPolicy
RejectedByUser
NotExecutedDueToBatchPolicy
NotExecutedDueToPriorFailure
```

## Accepted execution behavior

An executor normally returning `Executed / IsSuccess=false` is now a valid correlated tool response. Runner validates CallId and Disposition before publishing `ToolExecutionCompleted(success=false)`, then appends the complete ToolResult through `AgentMessage.FromToolResult`. Output and Error remain available to the model. Runtime does not interpret error text.

The next model turn may explain the failure, explicitly propose another operation, or finish. A Run that handles the failure and reaches a final response is Completed even though the business operation failed. A model that itself throws still causes Run Failed; production scripts were not expanded to handle new scenarios.

The private execution helper returns `(Terminal, BusinessFailed)`, distinguishing Runtime termination from business failure without changing public interfaces. Approved execution uses the same helper and continues the model loop after either a valid success or a valid failure.

No automatic tool retry occurs. A subsequent invocation requires an explicit new model ToolCall and a new CallId. Trusted lookup, budgets, policy and approval are applied again.

## Batch semantics and budgets

An all-Allow A/B/C batch with A succeeding and B returning a business failure produces:

| Call | Result | Execution count |
| --- | --- | ---: |
| A | Executed / true | 1 |
| B | Executed / false | 1 |
| C | NotExecutedDueToPriorFailure / false | 0 |

C retains its original CallId, has null Output and a short generic Error. Results are appended in A/B/C order before the next model turn. C has no execution events. Earlier successful effects are not rolled back.

All proposed CallIds, including skipped C, stay reserved. A new proposal using C fails uniqueness validation. A proposal using C-new can consume the remaining execution budget after fresh policy evaluation.

Whole-batch trusted-tool, CallId, budget and policy prechecks remain in place. Any Deny still prevents the entire batch from executing. Multi-call RequireApproval still fails before execution. No new batch approval mechanism exists.

Every failed executor invocation consumes a tool call. Each model turn consumes model budget; separate tests reach LimitReached using MaxModelTurns and MaxToolCalls. The invocation counter now increments after successfully emitting ToolExecutionStarted, immediately before calling the executor: a start-event persistence failure leaves the counter at zero, matching the fact that execution was never invoked. Existing transaction-failure tests now assert this count explicitly.

## Events and exceptional failures

Normal business error:

```text
ToolExecutionStarted
ToolExecutionCompleted(success=false)
[append failure and any skipped remainder]
ModelTurnStarted
...
RunCompleted, another approval pause, or the subsequent actual outcome
```

Invalid CallId, non-Executed result disposition, or executor/protocol exception:

```text
ToolExecutionStarted
RunFailed
```

There is no ToolExecutionCompleted for a rejected result contract or a thrown executor exception, and no fabricated ToolResult or next model turn. Caller cancellation keeps the existing Cancelled behavior. No new event type was introduced.

Completed now means that the executor returned a result accepted by Runtime's correlation/disposition checks; it does not mean that the business operation succeeded. Existing event persistence and observer isolation behavior remain in place.

## Real MCP verification results

The main tests use real Kestrel on random loopback ports, official MCP server/client, the unchanged adapter, independent SQLite files and test-only deterministic models. The HTTP observer records actual requests before forwarding to the official MCP handler.

| Scenario | Observed result |
| --- | --- |
| get_booking(UNKNOWN) | One HTTP tools/call, normal MCP tool error, same CallId reaches model, final explanation, Completed |
| Model corrects WRONG to NZ123 | Two HTTP calls, three model turns, two unique CallIds, two policy evaluations, Completed |
| Three-call batch A/B/C | A/B requests only; C skipped, two tool calls consumed, all three results persisted in order |
| Approved write returns business error | One HTTP write request, consumed original approval, failure reaches model, Completed when explained |
| Model proposes another write after approved failure | New CallId, same PolicyId but new ApprovalId, paused with only the first request sent |
| Old approval submitted again | Conflict, no extra remote request |
| Second approval in another fresh Runtime | Second frozen operation executes separately; total two tool calls, three model turns, Completed |
| HTTP / malformed response / SDK protocol failure | Failed, no completed execution event, no saved tool response, no extra model turn or replay |

The missing-booking model's actual final answer is `I could not find that booking.` The correction model's final answer is `Booking NZ123 is confirmed.` Failed results preserve Error `Unknown booking.` and their mapped Output in SQLite.

Approved failure tests inject a normal MCP result with `IsError=true`, text `Cancellation temporarily unavailable.`, and structured code `cancellation_unavailable`. The first approval stays Approved. When a second proposal is made, its pending snapshot includes the first failed result and a new frozen write-2 call. After separately approving that new operation, the existing sample business handler performs the cancellation.

## Remote side-effect uncertainty proof

`Remote_mutation_then_protocol_failure_fails_run_without_replay_or_fake_tool_result` runs this real HTTP sequence:

```text
pause cancellation for approval
→ dispose first adapter
→ fresh Runtime and adapter approve the frozen call
→ server receives cancel_booking(NZ123)
→ actual FlightBookingState.CancelBooking changes confirmed to cancelled
→ test handler throws an MCP protocol exception instead of returning CallToolResult
→ official SDK returns a protocol error
→ unchanged adapter throws
→ Runtime records RunFailed
```

Verified facts:

```text
HTTP cancel_booking requests = 1
FlightBookingState.CancelCallCount = 1
FlightBookingState.CancelMutationCount = 1
booking status = cancelled
Run lifecycle = Failed
resumed model turns = 0
ToolExecutionCompleted events = 0
persisted Tool messages = 0
```

This is a protocol-failure-after-side-effect test, not a simulated business error and not a test of every possible network-loss mode. It proves that Run Failed does not establish that the remote operation did not happen. No second request, replay or compensation occurs. Caller cancellation likewise does not establish rollback.

## Persistence compatibility

No migration, table change, DTO change or schemaVersion bump. Existing serialization already stores the full ToolResult and enum names. Added round-trip coverage for a completed A/B/C conversation containing success, executed failure with structured Output/Error, and NotExecutedDueToPriorFailure. Real MCP batch and approval tests also reload these results from SQLite.

Current Phase 5C code reads older schemaVersion 1 snapshots; older binaries are not guaranteed to read newer v1 snapshots containing newly introduced enum values. The project does not currently promise downgrade compatibility. A future structural snapshot change may introduce a new schemaVersion.

Snapshot timing remains unchanged. Events are saved as before; state is replaced at existing lifecycle/checkpoint boundaries. Tool failures do not create new recovery checkpoints. Running crash recovery remains unsupported.

## Tests and deliberate baseline changes

Full commands executed:

```text
dotnet build PortableAgent.sln
dotnet test PortableAgent.sln
```

Build: **0 warnings, 0 errors**. Tests: **200 passed, 0 failed, 0 skipped**.

| Test project | Previous | Added | Current |
| --- | ---: | ---: | ---: |
| Core.Tests | 30 | 9 | 39 |
| IntegrationTests | 40 | 0 | 40 |
| Persistence.Tests | 48 | 1 | 49 |
| Adapters.Mcp.Tests | 66 | 6 | 72 |
| Total | 184 | 16 | 200 |

Three old normal-error tests intentionally changed their model scripts and expected semantics:

- `AgentRunnerTests.Tool_failure_stops_without_another_model_call` → `Tool_failure_reaches_model_as_correlated_result_without_automatic_retry`.
- `ExecutionEventTests.Every_exit_attempts_exactly_one_matching_terminal_event`: normal failure now allows the model to explain and completes. Correlation corruption and executor exception still fail, now explicitly without ToolExecutionCompleted.
- `RuntimeIntegrationTests.Mcp_tool_error_terminates_runtime_without_model_retry` → `Mcp_tool_error_reaches_model_without_runtime_generated_retry`.

The three existing HTTP/protocol/malformed-response tests additionally verify that SQLite has no fabricated Tool message. Transaction failure tests verify execution counts match actual invocations. Safety boundaries for unknown tools, reused CallIds, budgets, policy denial, approval conflict, persistence failure and cancellation retain their intent and pass.

Added cases: local batch result order and skipped-ID reuse (2), failure budgets (2), invalid result/exception event boundaries (3), policy denial of a new attempt (1), skipped operation retried with a new ID and remaining budget (1), snapshot round-trip (1), real MCP business failure/explanation and correction (2), real batch stop/persistence (1), approved-write failure with/without a new attempt (2), remote mutation followed by protocol failure (1).

## Remaining scope

No blocker was found in this phase's acceptance checks before Phase 6 design. API/SSE/Studio have not been started. MCP multi-source aggregation, aliases, authentication/OAuth, rich content, generic timeout/reconnect frameworks, higher adapter concurrency, exactly-once writes, compensation, distributed transactions and real LLM providers remain deferred. They are not prerequisites for the Runtime/API/Studio MVP.
