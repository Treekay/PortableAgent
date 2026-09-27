# PortableAgent

使用 C# / .NET 构建的可迁移、业务无关的 Agent Runtime 与适配框架。

业务应用提供工具、策略和可信配置，同一个 Runtime 负责模型循环、校验、工具调度及审批恢复。本项目也用于逐步学习 C#、异步编程、接口设计、测试和 AI 应用开发。

## 当前状态

**Phase 0–3、Phase 4A（策略与内存审批）、Phase 4B（SQLite 持久化）、Phase 5A（最小 MCP 适配）、Phase 5B（领域 MCP 与远程审批）、Phase 5C（工具失败恢复与远端结果语义）、Phase 6A（HTTP 命令与查询 API）、Phase 6B（持久化回放与实时 SSE）、Phase 7A（Developer Studio Chat）、Phase 7B（Trace / Graph / Tools 检查）已完成。**

当前模型都是确定性脚本，只识别约定演示输入，无需 API Key。宠物寄养与航班预订复用原有领域模型、同一个 Core 和同一种 MCP adapter，连接两个独立业务服务器。现已提供 ASP.NET Core 命令、查询及执行事件 SSE，以及 React Studio 的独立任务、内联进度与审批界面；真实模型尚未实现。

已支持：

- 中立的 `IModelProvider`、`IToolProvider`、`IToolExecutor`，以及结构化消息、工具调用和工具结果。
- 可信 `ToolId` 与模型可见 `ModelName` 映射、唯一 CallId、结果关联校验、整批预算检查和顺序执行。
- 默认最多 4 次模型调用和 4 次工具调用；预算由 Runner 构造提供，恢复沿用保存的预算。
- Allow / Deny / RequireApproval，以及单调用人工审批。
- `Completed`、`Failed`、`Cancelled`、`LimitReached` 四种终止状态与非终止 `AwaitingApproval`。
- 17 种结构化执行事件、可选 live sink、SQLite 事件历史及有界分页读取。
- JSON 状态快照、版本 CAS、关键状态与事件的原子保存。
- 单来源 Streamable HTTP MCP 工具适配；Core 保持协议无关，Persistence 无 MCP 专用结构。
- 正常工具失败进入模型会话，模型可以解释或显式提出新的尝试；Runtime 不自动重试。
- **AwaitingApproval 成功保存 → 进程退出 → 新进程批准/拒绝 → 恢复同一个 Run。**
- HTTP 列出 Agent、启动、查询、批准/拒绝与取消活动 Run；开始和审批均在持久化提交后返回 202，执行不依赖 HTTP 连接存续。
- SSE 从 SQLite 回放事件并跟随新进度，支持按 Sequence 重连；断开或慢订阅者溢出不会取消 Run。
- Studio 从 API 读取 Agent，展示分组执行进度、只读审批参数、取消请求与持久化最终回答；错误、传输状态和 Runtime 状态分开管理。
- Studio 的 Trace、实际执行 Graph 和持久化工具目录共享 Run 选择与事件解释；切换页面保持 Chat、审批和原有 SSE 连接。

| 领域 | 模型可见工具 | 可信 ToolId |
| --- | --- | --- |
| Pet Boarding | `get_care_records` | `pet-local / care.get_records` |
| Pet Boarding | `create_staff_task` | `pet-local / staff.create_task` |
| Flight Booking | `get_booking` | `flight-local / booking.get` |
| Flight Booking | `cancel_booking` | `flight-local / booking.cancel` |

本地工具仍返回固定模拟结果。Phase 5B 的独立 MCP sample 会实际修改其进程内订单和任务状态，不连接真实航空公司或寄养业务。SQLite 保存的是 Runtime 状态和事件，不是业务系统数据库。`my booking` 固定对应 NZ123，不代表用户身份或资源授权。

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

## 启动 Developer Studio（Phase 7A / 7B）

需要 .NET 10 SDK 和 Node.js（已验证 **22.22.1 / npm 10.9.4**）。先在根目录执行 `dotnet build PortableAgent.sln`，再分别打开终端，从根目录启动：

```sh
# 终端 1：Pet MCP，localhost:5102
dotnet run --project samples/PortableAgent.Sample.PetBoardingMcpServer

# 终端 2：Flight MCP，localhost:5103
dotnet run --project samples/PortableAgent.Sample.FlightBookingMcpServer

# 终端 3：API，localhost:5100；自动初始化当前目录中的 SQLite
dotnet run --project src/PortableAgent.Api

# 终端 4：Studio
cd src/PortableAgent.Studio
npm install
npm run dev
```

打开 [http://localhost:5173](http://localhost:5173)。浏览器 POST、GET 和原生 EventSource 均使用相对 `/api/...`，由 Vite 直接代理到 5100；无需后端 CORS。5173 被占用时直接报错，不自动换端口。

- **每条消息是独立 Run**，不会把前面的消息传给下一次请求；没有 Session。一个非终态 Run 期间锁定发送和 Agent 切换，但可以编辑下一条草稿。
- **刷新页面会清空当前 Studio 历史**，不使用 localStorage，不自动找回 Run。关闭页面只停止观察，不取消后台执行。
- Flight 选择 `Cancel my booking.`：核对 `flight-mcp / cancel_booking` 和只读 `{"bookingId":"NZ123"}` 后批准或拒绝；202 只表示命令已接收，状态继续由 SSE/GET 确认。
- Pet 可试 `Has Cooper eaten today?` 和 `Ask the staff to give Cooper some fresh water.`；后一条展示策略拒绝，不出现审批按钮。
- `Cancel run` 仅在 Running 且最新可用快照确认本宿主仍在执行时显示。202 显示 Cancelling，收到终态后才显示 Cancelled；不代表业务回滚。
- 普通 SSE 断连交给 EventSource 原生重连；解析错误关闭连接并查询状态，可显式从最后一个精确 Sequence 重连。每 12 秒的非重叠 GET 仅做安全同步，不替代执行事件。
- `/api/agents` 加载失败保留页面并显示 API unavailable 和 Retry。启动 POST 结果未知时不自动重发，保留消息与重复执行风险提示，需要显式释放当前任务后才可再次发送。
- Completed 的最终回答来自 GET Run，以普通文本显示在执行卡片下面，成功读取后只自动折叠一次。无模型 token 流或隐藏推理展示；内联 Show details 继续保留。

前端验证：

```sh
cd src/PortableAgent.Studio
npm run build
npm test
npx playwright install chromium
npm run test:e2e
```

浏览器测试运行前先停止手动启动的四个开发服务，且在仓库根目录完成 Debug `dotnet build`。Playwright 自动启动真实 Pet/Flight MCP、API、Vite，使用独立 `TestResults/phase7a-*.db`，结束后停止测试服务；不复用已有进程。测试输出与截图在 Studio 的 `test-results/`，不纳入 Git。

[Phase 7A 验证记录](docs/phase7a-verification.md) 包含依赖精确版本、状态与竞态设计、49 项前端测试、2 项浏览器验收及 SSE 增量证据。

## Phase 7B：检查实际执行

- 桌面左侧及窄屏紧凑导航均提供 **Chat / Trace / Graph / Tools**。采用内部视图状态，不使用 Router；Chat 与 RunController 持续挂载，保留草稿、阅读位置、卡片和 details 展开及审批状态。
- 三个检查视图共用已绑定 RunId 的 Run 选择器，以 clientId 选择。首个 Run 默认选中，此后新 Run 不抢走当前检查目标。无 Run 时显示 Go to Chat；刷新仍清空 Studio 历史。
- **Trace** 每行对应一条收到的 ExecutionEvent，按精确 Sequence 升序，允许空洞与去重。提供 All / Lifecycle / Model / Tools / Policy / Approval，键盘展开原始事件；未知事件仍在 All 中。用户阅读旧事件时不强制滚到底部。GET 已终止而终止事件未收到时分开显示，不补造事件。
- **Graph** 使用固定版本 `@xyflow/react` **12.12.0**，展示 Run Start、Tool Discovery、Model Turn、Tool Operation、Approval、Run Resume、Terminal 七种实际执行节点。实线代表观察支持的关系，虚线仅代表执行观察顺序，不代表数据依赖；批次工具是兄弟节点。支持平移、缩放、选择和 Fit graph，不可编辑。
- Flight 审批前后同一个 CallId 显示为两个操作阶段，只有一次模型提案。重复 CallId 的提案保留独立观察并标记歧义；缺少执行证据时使用保守状态，不猜测跳过原因或隐藏推理。节点详情可追溯到原事件，实时更新不自动重置用户视角。
- **Tools** 通过唯一新增的只读 `GET /api/runs/{runId}/tools` 读取持久化 RunState.ToolCatalog，展示可信 ToolId、ModelName、说明和可展开/复制的 Schema。目录读取不联系 MCP、不做 tools/list，源服务离线仍可检查已保存目录。
- 目录状态与 snapshotSequence 和当前观察状态分开。Running 空目录表示“尚未持久化可用”，非空目录可能来自较早检查点；终态空目录只说明该快照未保存目录。恢复后目录可能被重新发现的结果替换，不提供历史 Schema 版本。
- 工具使用记录来自共享事件模型，标记 **Observed policy in this Run**，保留多次策略和审批观察，分别统计执行开始、完成和返回失败。优先按事件中的可信身份关联，其次唯一名称匹配；歧义留作未关联观察。Tools 只有检查与刷新，**没有手动执行工具或编辑参数入口**。

[Phase 7B 验证记录](docs/phase7b-verification.md) 包含 API 契约、边界检查、93 项前端测试、276 项后端测试与真实 Flight / Pet 跨视图验收证据。

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
  PortableAgent.Adapters.Mcp/
    McpSourceDefinition.cs  可信来源与完整 Endpoint
    McpToolAdapter.cs       同时实现 provider/executor，管理单客户端
    McpToolMapper.cs        发现、参数和结果映射
    McpResultJson.cs        显式 structuredContent null 的 SDK JSON 转换
  PortableAgent.Console/   原有命令 + mcp-pet-* / mcp-flight-*
    McpDomainDemo.cs       两个独立 MCP 来源的可信装配
  PortableAgent.Api/       ASP.NET Core 本地命令与查询宿主
    Contracts/            专用公开请求与响应 DTO
    Endpoints/            Agent、Run、审批、取消和 ProblemDetails
    Hosting/              Registry、Coordinator、提交确认信号和 Store 装饰器
    Streaming/            通知 Hub、有界独立订阅、SQLite 补读与 SSE DTO
  PortableAgent.Studio/    React + TypeScript + Vite，独立前端构建
    src/api/              HTTP DTO、ProblemDetails、原生 EventSource
    src/features/agents/  API Agent 列表与选择器
    src/features/run/     UI 状态、纯 reducer、控制器、分组投影、审批与消息
    src/features/inspection/  共享事件解释与检查视图协调
    src/features/trace/   精确时间线、分类与原始事件
    src/features/graph/   只读实际执行图、纯投影与布局
    src/features/tools/   独立目录查询缓存、Schema 与观察使用记录
    src/components/       页面外壳和连接状态
    src/styles/           CSS 变量、布局、窄屏和 reduced-motion
    src/test/             Vitest / RTL 辅助
    e2e/                  真实 Flight / Pet Playwright 验收
samples/
  PortableAgent.Sample.McpServer/  独立 ASP.NET Core MCP 服务，仅一个工具
  PortableAgent.Sample.PetBoardingMcpServer/  护理记录、进程内员工任务
  PortableAgent.Sample.FlightBookingMcpServer/  进程内订单与取消操作
tests/
  PortableAgent.Core.Tests/
  PortableAgent.IntegrationTests/
  PortableAgent.Persistence.Tests/
  PortableAgent.Adapters.Mcp.Tests/
  PortableAgent.Api.Tests/  真实 Kestrel、独立 SQLite、Flight MCP 端到端
dotnet-tools.json          固定版本的本地 dotnet-ef 工具
```

```text
Console → Core + Infrastructure + Persistence + Adapters.Mcp
Api → Core + Infrastructure + Persistence + Adapters.Mcp
Studio → 同源 /api HTTP + SSE（不引用后端项目，不加入 .sln）
Infrastructure → Core
Persistence → Core
Adapters.Mcp → Core + 官方 ModelContextProtocol.Core
三个 Sample MCP Server → 官方 ModelContextProtocol.AspNetCore（不引用任何 PortableAgent 运行时项目）
Core.Tests → Core
IntegrationTests → Core + Infrastructure
Persistence.Tests → Core + Infrastructure + Persistence
Adapters.Mcp.Tests → Adapters.Mcp + Infrastructure + Persistence + 三个 Sample MCP Server
Api.Tests → Api + Flight Sample MCP Server
```

Core 不引用 EF Core、SQLite、模型 SDK、MCP、ASP.NET Core 或业务类型。DI 只用于 API / sample 宿主边界；Core 继续使用直接构造函数，不引入容器、通用 repository 或 UnitOfWork。

## Phase 6A：HTTP 命令与查询 API

从仓库根目录，在两个独立终端启动 Flight MCP 与 API：

```sh
dotnet run --project samples/PortableAgent.Sample.FlightBookingMcpServer
dotnet run --project src/PortableAgent.Api -- --DatabasePath portable-agent.db
```

API 默认监听 `http://localhost:5100`，启动时应用既有 SQLite migration，保留原数据。`DatabasePath` 可为绝对路径，默认相对启动工作目录。端口可用 `--urls http://localhost:5200` 配置。可信宿主配置 `Agents:pet:Endpoint` / `Agents:flight:Endpoint` 默认指向 `localhost:5102/mcp` / `localhost:5103/mcp`，HTTP 客户端不能覆盖它们；实质更换业务绑定时需要维护对应 RuntimeDefinitionId。

Pet 演示需另启 `dotnet run --project samples/PortableAgent.Sample.PetBoardingMcpServer`。API 注册的 `pet`、`flight` 均使用 MCP adapter，无本地业务执行器；工具发现按需连接。

| 请求 | 成功响应 | 含义 |
| --- | --- | --- |
| `GET /api/agents` | 200 | 仅公开 Agent id、name |
| `POST /api/runs` | 202 + Location | Run 与 RunStarted 已提交 |
| `GET /api/runs/{runId}` | 200 | 持久化状态 DTO，附当前宿主 isActive |
| `GET /api/runs/{runId}/tools` | 200 | 持久化目录 DTO：runId、status、snapshotSequence、tools；不重新发现工具 |
| `POST /api/runs/{runId}/approvals/{approvalId}` | 202 + Location | 批准或拒绝的 CAS 已提交；执行可以尚未完成 |
| `POST /api/runs/{runId}/cancel` | 202 + Location | 已向当前宿主活动执行发出取消信号 |

PowerShell 演示（先批准一次，Flight sample 重启后可重新演示）：

```powershell
$base = 'http://localhost:5100'
Invoke-RestMethod "$base/api/agents"
$accepted = Invoke-RestMethod "$base/api/runs" -Method Post -ContentType 'application/json' `
    -Body '{"agentId":"flight","message":"Cancel my booking."}'
$run = Invoke-RestMethod ($base + $accepted.runUrl)
# 若仍为 Running，稍后再次 GET，直至 AwaitingApproval。
$run.pendingApproval
Invoke-RestMethod "$base/api/runs/$($run.runId)/approvals/$($run.pendingApproval.approvalId)" `
    -Method Post -ContentType 'application/json' -Body '{"decision":"approve"}'
Invoke-RestMethod ($base + $accepted.runUrl)
# 拒绝使用 {"decision":"reject"}。取消当前活动 Run：
# Invoke-RestMethod "$base/api/runs/<runId>/cancel" -Method Post
```

开始请求仅接受 `agentId`、`message`，消息限 1–8000 字符且不能全为空白。未知 JSON 属性（包括 runId、runtimeDefinitionId、endpoint）返回 400；审批只接受字符串 `approve` / `reject`，不接受数值枚举或 AgentId。RunId 由可信宿主生成。

开始返回 `{runId,status:"accepted",runUrl,eventsUrl}`；审批额外返回 `approvalId`。`eventsUrl` 自 Phase 6B 起提供，指向该 Run 的 SSE 端点。`accepted` 是命令状态，快速执行可能在响应送达前已经 Completed。开始必须等 Store 成功创建，审批必须等 Store 成功 CAS 且同批包含 ApprovalResolved、RunResumed；信号来自 API Store 装饰器，独立于 live event sink。提交失败不会伪造 202。

错误使用 `application/problem+json`，包含稳定 `code`：400 `invalid_request`；404 `agent_not_found` / `run_not_found`；409 `approval_conflict` / `run_not_active` / `runtime_unavailable`；503 `host_stopping`；500 `internal_error`。不返回异常原文或堆栈。

查询返回 runId、agentId（无兼容注册时为 null）、status、modelTurns、toolCalls、snapshotSequence、isActive、finalText、failure、pendingApproval。`status` 使用 Core 生命周期名称；`snapshotSequence` 是 **RunState.LastSequence，不是最新事件游标**。Running 时计数和快照可能落后于已经落库的事件。仅 Completed 从持久化最后一条 Assistant 消息提取 finalText；Failed 只公开 `{code:"run_failed",message:"Run execution did not complete."}`，详细失败原因不能持久化查询。AwaitingApproval 返回审批 ID、工具身份、精确冻结参数、理由和时间。

**HTTP 请求生命周期不等于 Run 生命周期。** Coordinator 持有每次执行的 Task 和独立 CTS，断开开始或审批请求只终止 HTTP 等待。每条消息创建独立 Run，无 Session 记忆。每个 Agent 的 Runner、脚本、策略与 MCP adapter 长期复用，adapter 当前按 Agent 串行化 MCP 操作；不同 Run 的状态和取消源独立。

暂停或终止后移除活动记录；审批按已保存 RuntimeDefinitionId 匹配注册并创建新的执行 CTS，SQLite CAS 仍是最终审批所有权判定。关闭宿主停止接收执行、取消活动 CTS，并在宿主关闭期限内等待任务，随后由 Registry 释放 adapter。依赖必须配合取消，期限并不提供远端回滚保证。

API 重启后可查询历史终态与待审批，并显式恢复兼容的 AwaitingApproval。`isActive` 是当前宿主内存信息，可能出现 `Running + false`；不会扫描或自动恢复 Running。取消只针对当前宿主活动 Running，AwaitingApproval、终态及非活动 Running 均返回 409；取消与完成竞争时以 Runner 最终持久化结果为准。

API **仅供本地开发，无认证、资源授权或前端**。每个 Agent 共享 sample 业务身份，不可作为多租户服务开放。没有自动工具重试、Running 崩溃恢复或 exactly-once 保证。Phase 6A 的历史验证见 [Phase 6A 验证记录](docs/phase6a-verification.md)。

## Phase 6B：SQLite 回放与实时 SSE

新增 `GET /api/runs/{runId}/events`。成功流返回 `200 Content-Type: text/event-stream`，客户端只需既有 HTTP 命令/查询与此端点即可观察执行，不需要访问 C# 对象或 SQLite。

```sh
# 在开始 Run 后使用响应中的 eventsUrl。Windows PowerShell 使用 curl.exe。
curl -N "http://localhost:5100/api/runs/<runId>/events?afterSequence=0"

# 断开后从最后收到的完整事件继续；两种游标方式任选其一。
curl -N -H "Last-Event-ID: 9" "http://localhost:5100/api/runs/<runId>/events"
curl -N "http://localhost:5100/api/runs/<runId>/events?afterSequence=9"
```

每条事件的格式为以下三行加一个空行：

```text
id: 11
event: RunResumed
data: {"eventId":"802f18bc-9ffd-456f-89c9-ba0d4aba3092","runId":"ca8a26d5-9ee9-494a-9f15-5a8f71c6bc51","sequence":11,"occurredAt":"2026-09-27T08:09:39.9737837+00:00","eventType":"RunResumed","payload":{}}

```

非空 `Last-Event-ID` 优先，其次是 `afterSequence`，均未提供则从 0 开始。游标使用非负 Int64 十进制数字；非法、负数或溢出返回 400 `invalid_request`，非法 header 不回退到 query。只发送 `Sequence > cursor`，未来游标不重置。Sequence 允许空洞，不等待或伪造缺失的编号。

**RunEventHub 不持久化；SQLite ExecutionEvents 才是事件历史。** API 先确认 Run 存在，注册订阅，再按每页最多 256 条读取数据库。Hub 只发送按 RunId 路由的序号通知；唤醒后清空当前通知队列，再从最后成功写入并刷新的序号补读 SQLite。不会直接发送 Hub 中的内容，重复、延迟、乱序通知均不会产生重复或乱序输出。

每个订阅拥有独立的 256 条有界 Channel。Runner 发布通知只使用非阻塞 TryWrite，不等待网络、数据库或缓冲区容量。订阅溢出后从 Hub 移除、完成 Channel，并取消该订阅的读写令牌以中断阻塞写入；其他订阅与 Run 继续。客户端使用最后收到的 SSE id 重连，SQLite 补齐断开期间的事件。

无通知时约每 15 秒补读一次数据库，补偿 live sink 失败导致的通知遗漏。若没有新事件，发送注释 `: keepalive` 加空行；心跳没有 id，不持久化，也不推进游标。持续通知同样触发数据库补读。

| 状态或事件 | 连接行为 |
| --- | --- |
| 未知 Run | 开流前返回 404 `run_not_found` |
| 终态 Run 且 cursor ≥ snapshotSequence | 返回 204，不打开 SSE，避免 EventSource 无限重连 |
| 尚未消费的 RunCompleted / RunFailed / RunCancelled / RunLimitReached | 写入并刷新该事件后关闭 |
| ApprovalRequired | 保持连接；批准或拒绝后，同一连接继续收到 ApprovalResolved、RunResumed 等 |
| 连接断开、订阅溢出、DB/网络故障 | 清理该订阅；已开流后不插入 ProblemDetails；不取消 Run |
| 宿主关闭 | 终止所有订阅；Run 关闭仍由 Coordinator 负责 |

SSE 游标来自最后成功发送的 ExecutionEvent.Sequence，Running 的 snapshotSequence 可能滞后，不能用作最新事件游标。重启后非活动 Running 仍只回放现有事件并保持心跳，不伪造终态；未来 Studio 可查询 isActive=false 后停止跟随。

SSE 保留既有事件脱敏边界，不添加提示词、工具参数/输出、最终回答或异常原文。收到终态后通过 `GET /api/runs/{id}` 获取 finalText 与状态。HTTP/SSE 断开不取消运行；每条用户消息仍是独立 Run，无 Session。无 SignalR、WebSocket、单独 history/trace 端点或前端。

真实 Flight 的同一连接审批轨迹、慢连接恢复及丢通知测试见 [Phase 6B 验证记录](docs/phase6b-verification.md)。

## Phase 5A：MCP 协议适配

官方 `ModelContextProtocol.Core` 和 `ModelContextProtocol.AspNetCore` 固定为 **2.2.0**；后者传递引用 `ModelContextProtocol` 2.2.0。运行时侧 MCP 依赖只进入 Adapters.Mcp，示例服务器独立引用官方 ASP.NET Core 包。Core、AgentRunner、Core 工具契约和 Persistence 均零改动。

先完成构建及数据库初始化，再在两个终端运行：

```sh
# 终端 A：独立服务器，固定监听 http://localhost:5101/mcp
dotnet run --project samples/PortableAgent.Sample.McpServer

# 终端 B：仓库根目录，沿用 portable-agent.db
dotnet run --project src/PortableAgent.Console -- init
dotnet run --project src/PortableAgent.Console -- mcp-status
```

`mcp-status` 假定服务器已启动，不负责拉起或重启服务。它使用 `sample-mcp-demo-v1`、协议无关的 ServerStatusScriptedModelProvider、同一个 McpToolAdapter 作为 provider/executor、既有 Console sink 和 SQLite Store。正常结果为：

```text
[9] Tool execution started · get_server_status
[10] Tool execution completed · get_server_status · success=True
[11] Model turn 2 started
[12] Model turn 2 completed · Completed
[13] Run completed · 2 model turns · 1 tool call(s)
RunId: <run-id>
Status: Completed
The sample service is online.
```

[Phase 5A 验证记录](docs/phase5a-verification.md) 包含实际双进程输出、数据库读取结果、测试与 SDK 兼容处理。

### 可信来源与发现

配置类型为 `McpSourceDefinition(string SourceId, Uri Endpoint)`。SourceId 非空白，Endpoint 必须是绝对 HTTP/HTTPS 地址，包含完整路由；不猜测 `/mcp`。Console 固定 `sample-mcp` 与 `http://localhost:5101/mcp`，不接受模型、参数或提示中的地址。HTTP 自动重定向关闭。

客户端显式使用 StreamableHttp，关闭 standalone GET stream 及流重连尝试；服务器显式使用 `HttpServerSessionMode.Stateless`，不启用 legacy SSE。MCP 传输与未来 Studio 执行事件 SSE 是不同边界。

每次 GetToolsAsync 都通过官方 SDK 重新读取完整分页工具目录。SourceId 来自配置，ToolId.Name 和初始 ModelName 来自远程名称，缺省 Description 为空串，InputSchema 克隆保存。重复 ToolId / ModelName 使用 ordinal 比较并明确失败，不改名、不覆盖。发现失败不退回旧目录。

适配器保留最近成功发现的目录供防御性一致性检查，**它不替代 Runner 的可信注册定义**。执行前检查完成发现、SourceId、ToolId、ModelName、InputSchema 及 Call.ToolName；实际远程工具名始终使用 `registeredTool.Id.Name`。

### 参数和结果

Arguments 必须是 JSON object；包括嵌套对象在内的重复属性名均拒绝。属性值按 JsonElement 克隆，保持对象、数组、数字、字符串、布尔与 null；空参数发送 `{}`。Core CallId 保持本地关联，不用作 JSON-RPC ID 或远程幂等键。

StructuredContent 存在时原样克隆，支持 **object / array / string / number / boolean / null**，不人为包装原始值，也不进行 outputSchema 校验。缺省时将文本块按顺序映射为 `{"content":[{"type":"text","text":"..."}]}`；无内容返回 `{"content":[]}`。文本即使看起来像 JSON 也不重新解析。存在任何非文本富内容块就明确失败，包括同时具有 StructuredContent 的响应。

SDK 2.2.0 的便捷 CallToolAsync 将显式 JSON null 与 StructuredContent 缺省合并。本实现使用同一官方 McpClient 的类型化 `SendRequestAsync<CallToolRequestParams, CallToolResult>`，请求方法为 SDK 的 `RequestMethods.ToolsCall`，在 SDK 默认 JSON 配置上加入一个 nullable JsonElement 转换器以保留显式 null。协议封装、请求 ID、HTTP、错误和取消仍全部由 SDK 负责；没有手写 JSON-RPC、协议解析器或 Core 改动。真实 HTTP 测试覆盖该差异。

IsError 为 true 且内容可映射时，返回 Executed + IsSuccess=false，Error 优先使用文本说明。从 Phase 5C 起，Runner 校验结果契约后发布 ToolExecutionCompleted(success=false)，把结果交回模型。HTTP、协议、解析失败沿异常路径产生 Failed，不伪造 ToolResult；调用者取消沿既有路径产生 Cancelled。不自动重试工具。

### 生命周期与限制

构造仅校验配置，首次发现时惰性创建 transport/client，后续发现和执行复用它们。一个 SemaphoreSlim 串行协调初始化、发现、执行和释放；等待支持调用者取消。DisposeAsync 等待正在进行的操作，释放 client，并在 finally 中释放持有 HttpClient 的 transport；之后调用明确拒绝，不重新连接。宿主应先取消/等待活动 Run 再释放适配器，没有额外超时或连接池框架。

初始化失败清理已创建资源并让当前操作失败；后续显式的新操作可以重新初始化，但当前操作不循环重试。已连接客户端故障也不触发自动重连。

RunState 仍只保存 Core 数据，不保存客户端、连接、会话 ID 或 MCP 对象；通用 Runtime 事件与 SQLite schema 不变。MCP 审批恢复边界可沿用既有机制，但 Phase 5A 未新增写操作或审批演示。

远程请求发出后的网络故障不证明业务副作用未发生，取消也不代表回滚。Phase 5C 只实现正常工具错误回到模型并验证远端结果不确定性；重连/超时、多来源、别名、认证、富内容和更高并发仍留待以后，不阻塞 API/Studio MVP。

## Phase 5B：领域 MCP 与远程审批

两个 sample 使用官方 MCP SDK 2.2.0、Streamable HTTP 和 `HttpServerSessionMode.Stateless`。协议会话无状态，业务应用仍各自持有进程内单例状态；服务器重启会重置业务数据。Phase 5B 实施时，Core、Persistence、Adapters.Mcp 以及两个领域 scripted model 相对 Phase 5A 均零修改；后续 Phase 5C 仅调整通用 Core 失败处理语义。

| 业务 | 默认端点 | SourceId | RuntimeDefinitionId |
| --- | --- | --- | --- |
| Pet | `http://localhost:5102/mcp` | `pet-mcp` | `pet-mcp-demo-v1` |
| Flight | `http://localhost:5103/mcp` | `flight-mcp` | `flight-mcp-demo-v1` |

每套 Runtime 只连接一个来源，两套装配共用同一种 `McpToolAdapter`。Phase 5A 的 `localhost:5101/mcp` 保留。可信配置由宿主维护，RuntimeDefinitionId 不从 URL 推导；实质更换业务绑定应使用新的身份。

| 可信 ToolId | Runtime 策略 |
| --- | --- |
| `pet-mcp/get_care_records` | Allow |
| `pet-mcp/create_staff_task` | Deny |
| `flight-mcp/get_booking` | Allow |
| `flight-mcp/cancel_booking` | RequireApproval，`confirm-flight-cancellation-v1` |

在独立终端启动所需服务器：

```sh
dotnet run --project samples/PortableAgent.Sample.PetBoardingMcpServer
dotnet run --project samples/PortableAgent.Sample.FlightBookingMcpServer
```

另一个终端从仓库根目录运行：

```sh
dotnet run --project src/PortableAgent.Console -- init
dotnet run --project src/PortableAgent.Console -- mcp-pet-read
dotnet run --project src/PortableAgent.Console -- mcp-pet-denied-task
dotnet run --project src/PortableAgent.Console -- mcp-flight-read
dotnet run --project src/PortableAgent.Console -- mcp-flight-pause
```

`pause` 输出 RunId、ApprovalId 后自然退出，服务器保持运行。用另一个 Console 进程提交原来的两个 ID：

```sh
dotnet run --project src/PortableAgent.Console -- mcp-flight-approve <runId> <approvalId>
```

新 Runtime 从 SQLite 加载冻结调用，CAS 取得审批所有权，重新远程发现工具、比较契约、评估当前策略，然后调用远端 `cancel_booking`，最后才让模型继续。相同 RunId 的事件从第 9 条 ApprovalRequired 延续到第 20 条 RunCompleted。

也可以对尚未解决的审批执行：

```sh
dotnet run --project src/PortableAgent.Console -- mcp-flight-reject <runId> <approvalId>
```

拒绝会重新发现工具，但不会调用远端取消操作。建议先演示一次拒绝，再创建新的暂停 Run 演示批准，使拒绝检查使用尚未取消的订单。

Flight 服务器维护 NZ123 的真实状态。重复取消返回已有的 cancelled，`CancelCallCount` 增加、`CancelMutationCount` 不增加。这只是业务幂等示例，不是 Runtime exactly-once 保证。服务器不接收审批 ID、RunId 或 Runtime 策略。

现有 Flight 查询脚本只接受 confirmed，因此请在取消前执行 `mcp-flight-read`；取消后的状态在测试中通过服务状态和直接 MCP 查询验证。Pet 模型也仍是固定样例：主路径拒绝写操作，独立业务测试验证真实创建任务。

完整的实际跨进程批准/拒绝记录、网络调用计数、业务变更计数和 SQLite 事件验证见 [Phase 5B 验证记录](docs/phase5b-verification.md)。

## Phase 5C：工具失败恢复与远端结果语义

`ToolResult` 字段不变。`Executed + IsSuccess=false` 是正常返回的工具结果，保留 CallId、Output、Error，以 Tool 消息交给模型。模型可解释失败后 Completed，也可显式提出新 CallId 的操作；Runtime 不解析错误文本、不生成重试调用。

| 情况 | Runtime 行为 |
| --- | --- |
| 正常返回 Executed / true | 追加结果，继续当前批次 |
| 正常返回 Executed / false | 追加结果，跳过批次剩余调用，预算允许时继续模型 |
| CallId 不匹配或 executor 返回非 Executed | Failed，不发布 ToolExecutionCompleted，不追加无效结果 |
| executor、HTTP、协议或映射异常 | Failed，不伪造 ToolResult，不再调用模型 |
| 调用方取消 | 保留 Cancelled 语义，不代表业务回滚 |

`ToolExecutionCompleted` 现在表示执行器返回了通过 CallId 与 Disposition 校验的结果，`success` 表示业务结果是否成功。正常业务失败会产生 `ToolExecutionCompleted(success=false)`，而不是立即产生 RunFailed；模型自身若不支持该失败结果并抛出异常，Run 仍可以 Failed。

全部 Allow 的批次按顺序执行。若 A 成功、B 业务失败、C 尚未执行，会按 A/B/C 顺序追加三个 Tool 消息；C 使用 `NotExecutedDueToPriorFailure`，保留原 CallId，`IsSuccess=false`、`Output=null`，无执行事件，不消耗工具执行次数。它与策略阻止整批执行的 `NotExecutedDueToBatchPolicy` 含义不同。已执行的 A 不会回滚。

整批工具、CallId、预算和策略预检查保持不变。所有原提案 CallId 都已占用，跳过的调用也不能复用旧 ID。模型新提案必须重新经过工具解析、预算、策略和审批；失败执行消耗一次 ToolCalls，每次模型调用消耗 ModelTurns，预算可终止反复尝试。

已批准的写操作返回业务失败后，原审批仍为已解决；模型若提出新写调用，即使 PolicyId 不变，也需要新的 ApprovalId。没有新增工具结果检查点，SQLite 保存时机与 Running 不可自动恢复的边界不变。

**Run Failed 不证明远端操作没有发生。** 真实 HTTP 测试中，服务器先取消订单再返回协议错误，Runtime Failed，但订单已变为 cancelled；请求只发送一次。没有新增 OutcomeUnknown 类型、自动重试或补偿。

本阶段仅修改两个 Core 生产文件，Persistence、MCP adapter、sample server、生产 scripted models 均未修改。实际测试与兼容说明见 [Phase 5C 验证记录](docs/phase5c-verification.md)。Phase 5A/5B 验证报告保留当时的历史语义。

## 策略和恢复边界

模型提出操作，Runtime 校验和调度工具；业务系统仍负责身份认证、资源权限、业务合法性和最终一致性。人工批准不能替代这些检查。工具由可信目录解析，模型不能指定任意服务器地址。输入 Schema 暂无通用校验器，各领域执行器自行检查参数。

无策略配置时采用 AllowAll；显式 `InMemoryPolicyEvaluator` 对未配置工具默认 Deny。Console 允许查询，取消要求确认。RequireApproval 必须配置 Store。

每个模型批次在任何工具执行前完成全部工具、预算和策略检查：

| 批次政策 | 行为 |
| --- | --- |
| 全部 Allow | 顺序执行，首次业务失败后跳过剩余调用 |
| 存在 Deny | 整批零执行，分别反馈 DeniedByPolicy / NotExecutedDueToBatchPolicy |
| 无 Deny，多调用且存在 RequireApproval | Failed，零执行；尚不支持多调用审批 |
| 单调用 RequireApproval | 冻结参数、原子保存，返回 AwaitingApproval |

`ToolResult.Disposition` 的值为 Executed、DeniedByPolicy、RejectedByUser、NotExecutedDueToBatchPolicy、NotExecutedDueToPriorFailure。有效的已执行失败与未执行结果均可交回模型；执行器异常仍终止运行。第二个工具失败不会回滚第一个工具。

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

`state` 实际保存全部 RunState 数据：可信配置身份、生命周期和 Version、对话、工具目录、待审批和已解决审批、模型/工具计数、原 RunLimits、UsedCallIds 和 LastSequence。显式 DTO 映射通过 AgentMessage 工厂重建消息，拥有独立 JSON/集合；UsedCallIds 恢复 ordinal 比较语义。缺失字段、畸形消息、未知枚举和未知 schemaVersion 明确失败。当前只支持 v1，没有快照升级机制。Phase 5C 能读取旧 v1 快照，但旧二进制不保证读取包含新增枚举值的新 v1 快照，项目不承诺降级兼容。此次枚举扩展不修改 schemaVersion；未来结构变化可另行升级。Task、模型实例、委托、执行器、sink、CancellationToken 均不序列化。

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

Core 仍顺序等待 sink，其他自定义慢 sink 可能阻塞执行。Phase 6B 的 API Hub 只做非阻塞通知广播，SSE 网络写入在订阅请求中进行；数据库回放与周期补读补偿通知遗漏。Hub 本身不提供持久化交付保证。

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

`dotnet build PortableAgent.sln`：0 警告、0 错误。`dotnet test PortableAgent.sln`：**276 通过，0 失败，0 跳过**。

Phase 7B 前端：`npm run build` 通过，`npm test` **93 通过**，`npm run test:e2e` **2 通过**（扩展原有真实浏览器流程）。原 Phase 7A 的 49 项前端与 268 项后端基线保留；新增 44 项前端与 8 项 API 测试。Core / Persistence / MCP adapter / Infrastructure 生产文件均为 0 变更；API 仅新增 DTO 文件并修改既有端点文件。

| 项目 | 用例数 | 重点 |
| --- | ---: | --- |
| Core.Tests | 41 | 执行循环、批次失败恢复、预算、策略重评估、取消、事件契约、可信预分配 RunId |
| IntegrationTests | 40 | 领域迁移、策略、批次、内存审批与并发 |
| Persistence.Tests | 49 | DTO、新 disposition 往返、独立 Store、重启批准/拒绝、SQLite CAS、事务故障 |
| Adapters.Mcp.Tests | 72 | 协议适配、领域迁移、审批恢复、真实工具失败恢复及远端副作用不确定性 |
| Api.Tests | 74 | 提交确认、独立执行、SSE 回放/连续审批/断连/溢出恢复、资源释放、真实 Flight MCP、只读与离线目录 |

Phase 6B 在 Phase 6A 的 234 项基线上新增 34 项 API 测试，原测试保持通过；只将旧测试中“无 SSE”的名称调整为未知 Run 的 404 行为。持久化与 API 测试使用独立临时文件数据库，不复用演示数据库。API 测试通过真实 Kestrel HTTP 连接与受控模型/工具验证异步边界，并用受控响应 Stream 确认溢出会中断阻塞写入；持久化事务故障测试通过 SQLite trigger 注入真实回滚。

建议依次阅读 [Console](src/PortableAgent.Console/Program.cs)、[AgentRunner](src/PortableAgent.Core/Execution/AgentRunner.cs)、[AgentMessage](src/PortableAgent.Core/Models/AgentMessage.cs)、[审批测试](tests/PortableAgent.IntegrationTests/ApprovalTests.cs)、[SqliteRunStateStore](src/PortableAgent.Persistence/Sqlite/SqliteRunStateStore.cs)、[快照映射](src/PortableAgent.Persistence/Sqlite/Serialization/RunStateSerializer.cs)、[重启测试](tests/PortableAgent.Persistence.Tests/RestartTests.cs) 和 [事务故障测试](tests/PortableAgent.Persistence.Tests/TransactionTests.cs)。

## 后续方向

| 阶段 | 目标 |
| --- | --- |
| 后续另行设计与批准 | 真实模型、Session / 历史恢复或配置能力；不属于已完成的 Phase 7B |
| 后续按需 | MCP 多来源、别名、认证、富内容、超时/连接生命周期及并发增强 |

Phase 5A 已实现可信单来源绑定、名称一致性和参数对象检查；通用 Schema 校验、凭据/资源授权与远程副作用恢复仍待后续设计。RuntimeDefinitionId 由配置方维护。未实现多 Agent、RAG、向量数据库、分布式执行、动态插件或工作流图引擎。
