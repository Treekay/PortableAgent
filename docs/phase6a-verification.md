# Phase 6A 验证记录

验证日期：2026-09-27（Pacific/Auckland）。基线：Phase 5C `6656f04085b0aef38989569d582b83a1655493ca`。

本阶段仅实现 ASP.NET Core 命令/查询宿主及 HTTP 生命周期与 Run 生命周期解耦。没有开始 Phase 6B。

## 变更范围

新增生产项目 `src/PortableAgent.Api`，包含：

- `Program.cs`、`ApiApplication.cs`：宿主构建、DI、JSON 严格解析、ProblemDetails 与路由。
- `Contracts/AgentDtos.cs`、`CommandDtos.cs`、`RunDtos.cs`：专用公开契约。
- `Endpoints/AgentEndpoints.cs`、`RunEndpoints.cs`、`ApiProblem.cs`：五个 HTTP 端点及错误映射。
- `Hosting/AgentRuntimeRegistration.cs`、`AgentRuntimeRegistry.cs`：可信长期注册、ordinal 唯一校验与资源释放。
- `Hosting/ApiRuntimeConfiguration.cs`：既有 Pet/Flight MCP 装配、SQLite 启动初始化。
- `Hosting/RunExecutionCoordinator.cs`：执行 Task、CTS、准入、取消与关闭。
- `Hosting/RunOperationSignals.cs`、`AcknowledgingRunStateStore.cs`：仅确认已提交的开始/审批操作。

API 单向引用 Core、Infrastructure、Persistence、Adapters.Mcp。业务 sample 不引用 API 或 Runtime。

Core 生产仅修改 `AgentRunner.cs`：新增 `RunAsync(Guid, AgentRunRequest, CancellationToken)`，拒绝 Guid.Empty，用可信宿主传入的 ID 创建 RunState；旧重载委托新重载并生成 Guid。请求契约与其他 Core 文件不变。

Persistence 生产改动 **0**，SQLite schema / migration 改动 **0**，MCP adapter 生产改动 **0**，Infrastructure 与业务 sample 生产改动 **0**。新增 `tests/PortableAgent.Api.Tests` 与 2 项 Core 测试；更新 solution、README 和本文。

## HTTP 契约

| 端点 | 成功状态 | 内容 |
| --- | --- | --- |
| GET `/api/agents` | 200 | id、name 数组 |
| POST `/api/runs` | 202 | runId、status=accepted、runUrl，Location 同 runUrl |
| GET `/api/runs/{runId}` | 200 | 持久化查询 DTO + 当前宿主 isActive |
| POST `/api/runs/{runId}/approvals/{approvalId}` | 202 | 同开始响应，额外包含 approvalId |
| POST `/api/runs/{runId}/cancel` | 202 | 取消信号已发出；不是已达到 Cancelled 的承诺 |

开始仅接受非空 agentId 和 1–8000 字符非空白 message。未知 JSON 属性返回 400，客户端不能指定 RunId、RuntimeDefinitionId 或远端端点。审批仅接受 approve / reject 字符串，不接受数值枚举或 AgentId。

400 / 404 / 409 / 503 / 500 均通过 ProblemDetails 返回适用的稳定 code：invalid_request、agent_not_found、run_not_found、approval_conflict、run_not_active、runtime_unavailable、host_stopping、internal_error。不将异常详情作为响应。

公开查询不返回原始 RunState、会话、工具目录或配置。pendingApproval 参数克隆为独立 JSON，表示实际授权的冻结操作。Completed 的 finalText 从持久化最后一条 Assistant 消息提取。Failed 只返回通用 run_failed，重启后保持同样查询结果。

## 提交确认与执行所有权

1. Coordinator 分配 ID、注册活动执行并预先 arm 确认信号；执行持有独立 CTS，可链接 ApplicationStopping。
2. Runner 调用 API 层 Store 装饰器，装饰器等待真实 SQLite 操作返回。
3. Create 成功且事件包含 RunStarted 才确认开始；TryReplace 返回 true 且同批包含 ApprovalResolved、RunResumed 才确认审批。
4. HTTP 竞争等待确认信号或执行结束。确认先出现就返回 202；开始未提交已结束返回 500；审批未 claim 时按 Core NotFound / Conflict 映射 404 / 409，其他异常返回 500。
5. RequestAborted 仅取消 HTTP 等待，不传给 Runner，不取消已调度执行；确认成功不等待模型、工具或最终回答。

确认源不是 live sink。装饰器不新增事件、事务或快照，也不持久化 Task/CTS。信号与活动记录在执行结束或 AwaitingApproval 返回后清理。

Coordinator 不使用 Task.Run，不捕获 HttpContext 或 request-scoped 服务；异步入口用一次 Task.Yield 让准入返回，随后直接持有并观察执行任务。每个 Agent 的 Runner、策略、脚本与 adapter 随注册存活；Store facade、signals、registry、coordinator 均为单例。Sqlite Store 继续使用每操作独立 DbContext。

相同 Run 的本机重复执行在 Coordinator 拒绝，跨实例审批所有权仍由 SQLite CAS 决定。审批通过保存的 RuntimeDefinitionId 匹配注册并使用新的 CTS。取消仅通知活动执行，绝不直接改写持久化状态；取消/完成竞态的实际终态由 Runner 决定。

关闭时停止准入并取消活动执行，在宿主关闭令牌期限内等待任务，随后 DI 释放 Registry 所有的 adapter；测试确认资源只释放一次。模型/工具依赖需要配合取消，硬退出或超过关闭期限可能留下 Running。

## 构建与自动验证

实际运行：

```text
dotnet build PortableAgent.sln
0 warnings, 0 errors

dotnet test PortableAgent.sln
Core.Tests           41 passed
IntegrationTests     40 passed
Persistence.Tests    49 passed
Adapters.Mcp.Tests   72 passed
Api.Tests            32 passed
Total               234 passed, 0 failed, 0 skipped
```

API 测试使用等价于集成测试宿主的真实 Kestrel 监听器，动态 loopback 端口、独立临时 SQLite、受控模型/工具及真实 HTTP 请求。无 mock HTTP 路由、无演示数据库复用。

32 项 API 用例覆盖：

- 公开 Agent 列表、未知 JSON 属性/数值 decision/空消息/超长消息/未知 Agent 拒绝，未生成 Run。
- 初始事务前等待、真实提交后 202、模型仍阻塞；数据库 RunId 与所有事件关联一致。
- 初始创建失败 500、不调用模型、不泄漏故障详情；重复可信 ID 不能覆盖原记录或事件。
- Running 快照序号/计数落后于已持久化事件；完成查询从保存的 Assistant 消息提取结果。
- 冻结审批内容、批准/拒绝提交后 202、审批事务失败不消费待审批、CAS 失败不发确认。
- 批准后的工具仍阻塞时 HTTP 已返回；同机活动重复审批以及终态重复审批均 409。
- 开始与审批的 HTTP 等待被客户端取消，后台操作仍完成且工具仅执行一次。
- 活动执行取消、恢复后的执行取消、审批已消费的事实保留、取消与完成竞争不改写终态。
- Failed 通用响应重启一致、历史终态查询、待审批跨宿主恢复、非活动 Running 不恢复且取消 409。
- 不兼容 Runtime 409、agentId=null、错误 ID 的稳定错误码、停止准入 HTTP 503。
- ordinal 注册唯一性、关闭取消活动任务、资源恰好释放一次。
- 真实 Flight sample MCP handler + 官方协议传输的 API 批准与拒绝；独立 Kestrel 端点，没有本地 executor 替代。

真实 Flight 自动测试确认：批准后 CancelCallCount=1、CancelMutationCount=1、订单 cancelled；拒绝后两计数均为 0、订单 confirmed。开始与恢复各发现一次工具。远端调用参数只包含 bookingId=NZ123，没有 RunId、ApprovalId；重复审批不会多发调用。

## 独立进程手动验证

从根目录启动已构建的 Flight sample 与 API DLL，使用 API 默认 localhost:5100、Flight localhost:5103/mcp；API 数据库独立配置为 `TestResults/phase6a/manual.db`，未重置既有演示数据。

实际请求为 `POST /api/runs`，正文 `{"agentId":"flight","message":"Cancel my booking."}`。

| 步骤 | 实际结果 |
| --- | --- |
| Agent 列表 | pet / Pet Boarding；flight / Flight Booking |
| 开始 | HTTP 202，RunId `84c0696a-51c6-40d2-a4e1-550ee9dd562f` |
| 查询暂停 | AwaitingApproval，modelTurns=1，toolCalls=0，snapshotSequence=9，isActive=false |
| 待审批 | ApprovalId `fa3a7115-7cb0-4bee-ac68-2dfab4a60cff`，flight-mcp/cancel_booking，参数 bookingId=NZ123 |
| 批准 | HTTP 202，同一 RunId 与 ApprovalId |
| 完成查询 | Completed，modelTurns=2，toolCalls=1，snapshotSequence=20，isActive=false |
| finalText | `Booking NZ123 has been cancelled.` |
| 重复批准 | HTTP 409 |
| 直接通过 MCP 查询业务 | `{"bookingId":"NZ123","route":"Auckland → Sydney","date":"2026-10-10","status":"cancelled"}` |
| 终止 API 后用同一数据库启动新进程 | 同一 Run 仍为 Completed，finalText、计数、snapshotSequence 相同，isActive=false |

人工 HTTP 原始结果保存在忽略的 `TestResults/phase6a/http-trace.json`、`restart-query.json`，日志与临时数据库不提交。精确远端调用及变更计数由上述真实 HTTP 自动测试验证，未给生产 sample 添加诊断端点。

## 已知限制与 Phase 6B 边界

- 本地开发 API，无认证、资源授权、前端、Session 或真实模型。每条消息都是独立 Run，scripted model 只识别既有固定输入。
- snapshotSequence 对应 RunState.LastSequence；Running 快照及计数可能落后于事件，不能当成 latest event sequence。
- isActive 仅属于当前宿主；重启后 Running + false 可查询，不自动恢复、不新增 Orphaned 生命周期。
- AwaitingApproval 只能显式批准/拒绝，当前不能取消；非活动 Running 和终态也不可取消。
- 详细失败原因不持久化查询，不根据事件猜测错误；持久化终态失败仍可能留下 Running。
- 每个 Agent 的长期 MCP adapter 用既有 SemaphoreSlim 串行化调用；未优化吞吐、自动重连或超时。
- 断开 HTTP 不取消执行，但断开发生在拿到新 RunId 前时，客户端可能不知道已经创建的 RunId。本阶段没有命令幂等键或 Run 列表检索。
- 无分布式执行协调、Running 崩溃恢复、自动重试、补偿或远端副作用 exactly-once。取消不是远端回滚。

未发现需要扩大 Phase 6A Core 变更或阻塞 Phase 6B 的问题。后续 SSE 必须独立建立持久化事件游标与订阅生命周期：不能使用本阶段瞬时操作确认信号作为事件源，不能把 snapshotSequence 当最新事件游标，也不能把订阅断开传播为执行取消。SafeExecutionEventSink 的观察隔离仍与命令持久化确认分离。

本阶段没有 `/api/runs/{id}/events`、RunEventHub、Channel subscriber、SSE、Last-Event-ID 或 heartbeat 实现。Phase 6B 等待单独授权。
