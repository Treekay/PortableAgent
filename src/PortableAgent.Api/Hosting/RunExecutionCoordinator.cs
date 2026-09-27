using PortableAgent.Api.Endpoints;
using PortableAgent.Core.Execution;

namespace PortableAgent.Api.Hosting;

/// <summary>Owns execution independently of HTTP callers. SQLite CAS remains approval authority.</summary>
public sealed class RunExecutionCoordinator(RunOperationSignals signals, AgentRuntimeRegistry registry,
    IHostApplicationLifetime lifetime, ILogger<RunExecutionCoordinator> logger) : IHostedService
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _active = [];
    private bool _stopping;

    public sealed class Operation(Guid runId, Task committed)
    {
        public Guid RunId { get; } = runId;
        public Task Committed { get; } = committed;
        public Task<ApprovalSubmissionStatus?> Completion { get; internal set; } = null!;
    }

    private sealed class Entry(AgentRuntimeRegistration registration, CancellationTokenSource source,
        RunOperationSignals.Signal signal, Operation operation)
    {
        public AgentRuntimeRegistration Registration { get; } = registration;
        public CancellationTokenSource Source { get; } = source;
        public RunOperationSignals.Signal Signal { get; } = signal;
        public Operation Operation { get; } = operation;
    }

    public bool IsActive(Guid id) { lock (_gate) return _active.ContainsKey(id); }

    public Operation Start(AgentRuntimeRegistration registration, string message)
    {
        var id = Guid.NewGuid();
        return Schedule(id, registration, RunOperationKind.Start, async ct =>
        {
            await registration.Runner.RunAsync(id, new(message), ct);
            return null;
        });
    }

    public Operation Resume(AgentRuntimeRegistration registration, ApprovalCommand command) =>
        Schedule(command.RunId, registration, RunOperationKind.Approval,
            async ct => (await registration.Runner.SubmitApprovalAsync(command, ct)).Status);

    private Operation Schedule(Guid id, AgentRuntimeRegistration registration, RunOperationKind kind,
        Func<CancellationToken, Task<ApprovalSubmissionStatus?>> execute)
    {
        lock (_gate)
        {
            if (_stopping || lifetime.ApplicationStopping.IsCancellationRequested)
                throw new ApiProblem(503, "host_stopping", "Host is not accepting new executions.");
            if (_active.ContainsKey(id)) throw new ApiProblem(409, "approval_conflict", "Run is already active in this host.");
            var signal = signals.Arm(id, kind);
            var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            var operation = new Operation(id, signal.Committed);
            var entry = new Entry(registration, source, signal, operation);
            _active.Add(id, entry);
            operation.Completion = ExecuteAsync(id, entry, execute);
            return operation;
        }
    }

    private async Task<ApprovalSubmissionStatus?> ExecuteAsync(Guid id, Entry entry,
        Func<CancellationToken, Task<ApprovalSubmissionStatus?>> execute)
    {
        // Yield admission before execution's synchronous prefix; retain and observe the async Task, not Task.Run.
        await Task.Yield();
        try { return await execute(entry.Source.Token); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background operation failed for Run {RunId}", id);
            return null;
        }
        finally
        {
            lock (_gate)
            {
                signals.Remove(id, entry.Signal);
                _active.Remove(id);
                entry.Source.Dispose();
            }
        }
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var entry)) return false;
            // Synchronize with disposal. Runtime dependencies must cooperate with cancellation.
            entry.Source.Cancel();
            return true;
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _ = registry.List(); // Force trusted registration validation before the host accepts requests.
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Task[] tasks;
        lock (_gate)
        {
            _stopping = true;
            foreach (var entry in _active.Values.ToArray()) entry.Source.Cancel();
            tasks = _active.Values.Select(e => (Task)e.Operation.Completion).ToArray();
        }
        try { await Task.WhenAll(tasks).WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { logger.LogWarning("Host shutdown deadline elapsed; active Runs may retain Running snapshots."); }
        // DI disposes the registry-owned resources after hosted services have stopped.
    }
}
