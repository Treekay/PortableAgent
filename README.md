# PortableAgent

使用 C# / .NET 构建的可迁移、业务无关的 Agent Runtime 与适配框架。

业务应用提供工具、策略和可信配置，同一个 Runtime 负责模型循环、校验、工具调度及审批恢复。本项目也用于逐步学习 C#、异步编程、接口设计、测试和 AI 应用开发。

## 当前状态

**Phase 0–3、Phase 4A（策略与内存审批）、Phase 4B（SQLite 持久化与待审批检查点重启恢复）已完成。**

当前模型都是确定性脚本，只识别约定演示输入，无需 API Key。宠物寄养与航班预订使用不同的模型和工具组合，共用 Core。MCP、真实模型、HTTP API、SSE、Studio 尚未实现。

已支持：

- 中立的 `IModelProvider`、`IToolProvider`、`IToolExecutor`，以及结构化消息、工具调用和工具结果。
- 可信 `ToolId` 与模型可见 `ModelName` 映射、唯一 CallId、结果关联校验、整批预算检查和顺序执行。
- 默认最多 4 次模型调用和 4 次工具调用；预算由 Runner 构造提供，恢复沿用保存的预算。
- Allow / Deny / RequireApproval，以及单调用人工审批。
- `Completed`、`Failed`、`Cancelled`、`LimitReached` 四种终止状态与非终止 `AwaitingApproval`。
- 17 种结构化执行事件、可选 live sink、SQLite 事件历史及有界分页读取。
- JSON 状态快照、版本 CAS、关键状态与事件的原子保存。
- **AwaitingApproval 成功保存 → 进程退出 → 新进程批准/拒绝 → 恢复同一个 Run。**

| 领域 | 模型可见工具 | 可信 ToolId |
| --- | --- | --- |
| Pet Boarding | `get_care_records` | `pet-local / care.get_records` |
| Pet Boarding | `create_staff_task` | `pet-local / staff.create_task` |
| Flight Booking | `get_booking` | `flight-local / booking.get` |
| Flight Booking | `cancel_booking` | `flight-local / booking.cancel` |

业务工具仍返回固定模拟结果，没有实际取消航班或创建员工任务。SQLite 保存的是 Runtime 状态和事件，不是业务系统数据库。`my booking` 固定对应 NZ123，不代表用户身份或资源授权。

## 快速开始：真正的跨进程恢复

需要 **.NET 10 SDK**；首次构建需要还原 NuGet 包。

```sh
git clone https://github.com/Treekay/PortableAgent.git
cd PortableAgent
dotnet build PortableAgent.sln
dotnet test PortableAgent.sln

dotnet run --project src/PortableAgent.Console -- init
dotnet run --project src/PortableAgent.Console -- pause
```

`init` 显式应用 EF migration，可重复执行，不删除已有记录。数据库为**命令当前工作目录**下的 `portable-agent.db`，每次打印绝对路径。后续命令必须使用同一目录；`pause/approve/reject` 不自动创建数据库或应用迁移。数据库及 SQLite 辅助文件已加入 `.gitignore`。

`pause` 打印以下信息后退出进程：

```text
Database path: <absolute-path>/portable-agent.db
...
[9] Approval required · <approval-id>
RunId: <run-id>
Status: AwaitingApproval
ApprovalId: <approval-id>
```

用输出的两个 ID，启动另一个进程：

```sh
dotnet run --project src/PortableAgent.Console -- approve <run-id> <approval-id>
```

批准成功时，事件从序号 10 继续，无第二个 RunStarted；最终输出 `Status: Completed` 和 `Booking NZ123 has been cancelled.`。

验证拒绝时，先执行一次新的 `pause`，再使用这次的新 ID：

```sh
dotnet run --project src/PortableAgent.Console -- reject <run-id> <approval-id>
```

拒绝路径不调用工具，模型得到 `RejectedByUser`，输出 `Understood. I did not cancel the booking.`。重复提交返回 `Conflict`，未知 RunId 返回 `NotFound`。无参数只显示用法，没有通用聊天 shell。

[Phase 4B 验证记录](docs/phase4b-verification.md) 包含实际进程输出、SQLite 表结构、测试结果与边界说明。

## 项目结构与依赖

```text
src/
  PortableAgent.Core/
    Execution/             AgentRunner、RunState、审批和 Store 契约
      Events/              事件类型、sink、安全观察包装
    Models/                中立模型契约和消息工厂
    Policies/              策略契约、默认 AllowAll
    Tools/                 工具身份、定义、调用和结果
  PortableAgent.Infrastructure/
    Execution/             InMemoryRunStateStore
    Policies/              按可信 ToolId 配置的内存策略
    Models/                加法、宠物、航班脚本
    Tools/                 本地加法、PetBoarding、FlightBooking
  PortableAgent.Persistence/
    Sqlite/
      PortableAgentDbContext.cs
      PortableAgentDbContextFactory.cs
      SqliteRunStateStore.cs
      Entities/            Runs、ExecutionEvents 映射
      Serialization/       显式 SnapshotDtos、RunStateSerializer
      Migrations/          InitialPersistence 与模型快照
  PortableAgent.Console/   init / pause / approve / reject，事件展示
tests/
  PortableAgent.Core.Tests/
  PortableAgent.IntegrationTests/
  PortableAgent.Persistence.Tests/
dotnet-tools.json          固定版本的本地 dotnet-ef 工具
```

```text
Console → Core + Infrastructure + Persistence
Infrastructure → Core
Persistence → Core
Core.Tests → Core
IntegrationTests → Core + Infrastructure
Persistence.Tests → Core + Infrastructure + Persistence
```

Core 不引用 EF Core、SQLite、模型 SDK、MCP、ASP.NET Core 或业务类型。无 DI 容器、通用 repository、UnitOfWork 或额外 service 包装。

## 策略和恢复边界

模型提出操作，Runtime 校验和调度工具；业务系统仍负责身份认证、资源权限、业务合法性和最终一致性。人工批准不能替代这些检查。工具由可信目录解析，模型不能指定任意服务器地址。输入 Schema 暂无通用校验器，各领域执行器自行检查参数。

无策略配置时采用 AllowAll；显式 `InMemoryPolicyEvaluator` 对未配置工具默认 Deny。Console 允许查询，取消要求确认。RequireApproval 必须配置 Store。

每个模型批次在任何工具执行前完成全部工具、预算和策略检查：

| 批次政策 | 行为 |
| --- | --- |
| 全部 Allow | 顺序执行 |
| 存在 Deny | 整批零执行，分别反馈 DeniedByPolicy / NotExecutedDueToBatchPolicy |
| 无 Deny，多调用且存在 RequireApproval | Failed，零执行；尚不支持多调用审批 |
| 单调用 RequireApproval | 冻结参数、原子保存，返回 AwaitingApproval |

`ToolResult.Disposition` 默认 Executed；实际工具失败终止运行。策略拒绝和用户拒绝是未执行结果，会交回模型继续处理。第二个工具失败不会回滚第一个工具。

`PendingApproval` 保存 ApprovalId、RunId、完整工具定义、精确 ToolCall、PolicyId、PolicyReason、时间和状态。批准后不会让模型重新生成参数。版本 CAS 只允许一个审批提交获得执行权，其他提交返回 Conflict，不执行工具，不写审批事件。

每次恢复重新发现当前可信工具。批准时比较 ToolId、ModelName、InputSchema（JSON 结构相等），不兼容则失败关闭；随后重新评估策略。Allow 执行冻结调用，Deny 反馈拒绝，RequireApproval 检查政策身份：命名 PolicyId 必须 ordinal 相等；双方 ID 都为 null 时还要求 PolicyReason ordinal 相等。身份变化则生成新 ApprovalId，再次暂停。政策实质变化需由配置方分配新 ID。

拒绝时追加 RejectedByUser 并继续模型；同样刷新工具目录，后续提案不会使用历史快照作为当前可信能力。恢复延续 RunId、CallId 历史、计数和预算。CAS 前取消不消费审批，CAS 后取消不会重置 Pending。

### 稳定的 RuntimeDefinitionId

Runner 构造函数要求非空白字符串，Console 固定为 `flight-demo-v1`。它由可信组合代码提供，不出现在用户请求或审批命令中。恢复按 ordinal / 大小写敏感方式匹配；不一致返回 Conflict，不修改状态。

它替代 Phase 4A 的进程内 RuntimeId，允许新的 Runner 和组件恢复同一可信配置的 Run。它是由应用维护的粗粒度组合身份，不是认证、自动配置指纹或 manifest；不能随意在不同执行器/模型配置之间复用。

## Phase 4B：状态与事件持久化

SQLite Store 每次操作创建并释放自己的 DbContext，使用短事务，不跨模型、工具或 sink 调用持有数据库事务。SQLite 锁等待上限配置为 5 秒；无应用层自动重试。

两张业务表为 `Runs` 和 `ExecutionEvents`，另有 EF 迁移管理表。Runs 保存关系型身份、生命周期、版本、更新时间和 StateJson。ExecutionEvents 以 EventId 为主键，RunId 为外键，`(RunId, Sequence)` 唯一。

状态格式为：

```json
{ "schemaVersion": 1, "state": { } }
```

`state` 实际保存全部 RunState 数据：可信配置身份、生命周期和 Version、对话、工具目录、待审批和已解决审批、模型/工具计数、原 RunLimits、UsedCallIds 和 LastSequence。显式 DTO 映射通过 AgentMessage 工厂重建消息，拥有独立 JSON/集合；UsedCallIds 恢复 ordinal 比较语义。缺失字段、畸形消息、未知枚举和未知 schemaVersion 明确失败。当前只支持 v1，没有快照升级机制。Task、模型实例、委托、执行器、sink、CancellationToken 均不序列化。

Store 契约（每个操作还接收 CancellationToken）：

```text
CreateAsync(snapshot, events)
GetAsync(runId)
TryReplaceAsync(runId, expectedVersion, replacement, events)
AppendEventAsync(executionEvent)
ReadEventsAfterAsync(runId, afterSequence, limit)
```

Create/TryReplace 的状态和事件同事务提交。内存 Store 保留同一契约，复制读写数据并在短锁内完成原子操作。

| 边界 | 原子写入内容 | 提交后 |
| --- | --- | --- |
| 初始运行 | Running 快照 + RunStarted | live sink，再发现工具/调用模型 |
| 暂停 | AwaitingApproval 快照（含已分配 LastSequence）+ ApprovalRequired | live sink，返回 AwaitingApproval |
| 审批 CAS | Running 快照 + 已解决审批 + ApprovalResolved + RunResumed | live sink，重新发现工具和校验 |
| 终止 | 最终快照 + 对应终止事件 | live sink，返回结果 |

审批 CAS 的条件包含 RunId、预期 Version、AwaitingApproval 生命周期及 RuntimeDefinitionId。只有一行更新成功才能插入审批事件并提交；事件插入失败回滚状态和全部同批事件。暂停事务失败返回 Failed，不留下部分待审批检查点。

普通事件分配 Sequence 后先写数据库，再投递 live sink。`ToolExecutionStarted` 保存失败时不调用执行器。执行器已返回、但 `ToolExecutionCompleted` 保存失败时返回 Failed，不重试，并提示副作用可能已经发生。最后的状态与终止事件保存失败时返回 Failed，不发送未落库的终止观察；数据库可能仍显示 Running。

事件查询只返回 `Sequence > afterSequence`，按 Sequence 升序，limit 必须是 1–1000；不按时间排序。事件不是恢复源，不通过事件重建 Run。普通事件不逐条更新 StateJson，Running 快照可能落后于事件历史。已分配但写入失败的序号可以留空洞。

### 执行事件与数据范围

17 种事件覆盖运行、工具发现、模型轮次、工具提案/执行、策略、审批及恢复。每条包含 EventId、RunId、Sequence、UTC OccurredAt、EventType 和 JSON Payload。新 Run 序号从 1 开始，恢复不再次发送 RunStarted。

Payload 只选择性保存计数、CallId、工具名称/身份、PolicyId、状态及成功标记；不复制原始用户消息、工具参数/输出、模型回答或异常原文。**RunState 快照则包含恢复必需的用户/业务对话、参数及结果，并未脱敏。** 未来接入不可信来源时仍需限制元数据大小并制定保留、授权和脱敏规则。

`SafeExecutionEventSink` 只隔离 live observer 故障，数据库写入不经过它。live sink 失败不改变运行语义，不重试；历史可通过 Store 重新读取。普通事件使用运行取消令牌，已提交的审批事实和终止事件使用 None 尽力投递。未配置 Store 时保留进程内观察模式。

当前仍顺序等待 sink，慢或不返回的 sink 会阻塞执行。共享 sink 需自行保证并发安全。本阶段没有后台队列、HTTP API、SSE 或可靠实时交付保证。

### EF 迁移

`Microsoft.EntityFrameworkCore.Sqlite`、`Microsoft.EntityFrameworkCore.Design` 和本地 `dotnet-ef` 固定为 **10.0.12**。初始迁移为 `20260927044802_InitialPersistence`。正常演示通过 `init` 调用 MigrateAsync，不使用 EnsureCreated。

维护迁移时：

```sh
dotnet tool restore
dotnet ef migrations list --project src/PortableAgent.Persistence
dotnet ef migrations add <Name> --project src/PortableAgent.Persistence --output-dir Sqlite/Migrations
```

普通构建和 Console 的 `init` 不需要安装全局 dotnet-ef。

## 明确不支持的崩溃恢复

Phase 4B **只恢复成功持久化的 AwaitingApproval**，通过显式批准或拒绝命令进入执行。初始 Running 记录用于保存历史，不代表 Running 可以恢复。

不支持 Running 自动恢复、模型调用中崩溃恢复、工具执行中崩溃恢复、外部副作用 exactly-once、自动重试、补偿、事件溯源重建或后台恢复扫描。

- 审批 CAS 成功后、工具执行前崩溃：记录已经是 Running，审批已消费，本阶段不会自动恢复。
- 工具副作用成功后、后续持久化前崩溃：无法确定外部业务结果，不保证 exactly-once；未来业务系统需要适当的幂等机制。
- 终止事务失败或进程中断：不声称数据库中存在 Completed，不尝试自动修复。

## 测试与阅读顺序

`dotnet build PortableAgent.sln`：0 警告、0 错误。`dotnet test PortableAgent.sln`：**118 通过，0 失败，0 跳过**。

| 项目 | 用例数 | 重点 |
| --- | ---: | --- |
| Core.Tests | 30 | 执行循环、校验、预算、取消、事件语义 |
| IntegrationTests | 40 | 领域迁移、策略、批次、内存审批与并发 |
| Persistence.Tests | 48 | DTO、独立 Store、重启批准/拒绝、SQLite CAS、事务故障、事件顺序 |

原有 70 个测试保留行为断言，仅调整配置身份和 Store 签名；原“暂停保存失败”替身从 Create 故障改为暂停 Replace 故障，继续验证相同边界。持久化测试使用独立临时文件数据库，关闭连接池，释放上下文并清理自身文件，不复用演示数据库。事务故障通过 SQLite trigger 注入，测试真实数据库回滚。

建议依次阅读 [Console](src/PortableAgent.Console/Program.cs)、[AgentRunner](src/PortableAgent.Core/Execution/AgentRunner.cs)、[AgentMessage](src/PortableAgent.Core/Models/AgentMessage.cs)、[审批测试](tests/PortableAgent.IntegrationTests/ApprovalTests.cs)、[SqliteRunStateStore](src/PortableAgent.Persistence/Sqlite/SqliteRunStateStore.cs)、[快照映射](src/PortableAgent.Persistence/Sqlite/Serialization/RunStateSerializer.cs)、[重启测试](tests/PortableAgent.Persistence.Tests/RestartTests.cs) 和 [事务故障测试](tests/PortableAgent.Persistence.Tests/TransactionTests.cs)。

## 后续方向

| 阶段 | 目标 |
| --- | --- |
| Phase 5 | MCP 适配器与协议迁移验证 |
| Phase 6 | ASP.NET Core API、HTTP 命令与 SSE 订阅 |
| Phase 7 | Developer Studio：Chat 与内联执行进度 |

进入 MCP 前需要明确可信服务器配置与稳定 ToolId、名称映射、协议 Schema 兼容性、参数校验、凭据/资源授权及远程取消和副作用语义；RuntimeDefinitionId 仍需由配置方维护。当前 Phase 4B 未实现这些内容，也没有多 Agent、RAG、向量数据库、分布式执行、动态插件或工作流图引擎。
