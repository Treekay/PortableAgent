# Phase 6B 验证记录

日期：2026-09-27（Pacific/Auckland）。冻结基线：Phase 6A `b938bd5344c7c2e709a783098ec5d2569cc06ece`，234 项测试通过。

本阶段仅实现持久化回放与实时 SSE，不包含 Phase 7 Developer Studio。

## 生产变更边界

新增文件均在 `src/PortableAgent.Api`：

| 文件 | 职责 |
| --- | --- |
| Streaming/RunEventHub.cs | 单例、按 RunId 广播非持久化通知、移除/关闭订阅 |
| Streaming/RunEventSubscription.cs | 独立有界 Channel、溢出状态及订阅取消源 |
| Streaming/RunEventStream.cs | 有界 SQLite 分页、补读/心跳、SSE 写入和退出清理；测试可注入短轮询间隔 |
| Streaming/SseEventDto.cs | 既有 ExecutionEvent 的公开 JSON 映射 |
| Endpoints/RunEventEndpoints.cs | SSE 预检、游标解析、终态 204 |

修改 API 文件：ApiApplication.cs 注册组件及路由、在 ApplicationStopping 终止订阅、避免已开始响应改写成 ProblemDetails；ApiRuntimeConfiguration.cs 向两个长期 Runner 注入 Hub；Contracts/CommandDtos.cs 增加只读 eventsUrl。

Core 生产变更 **0**；Persistence 生产变更 **0**；MCP adapter 生产变更 **0**。无新项目、表或 migration。业务 sample / Infrastructure 无变更。

RunOperationSignals、AcknowledgingRunStateStore、RunExecutionCoordinator **均未修改**。命令提交确认、执行所有权与事件订阅仍是三个独立职责。事件仍由 AgentRunner 生成，先落库再通知；Hub 从不写 SQLite。

## 接口与精确格式

`GET /api/runs/{runId}/events` 成功流为 HTTP 200、`Content-Type: text/event-stream`、`Cache-Control: no-cache, no-store`。禁用响应缓冲并刷新响应头；每条事件使用 LF 分行、空行终止，写入后显式刷新：

```text
id: 11
event: RunResumed
data: {"eventId":"802f18bc-9ffd-456f-89c9-ba0d4aba3092","runId":"ca8a26d5-9ee9-494a-9f15-5a8f71c6bc51","sequence":11,"occurredAt":"2026-09-27T08:09:39.9737837+00:00","eventType":"RunResumed","payload":{}}

```

字段固定为 eventId、runId、sequence、occurredAt、eventType、payload。eventType 使用名称字符串，payload 克隆后序列化。id 等于 Sequence，不是 EventId。

不从 RunState 补充提示词、参数、工具输出、最终回答、异常或凭据。终态事件只表示进度，最终回答继续使用 GET Run 查询。

游标优先级为非空 Last-Event-ID → afterSequence → 0。采用非负 Int64 十进制数字解析，非法/负值/溢出返回 400 invalid_request；非法 header 不回退到 query。未来游标不重置，永远只读 Sequence > cursor。编号允许空洞。

开流前未知 Run 返回 404 run_not_found；终态 Completed / Failed / Cancelled / LimitReached 且 cursor ≥ RunState.LastSequence 返回 204，不创建订阅。其余状态开始 SSE。响应开始后的数据库/网络故障中止连接，不尝试写 ProblemDetails。

## 回放与实时补读

1. 预检 Run 存在，注册 Hub 订阅，再执行第一笔历史读取。
2. 从 lastSent=cursor 开始，每页最多 256 条，按数据库 Sequence 升序写入。
3. 一条完整 SSE 事件写入并 Flush 成功后才推进 lastSent。
4. 追上历史后等待订阅通知或约 15 秒空闲超时；唤醒后先清空当前通知队列，再按 lastSent 从 SQLite 补读，不按通知值直接发事件。
5. 超时同样先补读数据库；无新事件才发送 `: keepalive\n\n`，不生成/持久化 ExecutionEvent、不推进游标。
6. 成功刷新 RunCompleted、RunFailed、RunCancelled、RunLimitReached 后立即结束，不再发送心跳。ApprovalRequired 非终态，连接继续等待审批恢复事件。

RunEventHub 不持久化。SQLite ExecutionEvents 是唯一事件历史。重复、延迟、乱序或虚假的高序号通知最多导致一次数据库检查；丢通知由周期补读恢复。Running 的 snapshotSequence 可能落后，不能限制回放或替代 SSE 游标。

## Hub、缓冲与生命周期

每个订阅独立 Channel<long>，只保存序号通知；默认容量 256、BoundedChannelFullMode.Wait、关闭同步 continuation。发布路径仅 TryWrite，无网络读写、DB 读取或容量等待；同一 Run 的订阅获得广播副本，不竞争同一队列。

满缓冲区的订阅标记 Overflowed、从 Hub 移除、完成 Channel，并通过 CancelAsync 立即请求订阅取消、异步执行取消回调。发布路径不等待可能缓慢的取消回调。订阅异步释放时观察回调任务并释放 CTS。

SSE 读写令牌链接 RequestAborted、该订阅的终止令牌、ApplicationStopping，绝不链接回 Coordinator 的执行 CTS。溢出能中断正在阻塞的 HTTP 写入；其他订阅与 Run 不受影响。终态、断连、溢出、DB/网络故障和宿主关闭均清理订阅。

订阅采用一次一个带超时的等待，不积累未完成 channel read 或 timer task。宿主关闭时 Hub 只终止订阅；运行取消继续由既有 Coordinator 管理。

## 自动验证

实际运行 `dotnet build PortableAgent.sln`：**0 警告、0 错误**。

实际运行 `dotnet test PortableAgent.sln`：

| 项目 | 通过 |
| --- | ---: |
| Core.Tests | 41 |
| IntegrationTests | 40 |
| Persistence.Tests | 49 |
| Adapters.Mcp.Tests | 72 |
| Api.Tests | 66 |
| 合计 | **268** |

0 失败、0 跳过。API 新增 34 项，原 32 项继续通过。独立 SQLite 和真实 Kestrel 用于 HTTP/SSE 集成验证，阻塞写入通过受控响应 Stream 确定性模拟。

新增测试覆盖：

- 独立广播、不同 Run 隔离、无订阅立即返回、幂等移除、并发发布/订阅/释放无遗留条目。
- 容量 1 的订阅溢出；慢取消回调被阻塞时 PublishAsync 仍同步完成，快速订阅健康。
- 完整历史的 id/event/JSON 一致性、EventId/OccurredAt/payload 保真、既有脱敏边界、终态 EOF。
- Header/query 优先级，非法/空 query/负数/Int64 溢出、未知 Run 的预检错误，终态已消费或未来游标 204。
- 两个真实 SSE 客户端同时跟随活动 Run；一个断开后另一个继续，Run 仍活动并最终完成；重连补齐后与完整数据库历史相同。
- 在实际订阅已注册、历史页已读取但尚未发送时释放阻塞模型，让新事件提交；随后继续回放，确认无丢失/重复且按序输出。
- 注入持续抛异常的 live sink，Runtime 隔离通知失败；50ms 测试轮询仍从 SQLite 收到最终事件，且空闲心跳不带 id。
- 重复/乱序/未落库的高序号通知不直接输出；300 条有编号空洞的持久化历史跨越分页正确发送。
- AwaitingApproval 在同一连接上继续批准或拒绝，ApprovalResolved、RunResumed 与 RunCompleted 只有一条连续序列、无第二个 RunStarted。
- 非活动 Running 回放超出快照的事件后保持心跳，未来游标不复位，不改写生命周期。
- Failed、Cancelled、LimitReached 分别刷新终态后关闭，再次已消费重连 204。
- DB 读取失败与响应写入失败清理订阅，不输出 ProblemDetails，不取消 Run；宿主关闭清空订阅。
- 容量 2 的慢订阅在第 2 个 HTTP 事件写入处阻塞；发布 3 个通知后阻塞写被取消，快速受控订阅正常读取，Run 仍活动并最终完成；以最后成功的 id=1 通过真实 HTTP 重连，补齐到终态且事件与 SQLite 一致。
- 真实 Flight sample handler、官方 MCP HTTP transport、独立 Kestrel 监听器与默认 API 注册贯通，同一 SSE 连接经历审批全过程，CancelCallCount=1、CancelMutationCount=1。

## 独立进程 HTTP + SSE + Flight MCP 验证

实际启动已构建的 Flight sample（localhost:5103/mcp）与 API（localhost:5100），API 使用独立数据库 `TestResults/phase6b/manual.db`。

POST `{"agentId":"flight","message":"Cancel my booking."}` 返回 202，RunId 为 `ca8a26d5-9ee9-494a-9f15-5a8f71c6bc51`，响应包含 eventsUrl。

PowerShell HttpClient 使用 ResponseHeadersRead 打开一条 SSE 连接，读到 ApprovalRequired 后通过另一个 HTTP 请求批准；同一 reader 继续读到 EOF。审批返回 202，实际完整轨迹如下：

```text
 1 RunStarted
 2 ToolDiscoveryStarted
 3 ToolDiscoveryCompleted
 4 ModelTurnStarted
 5 ModelTurnCompleted
 6 ToolCallProposed
 7 PolicyEvaluationStarted
 8 PolicyEvaluationCompleted
 9 ApprovalRequired
10 ApprovalResolved
11 RunResumed
12 ToolDiscoveryStarted
13 ToolDiscoveryCompleted
14 PolicyEvaluationStarted
15 PolicyEvaluationCompleted
16 ToolExecutionStarted
17 ToolExecutionCompleted
18 ModelTurnStarted
19 ModelTurnCompleted
20 RunCompleted
```

随后 GET Run 返回 Completed、modelTurns=2、toolCalls=1、snapshotSequence=20、isActive=false，finalText 为 `Booking NZ123 has been cancelled.`。

`Last-Event-ID: 20` 重连返回 **204**；`afterSequence=9` 返回 **200 SSE**，只回放 10–20 并关闭。直接通过 MCP 查询 NZ123，业务状态为 cancelled。

Flight 日志在额外诊断查询之前显示两次 tools/list、一次 tools/call，工具为 cancel_booking。真实 API + MCP 自动测试另直接检查 sample 的调用计数与变更计数均为 1；没有给生产业务 sample 添加诊断接口。

原始 wire、补读、JSON 轨迹和脚本保存在忽略的 `TestResults/phase6b/stream.sse`、`replay-after-9.sse`、`http-sse-trace.json`、`verify.ps1`。日志、数据库和验证脚本不提交到生产代码。

## Phase 7 前已知限制

- API 仍仅供本地开发，无认证、CORS 扩展、Session、真实模型或前端；每条消息是独立 Run。
- Hub 是进程内通知；其他进程的提交要等待轮询才能看到。没有分布式实时广播或 exactly-once 网络交付承诺。
- 服务端成功 Flush 不等于客户端已处理；客户端保存最后完整事件 id，重连按该游标去重/恢复。
- 15 秒是空闲补读间隔，不保证受阻网络或繁忙数据库中的硬延迟上界。每个订阅独立读取 SQLite，连接规模和吞吐尚未优化。
- 持久化 Running + isActive=false 不恢复，不伪造终态；SSE 可无限心跳，未来 Studio 可查询后停止跟随。未来游标不回退；若后续事件始终不超过它，就继续等待。
- AwaitingApproval 不能取消；SSE 断连不取消 Run。详细失败原因不持久化查询，终态后查询 GET Run 获取最终回答。
- MCP adapter 继续按 Agent 串行化调用；无自动工具重试、远端副作用回滚或 Running 崩溃恢复。

没有新增 history/trace API、SignalR、WebSocket 或 Developer Studio。Phase 7 需要单独授权。

实现对照的官方资料：[有界 Channel 与 TryWrite 语义](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)、[ASP.NET Core 响应写入与 Flush](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-response?view=aspnetcore-10.0)。
