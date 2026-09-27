using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PortableAgent.Api.Contracts;

namespace PortableAgent.Api.Tests;

public sealed class RunToolsTests
{
    [Theory]
    [InlineData(false, "Completed")]
    [InlineData(true, "AwaitingApproval")]
    public async Task Returns_only_persisted_catalog_without_mutating_run_or_discovering(bool approval, string status)
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { Approval = approval };
        await using var api = await ApiTestHost.StartAsync(db, runtime);
        var id = await api.StartRunAsync();
        await api.WaitAsync(id, status);
        var before = (await db.Store.GetAsync(id, default))!;
        var events = await db.Store.ReadEventsAfterAsync(id, 0, 1000, default);
        var discoveryCount = runtime.DiscoveryCount;
        var calls = runtime.Calls.Count;

        var response = await api.Client.GetAsync($"/api/runs/{id}/tools");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<RunToolsDto>())!;
        Assert.Equal(id, dto.RunId);
        Assert.Equal(status, dto.Status);
        Assert.Equal(before.LastSequence, dto.SnapshotSequence);
        var tool = Assert.Single(dto.Tools);
        Assert.Equal(new ToolIdentityDto("test-source", "write"), tool.ToolId);
        Assert.Equal(runtime.Tool.ModelName, tool.ModelName);
        Assert.Equal(runtime.Tool.Description, tool.Description);
        Assert.True(JsonElement.DeepEquals(runtime.Tool.InputSchema, tool.InputSchema));

        var after = (await db.Store.GetAsync(id, default))!;
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.LastSequence, after.LastSequence);
        Assert.Equal(events.Select(e => e.EventId), (await db.Store.ReadEventsAfterAsync(id, 0, 1000, default)).Select(e => e.EventId));
        Assert.Equal(discoveryCount, runtime.DiscoveryCount);
        Assert.Equal(calls, runtime.Calls.Count);
    }

    [Fact]
    public async Task Running_snapshot_can_be_empty_even_after_observed_discovery()
    {
        using var db = new DatabaseFile();
        var runtime = new TestRuntime { BlockModel = true };
        await using var api = await ApiTestHost.StartAsync(db, runtime);
        var id = await api.StartRunAsync();
        await runtime.ModelEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var dto = (await api.Client.GetFromJsonAsync<RunToolsDto>($"/api/runs/{id}/tools"))!;
        Assert.Equal("Running", dto.Status);
        Assert.Empty(dto.Tools);
        Assert.Equal(1, dto.SnapshotSequence);
        Assert.Equal(1, runtime.DiscoveryCount);
        Assert.Contains(await db.Store.ReadEventsAfterAsync(id, 0, 100, default), e => e.EventType.ToString() == "ToolDiscoveryCompleted");
        runtime.ModelRelease.TrySetResult();
        await api.WaitAsync(id, "Completed");
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_id_uses_existing_problem_details(string id)
    {
        using var db = new DatabaseFile();
        await using var api = await ApiTestHost.StartAsync(db);
        var response = await api.Client.GetAsync($"/api/runs/{id}/tools");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_request", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_run_returns_404()
    {
        using var db = new DatabaseFile();
        await using var api = await ApiTestHost.StartAsync(db);
        var response = await api.Client.GetAsync($"/api/runs/{Guid.NewGuid()}/tools");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("run_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Public_response_is_an_explicit_field_whitelist()
    {
        using var db = new DatabaseFile();
        await using var api = await ApiTestHost.StartAsync(db, new TestRuntime { Approval = true });
        var id = await api.StartRunAsync();
        await api.WaitAsync(id, "AwaitingApproval");
        var body = await api.Client.GetFromJsonAsync<JsonElement>($"/api/runs/{id}/tools");
        Assert.Equal(new[] { "runId", "snapshotSequence", "status", "tools" }, body.EnumerateObject().Select(p => p.Name).Order());
        var tool = body.GetProperty("tools")[0];
        Assert.Equal(new[] { "description", "inputSchema", "modelName", "toolId" }, tool.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(new[] { "name", "sourceId" }, tool.GetProperty("toolId").EnumerateObject().Select(p => p.Name).Order());
    }
}
