using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PortableAgent.Core.Tools;

namespace PortableAgent.Adapters.Mcp;

/// <summary>Single trusted source. Serializes discovery, execution and owned-resource cleanup.</summary>
public sealed class McpToolAdapter : IToolProvider, IToolExecutor, IAsyncDisposable
{
    private readonly McpSourceDefinition _source;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpClient? _client;
    private HttpClientTransport? _transport;
    private Dictionary<ToolId, ToolDefinition>? _catalog;
    private bool _disposed;

    public McpToolAdapter(McpSourceDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.SourceId);
        if (source.Endpoint is null || !source.Endpoint.IsAbsoluteUri
            || source.Endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("MCP Endpoint must be an absolute HTTP or HTTPS URI.", nameof(source));
        _source = source;
    }

    public async Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _catalog = null; // A failed refresh must not authorize execution from an old catalog.
            await InitializeAsync(cancellationToken);
            var remote = await _client!.ListToolsAsync(cancellationToken: cancellationToken);
            var catalog = new Dictionary<ToolId, ToolDefinition>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in remote)
            {
                var tool = McpToolMapper.Definition(_source.SourceId, item.ProtocolTool);
                if (!catalog.TryAdd(tool.Id, tool) || !names.Add(tool.ModelName))
                    throw new InvalidDataException("Duplicate MCP ToolId or model-visible ModelName.");
            }
            _catalog = catalog;
            return catalog.Values.Select(t => t with { InputSchema = t.InputSchema.Clone() }).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<ToolResult> ExecuteAsync(ToolDefinition registeredTool, ToolCall call, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_catalog is null || _client is null) throw new InvalidOperationException("MCP discovery must complete before execution.");
            if (!string.Equals(registeredTool.Id.SourceId, _source.SourceId, StringComparison.Ordinal)
                || !_catalog.TryGetValue(registeredTool.Id, out var discovered))
                throw new InvalidOperationException("Trusted ToolId does not belong to this discovered MCP source.");
            if (!string.Equals(registeredTool.ModelName, discovered.ModelName, StringComparison.Ordinal)
                || !JsonElement.DeepEquals(registeredTool.InputSchema, discovered.InputSchema)
                || !string.Equals(call.ToolName, registeredTool.ModelName, StringComparison.Ordinal))
                throw new InvalidOperationException("Trusted tool contract or proposed ToolName is inconsistent with discovery.");
            var arguments = McpToolMapper.Arguments(call.Arguments);
            // Runner's definition remains authoritative. The cache never replaces or rewrites it.
            var response = await _client.SendRequestAsync<CallToolRequestParams, CallToolResult>(RequestMethods.ToolsCall, new CallToolRequestParams
            {
                Name = registeredTool.Id.Name,
                Arguments = arguments
            }, McpResultJson.Options, cancellationToken: cancellationToken);
            return McpToolMapper.Result(call.CallId, response);
        }
        finally { _gate.Release(); }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_client is not null) return;
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var transport = new HttpClientTransport(new()
        {
            Endpoint = _source.Endpoint,
            Name = _source.SourceId,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            MaxReconnectionAttempts = 0
        }, http, ownsHttpClient: true);
        try
        {
            _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            _transport = transport;
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _catalog = null;
            try { if (_client is not null) await _client.DisposeAsync(); }
            finally { if (_transport is not null) await _transport.DisposeAsync(); }
        }
        finally { _gate.Release(); }
        // Do not dispose the managed gate: concurrent waiters must wake and observe _disposed.
    }
}
