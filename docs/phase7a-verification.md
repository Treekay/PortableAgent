# Phase 7A 验证记录

日期：2026-09-27（Pacific/Auckland）。冻结基线：Phase 6B `795ff40e0584fe81ac5f4d363507e6630159e3d2`。

本阶段仅增加单页 Developer Studio Chat。Phase 7B 未开始。启动步骤见 [README](../README.md#phase-7a启动-developer-studio)。

## 边界与依赖

| 受保护生产项目 | 变更文件数 |
| --- | ---: |
| PortableAgent.Core | 0 |
| PortableAgent.Persistence | 0 |
| PortableAgent.Adapters.Mcp | 0 |
| PortableAgent.Api（含契约） | 0 |

其余 C# 项目、sample、后端测试和 solution 均未修改。生产新增全部位于 `src/PortableAgent.Studio`；根目录只更新 README、忽略规则及本验证文档。不添加 CORS、中间层、持久化表或后端端点。

已验证 Node.js 22.22.1、npm 10.9.4、.NET SDK 10.0.400。直接依赖全部固定精确版本，提交 npm lockfile：

| 依赖 | 版本 |
| --- | --- |
| react / react-dom | 19.3.0 / 19.3.0 |
| lucide-react | 1.48.0 |
| typescript | 7.0.2 |
| vite / @vitejs/plugin-react | 8.3.1 / 6.1.1 |
| vitest / jsdom | 5.0.2 / 27.4.0 |
| @testing-library/react | 16.3.3 |
| @testing-library/user-event | 14.6.7 |
| @testing-library/jest-dom | 7.0.1 |
| @playwright/test | 1.63.0 |
| @types/react / @types/react-dom | 19.3.0 / 19.3.0 |
| @types/node | 22.20.4 |

jsdom 使用兼容当前 Node 的稳定 27.4.0；其更新版本要求更高的 Node 补丁版本。安装完成无 engine 警告，npm audit 报告 0 漏洞。无 Redux、Router、Next.js 或大型 UI 框架。

## 文件职责

```text
PortableAgent.Studio/
  package.json / package-lock.json / index.html
  vite.config.ts / tsconfig.json / playwright.config.ts
  src/
    main.tsx / App.tsx
    api/
      contracts.ts                当前 API/SSE 类型与全部 17 个事件名
      portableAgentApi.ts          唯一 fetch 边界、命令 202 校验、ProblemDetails
      eventStream.ts              原生 EventSource、身份/游标校验、连接 generation
    features/agents/
      useAgents.ts / AgentSelector.tsx
    features/run/
      runTypes.ts / runReducer.ts  前端自有 StudioRun 与纯状态转换
      runController.ts            命令锁、SSE/GET 协调、单飞查询和安全同步
      useRunExecution.ts          React 订阅及 StrictMode effect 生命周期
      eventPresentation.ts        纯分组投影，不改变原始事件
      demoPrompts.ts              隔离的领域演示提示
      RunConversation / RunBlock / UserMessage / AssistantMessage
      ExecutionCard / ExecutionStep / ApprovalCard / Composer
    components/StudioShell.tsx / ConnectionStatus.tsx
    styles/tokens.css / studio.css
    test/setup.ts / fixtures.ts
    *.test.ts(x)                  6 个测试文件，与被测模块相邻
  e2e/flight-approval.spec.ts      2 个真实浏览器用例
```

## 页面与状态

只有 `/`，166px Chat 侧栏、紧凑 Agent 顶栏、约 800px 内容区和底部 composer。暗色中性背景、低装饰；桌面 1440×1000 和窄屏 390×844 均已截图检查。窄屏隐藏侧栏，审批参数可横向滚动，批准/拒绝在内容视口内，composer 不遮挡按钮。普通内容使用系统字体；JSON、可信工具身份、ID 使用等宽字体。

每个 RunBlock 保留启动时的 Agent 名称和用户原文，结构为用户消息 → 执行卡片 → 独立最终回答。每次 POST 仅发送 `{agentId,message}`。没有 Session 或隐式消息拼接，没有 localStorage；刷新清空屏幕历史，后台 Run 不因此取消。一个非终态受控 Run 期间锁定 Send 和 Agent，允许编辑下一条草稿。

StudioRun 保存 clientId、绑定后的三个服务端标识/URL、Agent 和消息、三类命令状态、Runtime 状态、快照计数、精确事件 map/排序 ID/游标、streamState、pendingApproval、GET 状态/generation、最终文本/失败、展开与一次折叠标记。`stateSequenceId` 表示最新生命周期证据，与最新收到的普通执行事件游标分开。

Reducer 无 I/O。`useRunExecution` 用 `useSyncExternalStore` 订阅可单独测试的 RunController，以 effect 管理 activate/suspend；控制器集中编排副作用。组件不 fetch、不 POST、不创建 EventSource。

## POST、SSE 与 GET

1. 同步检查输入与受控 Run，创建 clientId 和 Starting 卡片，再开始 POST；双击不会创建第二次请求。
2. 合法 202 绑定 runId/runUrl/eventsUrl，立刻开 SSE，再启动初始 GET。只有原草稿未被用户编辑时才清空，避免覆盖下一条草稿。
3. ApprovalRequired、ApprovalResolved、RunResumed、终态及流错误触发 GET；非终态每 12 秒安全同步。每个 Run 同时最多一个 GET，重复请求设置 refreshNeeded，结束后合并再查询一次。
4. 查询响应绑定 clientId/runId/request generation，cleanup abort GET 并更新 epoch。旧快照不能覆盖更新生命周期、回退终态、恢复已解决审批或覆盖新的 ApprovalId。
5. POST 网络失败/无效确认/无法确认的服务端异常展示 “Unable to confirm whether this run started.”，保留原消息，绝不自动 POST。用户阅读重复风险后显式释放任务，才能再次发送。

17 个命名事件统一 addEventListener，额外接收默认 message 以保留可观察的未来未知 envelope。原生 EventSource 没有未知命名事件通配符，因此未注册的未来 named event 无法捕获；升级契约时需同步扩展名单。未知已接收类型保留原始详情，不解释为生命周期。

`MessageEvent.lastEventId` 是唯一规范去重/重连身份，保留原十进制字符串，校验非负 Int64 范围，BigInt 排序。JSON sequence 只用于一致性校验，安全整数时精确比较；允许空洞、保留乱序唯一事件，重复 ID 不再转移状态。

普通中断使用 EventSource 自带重连与 Last-Event-ID，没有自定义重连计时器。解析/身份错误关闭流并保留历史，查询持久化状态；显式重建先关闭旧源，再用 `afterSequence=<lastSequenceId>` 开流。连接 generation 屏蔽旧 callback。StrictMode cleanup 关闭源、停止计时器、abort GET，不发取消。

初始 GET 可能先确认终态，而 SSE 仍在回放历史；此时允许历史回放至终止事件，避免提前关闭而丢失时间线。原生源已失效且 GET 确认终态时可关闭观察。

## 分组、审批、取消、终态

工具发现和模型轮次按生命周期对合并；工具提案、策略、审批、执行使用 callId 关联，RunResumed 后采用新的阶段编号，保留首次策略结论。等待审批、Deny、工具失败突出显示。工具 success=false 只改变操作展示，整体是否失败由 Runtime 终态决定。术语只用 Model turn / Model response，无隐藏推理或 token 流。

原始详情显示 Sequence、时间、类型、payload；Run UUID 缩写并可复制。没有 Trace 筛选、搜索或 Graph。

ApprovalRequired 先显示加载，只有 GET 确认 AwaitingApproval + pendingApproval 才呈现按钮。展示 toolName、可信 toolId、原因、创建时间和不可编辑的冻结 JSON。批准/拒绝同步锁住双按钮，202 仅标记命令 accepted，不合成 ApprovalResolved。409/网络不确定先 GET；同步失败时只允许再次查询，成功确认相同审批仍待处理后才允许用户显式重试。命令响应绑定确切 ApprovalId，旧响应不能干扰第二次审批。

取消只在 Running + 持久化 isActive=true 且快照不早于最新生命周期证据时显示。202 显示 Cancelling，等待 SSE/GET 确认 Cancelled；409 刷新，错误不会变成 Run Failed。取消不承诺远端回滚。

RunCompleted 触发 GET；只有 Completed + finalText 才呈现独立 Assistant 消息，并按持久化 modelTurns/toolCalls 生成摘要，自动折叠一次。用户再次展开后不再折叠。最终 GET 失败保留时间线和 Retry。Failed、Cancelled、LimitReached 默认保持展开，各自使用通用失败、取消、预算限制文案。

Agent 列表加载失败保留 shell、明确 localhost:5100 和 Retry；成功仅表示 Agent list loaded，不推断全系统在线。SSE reconnecting 是观察状态，不改变 Run 状态。启动/审批/取消命令、查询、协议、API 可达性、Run Failed 分开表示。

textarea 最大 8000，Enter 提交、Shift+Enter 换行，原生 isComposing、composition 生命周期和 229 键码保护输入法。native button/select、标签、焦点、aria-expanded、重要状态 aria-live 和 reduced-motion 已实现。仅在读者靠近底部时自动滚动，否则提供 New progress 按钮。

## 自动验证

| 命令 | 实际结果 |
| --- | --- |
| npm install | 成功，锁定依赖，无 engine 警告 |
| npm run build | TypeScript + Vite 成功；JS 251.74 kB / gzip 78.85 kB |
| npm test | **49 通过，0 失败**，6 个文件 |
| npm run test:e2e | **2 通过，0 失败**，Chromium 153.0.8010.12 |
| dotnet build PortableAgent.sln | **0 警告、0 错误** |
| dotnet test PortableAgent.sln | **268 通过，0 失败、0 跳过** |

后端分类：Core 41、Integration 40、Persistence 49、MCP 72、API 66；无新增/修改后端测试。

49 项前端测试覆盖纯 reducer 的重复/乱序/空洞/大整数、生命周期与查询 generation、旧审批响应、一次折叠、工具失败；控制器的同步锁、独立消息、未知 POST、SSE 先于 GET、查询合并、409/不确定恢复、终态失败重试、协议重建和 cleanup；组件的 API unavailable/Retry、API Agent 名称、即时消息、冻结参数、禁用决策、取消、三个异常终态、键盘/IME/长度与向上阅读不被拖回；HTTP ProblemDetails 和命名 EventSource 的身份/生命周期。

最初 .NET 构建被沙箱禁止读取现有用户 NuGet.Config，使用授权构建权限后完整通过。最终浏览器运行只有 Playwright 的 FORCE_COLOR/NO_COLOR 提示，无应用失败。

## 真实浏览器结果与 SSE 证据

Playwright 从构建输出启动真实三个 .NET 服务和 Vite，使用独立 SQLite 文件；拒绝复用已占用端口。没有 mock HTTP、SSE、模型或工具；模型为已有确定性脚本。测试结束由 Playwright 清理子进程。

最新验证 Run：

- 拒绝：`77283af5-a7d5-433c-9f20-ed291ee9824d`，Completed，modelTurns=2、toolCalls=0、snapshotSequence=16，回答 `Understood. I did not cancel the booking.`。
- 批准：`dc037cf9-1c93-4413-b863-865f1c843e8c`。批准前 GET 为 AwaitingApproval、finalText=null；页面已展示 1–9 共九条来自 `http://localhost:5173/api/runs/.../events?afterSequence=0` 的命名 SSE 事件，包括 ApprovalRequired。证明代理在终态之前已经逐步传输事件，并非等完成后一次返回。
- 批准后同一卡片继续收到 10 ApprovalResolved、11 RunResumed、12–13 工具发现、14–15 策略重评估、16 ToolExecutionStarted、17 ToolExecutionCompleted、18–19 第二轮模型、20 RunCompleted。GET 确认 Completed、modelTurns=2、toolCalls=1、snapshotSequence=20，回答 `Booking NZ123 has been cancelled.`，卡片折叠且答案位于其下。
- Pet 查询回答 `Yes. Cooper was fed at 08:00.`；新独立 Run 的写入任务被 Deny，toolCalls=0，展示 Blocked by policy，无审批按钮，回答操作未获许可。刷新后屏幕无历史 Run。
- 窄屏 390×844：无文档横向溢出，批准/拒绝均可见且位于 composer 上方；只读 JSON、顶栏 Agent 和下一条草稿输入可用。

可复现证据生成在 `src/PortableAgent.Studio/test-results/`：desktop-empty、desktop-approval、narrow-approval、desktop-completed、pet-denial PNG，以及 `proxied-sse-evidence.json`。这些临时产物不提交 Git；本节保留关键事件与计数。

## 限制与 Phase 7B 前状态

没有发现需要修改冻结后端契约的阻塞问题。没有开始 Phase 7B。

- 页面内历史、一个受控非终态 Run，无刷新恢复、Run 列表、Session、认证或多用户。刷新失去未知 RunId 后不能从本 UI 找回；关闭页面不停止后台执行。
- 模型仍只接受既有演示输入，最终答案是纯文本；无真实 LLM、token streaming、Markdown、推理显示。
- 未注册的未来 named event 受原生 EventSource API 限制，需升级客户端事件名单。GET 的 snapshotSequence 是现有 JSON number；若超过安全整数范围，UI 明确报查询错误而不进行不安全状态比较，SSE Int64 游标仍精确保留。
- 最多每 12 秒重新检查活动状态；isActive 不是租约。仅有前端进程内同步锁，后端审批 CAS 仍是并发权威。
- 浏览器验收使用 Chromium；Firefox/WebKit 和生产反向代理不在本阶段验证范围。原生断线/替换/generation 竞态通过可控事件源单测验证；真实浏览器验证了开发代理的增量传输。
- 无 Trace / Tools / Graph、路由器、设置、主题切换、MCP/模型配置、部署或后端恢复功能。
