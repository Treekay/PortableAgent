# PortableAgent

使用 C# / .NET 构建的可迁移、业务无关的 Agent Runtime 与适配框架。

项目目标是让业务应用通过工具、上下文、策略和配置接入同一个 Runtime，而无需把 Agent 执行逻辑写进各自的业务系统。未来的 Developer Studio 将通过聊天内的实时执行进度，展示 Agent 如何调用工具并完成任务。

本项目也用于循序学习 C#、异步编程、接口设计、测试和 Agent 执行机制。每次只引入一个可理解、可验证的实现单元。

## 当前状态

**Phase 1 的第一个实现单元已完成：确定性的本地工具调用闭环。**

当前没有接入真实大模型。`ScriptedModelProvider` 是固定脚本，不解析用户意图；无需 API Key，也不会调用模型服务。两个业务领域的迁移验证尚未实现。

演示流程：

```text
固定用户消息：Calculate 2 + 3.
  → AgentRunner 加载本地工具目录
  → 脚本模型提出 calculator_add(a = 2, b = 3)
  → Runtime 查找可信工具并检查预算
  → 本地工具返回 { "result": 5 }
  → Runtime 将关联的工具结果加入对话
  → 脚本模型验证结果并返回 The result is 5.
  → Completed
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

Console 预期输出：

```text
Status: Completed
The result is 5.
```

Console 目前仅执行一次固定演示，不提供交互式聊天。修改用户文本不会使脚本模型变成通用计算助手。

## 项目结构与依赖

```text
PortableAgent.sln
src/
  PortableAgent.Core/
    Execution/          执行循环、请求、结果及运行限制
    Models/             中立模型契约和结构化消息
    Tools/              工具身份、定义、调用、结果及接口
  PortableAgent.Infrastructure/
    Models/             确定性 ScriptedModelProvider
    Tools/              本地工具目录与加法执行器
  PortableAgent.Console/
    Program.cs          直接通过构造函数组合依赖并运行演示
tests/
  PortableAgent.Core.Tests/
    AgentRunnerTests.cs
    TestDoubles/        记录模型请求和工具调用的测试替身
```

```text
Console ────────────→ Core
   └→ Infrastructure ─→ Core

Core.Tests ─────────→ Core
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

## 测试与代码阅读

Core 测试只引用 Core，使用记录型替身验证控制流；Console 演示使用 Infrastructure 中的实际脚本和本地加法实现。

测试覆盖成功闭环、未知工具、两种预算限制、工具失败、预先取消、错误关联 ID、重复注册身份、空或重复调用 ID、多工具顺序以及 JSON 生命周期。

建议按以下顺序阅读：

1. [Console 入口](src/PortableAgent.Console/Program.cs)：查看组件如何组合。
2. [AgentRunner](src/PortableAgent.Core/Execution/AgentRunner.cs)：理解循环、执行前检查和终止条件。
3. [AgentMessage](src/PortableAgent.Core/Models/AgentMessage.cs)：查看四种消息工厂和 JSON 副本处理。
4. [成功闭环及边界测试](tests/PortableAgent.Core.Tests/AgentRunnerTests.cs)：观察两次模型请求和一次工具调用如何被验证。
5. [脚本模型](src/PortableAgent.Infrastructure/Models/ScriptedModelProvider.cs)：查看它如何根据对话结构确定阶段。
6. [本地工具执行器](src/PortableAgent.Infrastructure/Tools/LocalToolExecutor.cs)：查看可信内部身份如何对应实际操作。

## 后续方向

以下是分阶段计划，不代表当前已经支持：

| 阶段 | 目标 |
| --- | --- |
| Phase 1 后续 | 在审阅当前闭环后，逐步推进最小本地 Runtime |
| Phase 2 | 复核契约，用两个本地微型业务验证领域迁移 |
| Phase 3 | 结构化执行事件与 Console Trace |
| Phase 4 | 策略、审批，以及已持久化待审批 Run 的重启恢复 |
| Phase 5 | MCP 适配器与协议迁移验证 |
| Phase 6 | ASP.NET Core API、HTTP 命令与 SSE 订阅 |
| Phase 7 | Developer Studio：Chat 与内联执行进度 |

早期不引入多 Agent、RAG、向量数据库、分布式执行、动态插件加载或工作流图引擎。Graph 将来只展示执行结构，不决定 Runtime 如何运行。
