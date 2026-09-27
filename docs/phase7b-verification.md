# Phase 7B：Trace、实际执行 Graph 与持久化 Tools 检查

验证日期：2026-09-27（Pacific/Auckland）。基线：Phase 7A `d4f52e09b515ad62d08bb5888a0b9e35b1bc46d8`。

按新版批准附件复核：既有 Phase 7B 提交为 `70175f1199ce0f85c6e3427c51910f810b8f3e3b`。补齐 Proposals observed / Approvals observed 计数措辞，并增加无标识 RunResumed 不推断审批因果关系的回归测试；无新增后端变更。

## 范围与生产边界

只实施 Phase 7B。没有 Session、Run 历史查询/刷新恢复、目录版本历史、配置编辑、手动工具执行、认证、真实 LLM、token streaming 或部署。

| 后端生产项目 | 变更文件数 | 内容 |
| --- | ---: | --- |
| Core | 0 | 执行语义不变 |
| Persistence | 0 | Store、实体、迁移均不变 |
| Adapters.Mcp | 0 | 无发现或调用路径变更 |
| Infrastructure | 0 | 领域与脚本模型不变 |
| Api | 2 | 新增 `Contracts/RunToolsDtos.cs`，修改 `Endpoints/RunEndpoints.cs` |

API 仅新增一个 GET；RunDto、ApiApplication、DI、Coordinator、Registry 均未修改。Studio 的 RunController / useRunExecution 不变。Chat 仅增加隐藏时的滚动保护和恢复。

唯一直接新增依赖是 **`@xyflow/react` 12.12.0**，在实现时通过 npm registry 的 `dist-tags.latest` 验证并精确固定，lockfile 已更新。设计时的 12.11.6 已不是最新稳定版本。其 React / ReactDOM peer 要求为 `>=17`，适用于本仓库 React 19.3.0。没有 Router、Dagre、ELK 或新的状态库。安装 audit 为 0 vulnerabilities。

## 唯一新增 API 契约

```http
GET /api/runs/{runId}/tools
```

```csharp
public sealed record RunToolsDto(
    Guid RunId,
    string Status,
    long SnapshotSequence,
    IReadOnlyList<ToolCatalogEntryDto> Tools);

public sealed record ToolCatalogEntryDto(
    ToolIdentityDto ToolId,
    string ModelName,
    string Description,
    JsonElement InputSchema);
```

JSON 使用 camelCase；ToolIdentityDto 继续使用 `sourceId` / `name`。只调用既有 `IRunStateStore.GetAsync`，一次快照读取后映射 `Lifecycle.ToString()`、`LastSequence`、`ToolCatalog`。Schema 使用 `Clone()`。不直接序列化 Core ToolDefinition，不公开会话、参数、工具结果或私有配置。

| 输入 / 结果 | HTTP |
| --- | --- |
| 已存在的 Run | 200，以上 DTO |
| 非 Guid 或全零 Guid | 400，既有 `invalid_request` ProblemDetails |
| 未知 Run | 404，既有 `run_not_found` ProblemDetails |
| Store / 内部异常 | 既有全局 500 处理 |

完成、等待审批和 Running 空目录都有 API 测试。读取前后的 Version、LastSequence、事件数量、业务订单及发现次数保持不变。真实 Flight MCP 测试先暂停并保存目录，停止 MCP，再读取目录成功；没有新的 tools/list，取消次数仍为 0，订单仍为 Confirmed。

## 导航、选择与共享解释

App 保留 `activeView` 和 `selectedInspectorRunClientId`；Chat / Trace / Graph / Tools 是内部状态，无路由切换。桌面导航和窄屏紧凑导航均使用带 `aria-current="page"` 的按钮。

Chat 和运行控制器持续挂载。切换不关闭或新建 EventSource、不取消 Run；保留草稿、卡片、原生 details 展开与审批。隐藏时不按零尺寸容器自动滚动，返回时恢复阅读位置。Chat 的 Show details 仍是快速原始事件入口。

检查选择器只列出绑定 RunId 的记录，以 clientId 为值，展示 Agent、消息摘要、Runtime 状态和短 RunId。第一条默认选中，此后不会因新 Run 开始而更换目标。无 Run 时显示 Go to Chat。刷新仍清空 Studio 历史。

`features/inspection/` 统一派生 orderedEvents、epochs、modelTurns、discoveries、calls、approvals、terminalEvent、unassociatedEvents、diagnostics。全部保留源 Observation / Sequence / EventType；不修改 `eventsBySequence`。整数 Sequence 用十进制字符串与 BigInt 排序、去重，允许空洞，不按时间排序。

Call 记录保留 proposals、policyEvaluations、approvalObservations、executionStarts、executionCompletions 数组。重复 CallId 的提案发生在 Runtime 唯一性检查之前，因此每条提案观察单独保留；标记歧义后，不把后续事实强行分给其中某条。未知或缺少关联身份的事件仍可查看并有诊断。

## Trace 与 Graph

Trace 每条事件一行，六种过滤器为 All / Lifecycle / Model / Tools / Policy / Approval。未知类型保留在 All。摘要只读实际 payload，缺字段显示 Not provided 或省略。行可键盘展开原始 EventId、RunId、精确 Sequence、服务端 OccurredAt、EventType、payload；另显示本地毫秒时间。

Trace 读取原有事件集合，不另开 SSE。靠近底部时跟随，否则保留位置并显示 New events。过滤和增量更新保留展开状态。GET 已终止但终止事件未到时，标题明确区分；不合成终止行或图节点。

Graph 的七类节点是 Run Start、Tool Discovery、Model Turn、Tool Operation、Approval、Run Resume、Terminal。策略和执行事件作为操作详情，不逐事件造节点。纯投影、确定性布局与 React Flow 渲染分离。

- 实线：有观察支持的 model→proposal、operation→approval 关系；不表示模型隐藏推理。
- 虚线：执行观察顺序；不表示数据依赖。
- RunResumed 没有 callId / approvalId；恢复节点不补造身份，其相邻边保持观察顺序。测试包含多个候选审批，确认没有生成精确因果关系。
- 一个模型响应的多个工具为兄弟节点，不画 A→B→C，不宣称并行执行。
- 操作显示身份包含 RunId、阶段、锚点 Sequence、CallId；重复提案不合并。批准后是同一逻辑调用的恢复执行阶段，不是第二次模型提案，也没有回边或第二个 Run Start。
- 状态区分 Waiting approval、Blocked by policy、Completion not observed、Execution not observed yet、Not executed、Execution not observed、Returned failure。Not executed 只描述该阶段没有观察到开始，不表示整个逻辑调用从未执行。问题流使用保守标签；不编造 prior-failure skip。工具返回失败不等于 RunFailed。

只允许平移、缩放、选择和 Fit graph；禁用拖动节点、连接编辑、重连和删除。不可见的锚点仅用于渲染边，没有工具调用或保存布局入口。首次打开执行一次 fit，并限制最低初始缩放以保留可读性；显式 Fit graph 可缩小到全图概览。用户视角在跨视图时保存，新事件不重置镜头，未变节点对象复用。节点用图标、文字和颜色表达状态，文本详情可键盘访问；Trace 始终提供全部原始事件。

## Tools 快照、关联与请求生命周期

目录区明确标记 Persisted tool catalog snapshot，展示自身的 status / snapshotSequence，不覆盖 RunController 的观察状态。它代表最新持久化 RunState 中的目录，不是 MCP 当前实时目录，也不是历史版本。

| 快照情况 | 展示语义 |
| --- | --- |
| Running 且空 | Tool catalog snapshot is not yet durably available；若已观察到发现数量，另行展示该事实 |
| Running 且非空 | 最新持久化目录，可能来自较早检查点 |
| 终态且空 | 此持久化 Run 快照没有工具目录；不声称 Agent 没有工具 |

恢复可能替换目录，所以当前 Schema 不被宣称为所有历史调用使用的 Schema。Schema 可展开/收起与复制，只读，没有表单或执行入口。

使用记录与目录定义分区。优先使用事件中的可信身份，其次按唯一 ModelName/toolName 匹配；身份冲突、重复提案或名称歧义留作 unassociated。名称匹配标注其证据级别。策略标题是 Observed policy in this Run，保留全部按序策略和审批观察；无观察时说明未观察到策略评估，不冒充配置查询。

分别统计 Execution starts observed、Execution completions observed、Returned failures；开始不等于业务成功，未使用目录工具也可查看。

Proposals observed 统计提案事件；Approvals observed 统计已观察到的不同 ApprovalId，并明确标注 distinct approval IDs。一次 ApprovalRequired 和同一 ID 的 ApprovalResolved 合计一个审批，两条原始观察都保留；缺失 ID 的观察不被猜测为新的审批。

目录查询拥有独立 runId 缓存、请求 generation、每 Run 单个在途请求和合并刷新。首次进入 Tools、显式 Refresh、相关生命周期/快照检查点变化触发读取，不逐事件请求。旧响应不替换较新 snapshotSequence 或其他选中 Run 的显示；失败保留已有目录并显示错误。API long 在当前 JSON 契约中仍是 number，超出 JavaScript 安全整数范围的 snapshotSequence 被拒绝，避免错误比较；SSE Sequence 继续精确处理。

## 自动验证

环境：.NET SDK 10.0.400，Node 22.22.1，npm 10.9.4，Playwright 1.63.0，Chromium 153.0.8010.12。

| 命令 | 结果 |
| --- | --- |
| `npm install --save-exact @xyflow/react@12.12.0` | 成功，lockfile 更新，audit 0 |
| `npm test` | 15 文件、94 测试通过；保留原 49，新增 45 |
| `npm run build` | TypeScript 与 Vite 成功 |
| `npm run test:e2e` | 2 个扩展的真实浏览器流程通过 |
| `dotnet build PortableAgent.sln` | 0 警告、0 错误 |
| `dotnet test PortableAgent.sln` | 276 通过，0 失败、0 跳过 |

后端分布：Core 41、Integration 40、Persistence 49、MCP 72、API 74（基线 66，新增 8）。没有以 mock-only 浏览器替代真实验收。

新增前端覆盖：共享解释 12、Graph 投影 13、Tools 关联 3、Tools 缓存 4、Trace UI 2、Tools UI 5、GraphInspector 1、跨视图 3、Graph UI 2。包含 BigInt/空洞/去重、原始 payload 一致性、过滤/键盘/滚动、歧义保留、审批恢复、批次、业务失败、相机稳定、只读属性、目录时效及竞态。Graph UI 测试隔离 React Flow 边界，不测试库内部；真实浏览器另验证不能拖动或删除节点。

## 真实 Flight / Pet 浏览器结果

Playwright 自动启动真实 Pet / Flight MCP、API、Vite，使用独立 SQLite。浏览器通过 localhost:5173 `/api` 代理和原生 EventSource，不 mock HTTP/SSE。保留原先先拒绝、再批准，以及 Pet 读取后拒绝策略的回归步骤。

最终 Flight 批准 RunId：`f7302fdf-ccd2-468c-9e1a-7f5fb293a7dd`。

| 检查点 | 结果 |
| --- | --- |
| 等待审批 | AwaitingApproval，9 条事件，Graph 5 节点 |
| Chat→Trace→Graph→Tools | 同一选中 Run，目录快照 AwaitingApproval / Sequence 9，审批与草稿保留 |
| 返回 Chat 批准 | 同 Run 恢复，最终 Completed，1 次工具调用 |
| 最终 Trace | 实际 Sequence 1–20，共 20 行 |
| 最终 Graph | 10 节点，原 5 个 ID 保留，操作的两个阶段共享 call-1，仅 1 次提案 |
| 最终目录 | Completed / Sequence 20，get_booking 与 cancel_booking |
| cancel_booking 观察 | #8 与 #15 两次 RequireApproval，#10 Approved，开始 1、完成 1、返回失败 0 |
| 最终 Chat | `Booking NZ123 has been cancelled.` |
| SSE 连续性 | 两个独立 Flight Run 合计构造 2 个 EventSource；跨视图与审批后仍为 2，无新增连接 |

Graph 路径：Start → Discovery → Model 1 → cancel_booking proposal stage → Approval → Resume → Discovery → cancel_booking execution stage → Model 2 → Completed。

Pet 精确输入 `Ask the staff to give Cooper some fresh water.`：Trace 有提案和 Deny，无 ToolExecutionStarted；Graph 显示 create_staff_task / Blocked by policy；Tools 显示 `pet-mcp / create_staff_task`、Deny、开始 0。无审批或手动执行按钮。此前护理读取仍完成为独立 Run，刷新清空历史。

桌面与 390×844 窄屏均验收。紧凑导航可到达三种检查视图，无页面水平溢出；窄屏 Graph 可以平移缩放，选中详情提供文字。检查了生成截图。

重跑生成的本地证据位于 `src/PortableAgent.Studio/test-results/`（忽略于 Git，后续测试会覆盖）：

- Flight 子目录 `flight-approval-real-Fligh-2fd88-approval-and-durable-answer/`：`proxied-sse-evidence.json`、`phase7b-inspection-evidence.json`，含实际事件、Run 状态、节点 ID、目录和观察用量。
- 同目录：`trace-paused.png`、`graph-paused.png`、`trace-completed.png`、`graph-completed.png`、`tools-completed.png`、`narrow-trace.png`、`narrow-graph.png`、`narrow-tools.png`，以及原 Chat 截图。
- Pet 子目录 `flight-approval-real-Pet-r-a6fbd-n-separate-independent-Runs/`：`pet-denial.png`、`pet-tools-denial.png`。

## MVP 边界

原定以确定性模型验证的 Runtime + API + Studio MVP 已完成本阶段验收，无 Phase 7B 遗留必做项。它验证可迁移执行、持久化审批、MCP 适配和可观察界面，不代表已交付任意自然语言助手或生产服务。脚本输入、单活动任务、无 Session/刷新恢复、无认证/部署及既有外部副作用恢复限制继续成立。

新的真实模型、会话或配置能力需要另行定义范围；本阶段没有自动继续开发。
