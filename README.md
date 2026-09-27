# PortableAgent

使用 C# / .NET 构建的可迁移、业务无关的 Agent Runtime 与适配框架。

项目目标是让业务应用通过工具、上下文、策略和配置接入同一个 Runtime，而无需把 Agent 执行逻辑写进各自的业务系统。未来的 Developer Studio 将通过聊天内的实时执行进度，展示 Agent 如何调用工具并完成任务。

本项目也用于循序学习 C#、异步编程、接口设计、测试和 Agent 执行机制。每次只引入一个可理解、可验证的实现单元。

## 当前状态

**Phase 0 架构基线、Phase 1 最小执行循环、Phase 2 本地领域可迁移性验证、Phase 3 结构化执行事件已完成。**

当前没有接入真实大模型。模型实现都是固定脚本，仅识别约定的演示输入；无需 API Key，也不会调用模型服务。宠物寄养与航班预订通过不同的模型和工具组合，共用同一个 `AgentRunner` 和 Core 程序集。Phase 2 没有修改 Core；Phase 3 在 Core 中加入了业务无关的执行事件观察能力。

这仍是本地模拟验证，不是完整 Agent 平台。MCP、策略、审批、持久化、技术遥测、API 和 Studio 均未实现。

演示流程：

```text
PetBoardingScriptedModelProvider + PetBoardingToolProvider + PetBoardingToolExecutor
  → AgentRunner → get_care_records → JSON 结果 → 模型验证 → Completed

FlightBookingScriptedModelProvider + FlightBookingToolProvider + FlightBookingToolExecutor
  → AgentRunner → get_booking → JSON 结果 → 模型验证 → Completed
```

已实现：

- 三个接口：`IModelProvider`、`IToolProvider`、`IToolExecutor`。
- 由 `AgentRunner` 独立控制的执行循环。
- 内部 `ToolId` 与模型可见 `ModelName` 的映射及唯一性检查。
- 用户消息、Assistant 工具请求、Tool 结果和最终回答的结构化表示。
- 每次模型请求使用独立的对话快照。
- Run 内非空且唯一的 `CallId`，以及工具结果关联校验。
- 多工具调用的顺序执行与整批预算检查。
- `Completed`、`Failed`、`Cancelled`、`LimitReached` 四种终止状态。
- 通过 Runner 构造函数传入的可信 `RunLimits`，默认最多 4 次模型调用和 4 次工具调用。
- JSON 生命周期处理，以及围绕执行循环的 14 个自动化测试用例。
- 两个领域各自的查询与模拟操作，以及 9 个集成测试用例。
- 结构化执行事件、可选异步 sink、Console Trace，以及 16 个新增事件测试用例（合计 39 个）。

| 领域 | 模型可见工具 | 可信内部 ToolId |
| --- | --- | --- |
| Pet Boarding | `get_care_records` | `pet-local / care.get_records` |
| Pet Boarding | `create_staff_task` | `pet-local / staff.create_task` |
| Flight Booking | `get_booking` | `flight-local / booking.get` |
| Flight Booking | `cancel_booking` | `flight-local / booking.cancel` |

员工任务创建和取消预订都返回固定模拟结果，没有外部调用或持久化；取消后的再次查询仍返回固定的 confirmed 预订。`my booking` 固定指向 NZ123，不代表已实现用户身份或授权。

本单元中，工具失败会直接终止运行，不自动重试。取消采用标准 `CancellationToken` 协作机制；尚无超时框架。

## 快速开始

需要安装 **.NET 10 SDK**。首次构建需要还原 NuGet 测试依赖。

```sh
git clone https://github.com/Treekay/PortableAgent.git
cd PortableAgent

dotnet build PortableAgent.sln
dotnet test PortableAgent.sln
dotnet run --project src/PortableAgent.Console
```

Console 顺序展示两个领域的 Trace，以下为航班部分的实际输出：

```text
=== Flight Booking ===
Show my booking.
[1] Run started
[2] Discovering tools
[3] Discovered 2 tools
[4] Model turn 1 started
[5] Model turn 1 completed · ToolCalls
[6] Tool proposed · get_booking
[7] Tool execution started · get_booking
[8] Tool execution completed · get_booking · success=True
[9] Model turn 2 started
[10] Model turn 2 completed · Completed
[11] Run completed · 2 model turns · 1 tool call(s)
Booking NZ123 is confirmed from Auckland to Sydney on 2026-10-10.
```

Console 顺序执行两次独立组合的查询演示，没有菜单或交互式聊天。两个模拟操作场景由集成测试覆盖。Phase 1 的加法脚本、Provider 和 Executor 原样保留，但不再是默认 Console 演示。

## 项目结构与依赖

```text
PortableAgent.sln
src/
  PortableAgent.Core/
    Execution/          执行循环、请求、结果及运行限制
      Events/           事件契约、枚举、sink 接口与安全包装
    Models/             中立模型契约和结构化消息
    Tools/              工具身份、定义、调用、结果及接口
  PortableAgent.Infrastructure/
    Models/             加法、PetBoarding、FlightBooking 脚本
    Tools/              原有加法工具
      PetBoarding/      宠物工具目录与执行器
      FlightBooking/    航班工具目录与执行器
  PortableAgent.Console/
    Program.cs          直接通过构造函数组合依赖并运行演示
    ConsoleExecutionEventSink.cs
tests/
  PortableAgent.Core.Tests/
    AgentRunnerTests.cs
    ExecutionEventTests.cs
    TestDoubles/        记录模型请求和工具调用的测试替身
  PortableAgent.IntegrationTests/
    DomainPortabilityTests.cs
    RecordingWrappers.cs
```

```text
Console ────────────→ Core
   └→ Infrastructure ─→ Core

Core.Tests ─────────→ Core
IntegrationTests ───→ Core + Infrastructure
```

Core 不依赖模型 SDK、MCP、EF Core、ASP.NET Core 或业务领域类型。当前没有 DI 容器，Console 中的组合关系可以直接阅读。

## 执行边界

模型提出操作，Runtime 校验并调度工具；业务系统仍应负责最终的业务合法性和权限检查。

当前 Runtime 的校验仅包括注册工具查找、调用关联、回复结构及执行预算，**尚未实现策略引擎或业务授权**。

工具内部身份由 `SourceId` 和 `Name` 组成。例如：

```text
ToolId:    local / calculator.add
ModelName: calculator_add
```

模型只提出工具名称和参数；实际执行目标由 Runtime 在已注册目录中解析，不接受模型指定任意服务器地址。

工具目录提供输入 Schema，但当前没有通用 JSON Schema 校验器。加法执行器自行验证 `a`、`b`，使用 `decimal` 运算并处理溢出。

## 执行事件

事件是执行事实的观察通道，不决定模型或工具的下一步，也不是恢复执行所需的状态。本项目没有采用事件溯源。

`ExecutionEvent` 只有 `EventId`、`RunId`、`Sequence`、UTC `OccurredAt`、`EventType` 和 JSON `Payload`。Runner 每次运行生成新的 RunId，序号从 1 开始；并发 Run 的身份、序号和预算计数独立。结果通过 `AgentRunResult.RunId` 与事件关联，请求仍只包含用户消息。

开始事件紧接着对应操作的调用，完成事件仅在组件正常返回后产生。`ToolExecutionCompleted(success=false)` 表示执行器返回失败结果；执行器抛异常时不产生该完成事件。模型返回的每个工具提案在 Runtime 验证前产生 `ToolCallProposed`，因此未知工具会有提案，但不会有工具执行开始事件。终止事件携带实际模型调用数和工具调用数。

`IExecutionEventSink` 是可选构造依赖，默认空实现。每个 Run 使用一个安全包装器隔离消费者异常：失败不重试、不回退序号，也不改变 Agent 结果。包装器仅保留内部故障计数供调试，不向 `AgentRunResult` 加入观察者健康字段。消费者自己的取消异常只有在 Run 令牌实际取消时才归为 Run 取消，否则按消费者故障处理。

普通事件使用 Run 的取消令牌；四种终止事件使用 `CancellationToken.None` 尽力投递。预先取消的请求仍获得 RunId、返回 Cancelled 并尝试发送 RunCancelled，但普通事件可能未被观察到。投递失败可能造成序号间隙，当前没有持久化、重放或可靠交付保证。

当前顺序等待进程内 sink，**慢 sink 会增加运行延迟，不返回的 sink 也会阻塞调用**。共享 sink 需要自行保证并发安全；本阶段不引入后台队列或网络传输。

Payload 仅选择性包含计数、调用 ID、工具名称/身份、结束原因和成功状态，不自动复制用户消息、完整参数、输出、最终回答或异常原文。未来接入不可信来源时，仍需单独设计名称等元数据的脱敏和大小限制。

领域脚本中的业务检查只是确定性模拟。未来真实模型适配器负责协议转换，通用工具执行器转发可信调用，业务系统负责权限、业务校验和数据一致性；这些规则不应迁入 Core。

## 测试与代码阅读

Core 测试只引用 Core，使用记录型替身验证控制流；集成测试引用 Core 和 Infrastructure，通过薄记录包装器转发给真实领域实现。Console 使用相同的真实领域组件。

测试覆盖成功闭环、未知工具、两种预算限制、工具失败、预先取消、错误关联 ID、重复注册身份、空或重复调用 ID、多工具顺序以及 JSON 生命周期。

集成测试验证四个领域场景、宠物目录拒绝未注册的航班工具，以及四种脚本不会对错误业务结果返回成功确认。

事件测试验证语义顺序、关联和序号、四种终止状态、投递故障隔离、sink 自发取消、Run 取消令牌传播、并发 Run、失败投递不重试以及 Payload 范围。原 Phase 1/2 测试不依赖事件展示文本。

建议按以下顺序阅读：

1. [Console 入口](src/PortableAgent.Console/Program.cs)：查看组件如何组合。
2. [AgentRunner](src/PortableAgent.Core/Execution/AgentRunner.cs)：理解循环、执行前检查和终止条件。
3. [AgentMessage](src/PortableAgent.Core/Models/AgentMessage.cs)：查看四种消息工厂和 JSON 副本处理。
4. [成功闭环及边界测试](tests/PortableAgent.Core.Tests/AgentRunnerTests.cs)：观察两次模型请求和一次工具调用如何被验证。
5. [脚本模型](src/PortableAgent.Infrastructure/Models/ScriptedModelProvider.cs)：查看它如何根据对话结构确定阶段。
6. [本地工具执行器](src/PortableAgent.Infrastructure/Tools/LocalToolExecutor.cs)：查看可信内部身份如何对应实际操作。

Phase 2 建议继续阅读 [领域迁移测试](tests/PortableAgent.IntegrationTests/DomainPortabilityTests.cs)、[宠物脚本](src/PortableAgent.Infrastructure/Models/PetBoardingScriptedModelProvider.cs) 和 [宠物执行器](src/PortableAgent.Infrastructure/Tools/PetBoarding/PetBoardingToolExecutor.cs)，再对照 FlightBooking 的对应实现。

Phase 3 建议阅读 [事件契约](src/PortableAgent.Core/Execution/Events/ExecutionEvent.cs)、[安全 sink](src/PortableAgent.Core/Execution/Events/SafeExecutionEventSink.cs)、[事件测试](tests/PortableAgent.Core.Tests/ExecutionEventTests.cs) 和 [Console renderer](src/PortableAgent.Console/ConsoleExecutionEventSink.cs)。

## 后续方向

以下是分阶段计划，不代表当前已经支持：

| 阶段 | 目标 |
| --- | --- |
| Phase 4 | 策略、审批，以及已持久化待审批 Run 的重启恢复 |
| Phase 5 | MCP 适配器与协议迁移验证 |
| Phase 6 | ASP.NET Core API、HTTP 命令与 SSE 订阅 |
| Phase 7 | Developer Studio：Chat 与内联执行进度 |

早期不引入多 Agent、RAG、向量数据库、分布式执行、动态插件加载或工作流图引擎。Graph 将来只展示执行结构，不决定 Runtime 如何运行。
