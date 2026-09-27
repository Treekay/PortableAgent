namespace PortableAgent.Adapters.Mcp;

/// <summary>Host-owned configuration. Endpoint includes the complete MCP route.</summary>
public sealed record McpSourceDefinition(string SourceId, Uri Endpoint);
