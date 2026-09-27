import { terminal, type AgentDto } from '../../api/contracts';
import { EventStream, type SourceFactory } from '../../api/eventStream';
import { ApiError, errorText, type PortableAgentApi } from '../../api/portableAgentApi';
import { newRun, runReducer, type Action } from './runReducer';
import { canCancel, controlsRun, type StudioState } from './runTypes';

interface NetworkState { stream: EventStream; querying: boolean; refreshNeeded: boolean; generation: number; abort?: AbortController }
export class RunController {
  private state: StudioState = [];
  private listeners = new Set<() => void>();
  private networks = new Map<string, NetworkState>();
  private active = false;
  private epoch = 0;
  private timer?: ReturnType<typeof setInterval>;
  constructor(private api: PortableAgentApi, private sources?: SourceFactory) {}
  getSnapshot = () => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  dispatch = (action: Action) => { this.state = runReducer(this.state, action); this.listeners.forEach(l => l()); };
  private run(id: string) { return this.state.find(r => r.clientId === id); }
  private network(id: string) {
    if (!this.networks.has(id)) this.networks.set(id, { stream: new EventStream(this.sources), querying: false, refreshNeeded: false, generation: 0 });
    return this.networks.get(id)!;
  }
  activate() {
    if (this.active) return;
    this.active = true;
    for (const run of this.state) if (run.runId && !terminal(run.runtimeStatus) && !run.released) this.reconnect(run.clientId);
    this.timer = setInterval(() => {
      for (const run of this.state) if (run.runId && !terminal(run.runtimeStatus) && !run.released) void this.refresh(run.clientId);
    }, 12000);
  }
  suspend() {
    this.active = false;
    ++this.epoch;
    clearInterval(this.timer);
    this.networks.forEach(n => { n.stream.close(); n.abort?.abort(); n.querying = false; n.refreshNeeded = false; ++n.generation; });
  }
  async start(agent: AgentDto, message: string): Promise<boolean> {
    if (!this.active || !message.trim() || message.length > 8000 || this.state.some(controlsRun)) return false;
    const id = crypto.randomUUID(), epoch = this.epoch;
    this.dispatch({ type: 'add', run: newRun(id, agent.id, agent.name, message) }); // Synchronous lock, before the first await.
    try {
      const accepted = await this.api.startRun(agent.id, message);
      if (!this.active || epoch !== this.epoch) return false;
      this.dispatch({ type: 'accepted', id, value: accepted });
      this.open(id);
      void this.refresh(id);
      return true;
    } catch (error) {
      if (!this.active || epoch !== this.epoch) return false;
      const rejected = error instanceof ApiError && error.kind === 'http' && [400, 404, 409, 503].includes(error.status ?? 0);
      this.dispatch({ type: 'command', id, command: 'startCommand', value: { state: rejected ? 'rejected' : 'unknown',
        message: rejected ? errorText(error) : 'Unable to confirm whether this run started. Trying again may create a duplicate run.' } });
      return false;
    }
  }
  private open(id: string) {
    const run = this.run(id);
    if (!this.active || !run?.runId || !run.eventsUrl || terminal(run.runtimeStatus)) return;
    this.network(id).stream.open(run.eventsUrl, run.runId, run.lastSequenceId, {
      state: (state, message) => {
        this.dispatch({ type: 'stream', id, state, message });
        if (['reconnecting', 'unavailable', 'protocol'].includes(state)) void this.refresh(id);
      },
      event: event => {
        const previous = this.run(id)!;
        if (previous.eventsBySequence[event.id]) return;
        this.dispatch({ type: 'event', id, event });
        if (['RunCompleted', 'RunFailed', 'RunCancelled', 'RunLimitReached'].includes(event.eventType)) {
          this.network(id).stream.close();
          this.dispatch({ type: 'stream', id, state: 'closed' });
          void this.refresh(id);
        } else if (event.eventType === 'ApprovalRequired' || event.eventType === 'ApprovalResolved' || event.eventType === 'RunResumed') void this.refresh(id);
      },
    });
  }
  reconnect = (id: string) => { this.open(id); void this.refresh(id); };
  async refresh(id: string) {
    const run = this.run(id);
    if (!this.active || !run?.runId) return;
    const net = this.network(id);
    if (net.querying) { net.refreshNeeded = true; return; }
    net.querying = true;
    const epoch = this.epoch;
    do {
      net.refreshNeeded = false;
      const generation = ++net.generation;
      const uncertainApproval = this.run(id)?.approvalCommand.state === 'unknown' ? this.run(id)?.approvalCommand.approvalId : undefined;
      net.abort = new AbortController();
      this.dispatch({ type: 'queryStart', id, generation });
      try {
        const value = await this.api.getRun(run.runId, net.abort.signal);
        if (!this.active || epoch !== this.epoch || generation !== net.generation) return;
        this.dispatch({ type: 'snapshot', id, generation, value });
        const current = this.run(id)!;
        if (uncertainApproval && current.queryState === 'ready' && current.pendingApproval?.approvalId === uncertainApproval)
          this.dispatch({ type: 'command', id, command: 'approvalCommand', value: { state: 'rejected', approvalId: uncertainApproval,
            message: 'Approval is still pending. You may submit a decision again.' } });
        if (terminal(current.runtimeStatus) && (['reconnecting', 'unavailable', 'protocol', 'closed'].includes(current.streamState)
          || BigInt(current.lastSequenceId) >= BigInt(current.stateSequenceId))) {
          net.stream.close();
          this.dispatch({ type: 'stream', id, state: 'closed' });
        }
      } catch (error) {
        if (!this.active || epoch !== this.epoch || generation !== net.generation) return;
        this.dispatch({ type: 'queryError', id, generation, message: errorText(error) });
      }
    } while (net.refreshNeeded && this.active && epoch === this.epoch);
    if (epoch === this.epoch) net.querying = false;
  }
  async approve(id: string, decision: 'approve' | 'reject') {
    const run = this.run(id), epoch = this.epoch;
    if (!run?.runId || run.runtimeStatus !== 'AwaitingApproval' || !run.pendingApproval
      || ['submitting', 'accepted', 'unknown'].includes(run.approvalCommand.state)) return;
    const approvalId = run.pendingApproval.approvalId;
    this.dispatch({ type: 'command', id, command: 'approvalCommand', value: { state: 'submitting', approvalId } });
    try {
      await this.api.submitApproval(run.runId, approvalId, decision);
      if (this.active && epoch === this.epoch) this.dispatch({ type: 'command', id, command: 'approvalCommand', value: { state: 'accepted', approvalId } });
    } catch (error) {
      if (!this.active || epoch !== this.epoch) return;
      this.dispatch({ type: 'command', id, command: 'approvalCommand', value: { state: 'unknown', approvalId, message: errorText(error) } });
      await this.refresh(id);
    }
  }
  async cancel(id: string) {
    const run = this.run(id), epoch = this.epoch;
    if (!run?.runId || !canCancel(run)) return;
    this.dispatch({ type: 'command', id, command: 'cancelCommand', value: { state: 'submitting' } });
    try {
      await this.api.cancelRun(run.runId);
      if (this.active && epoch === this.epoch) this.dispatch({ type: 'command', id, command: 'cancelCommand', value: { state: 'accepted' } });
    } catch (error) {
      if (!this.active || epoch !== this.epoch) return;
      this.dispatch({ type: 'command', id, command: 'cancelCommand', value: { state: 'rejected', message: errorText(error) } });
      void this.refresh(id);
    }
  }
  releaseUnknown = (id: string) => { if (this.run(id)?.startCommand.state === 'unknown') this.dispatch({ type: 'release', id }); };
  expand = (id: string) => this.dispatch({ type: 'expand', id });
}
