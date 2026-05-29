using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Loads the SharpLensMcp solution once per test class.
/// Use via IClassFixture&lt;SolutionFixture&gt; to avoid reloading
/// the MSBuildWorkspace for every test method.
/// </summary>
public sealed class SolutionFixture : IAsyncLifetime
{
    public RoslynService Service { get; private set; } = null!;
    public string SolutionPath { get; private set; } = null!;
    public string RoslynServicePath { get; private set; } = null!;
    public string McpServerPath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var solutionDir = currentDir;

        while (solutionDir != null && !File.Exists(Path.Combine(solutionDir, "SharpLensMcp.sln")))
            solutionDir = Directory.GetParent(solutionDir)?.FullName;

        if (solutionDir == null)
            throw new InvalidOperationException("Could not find SharpLensMcp.sln");

        SolutionPath = Path.Combine(solutionDir, "SharpLensMcp.sln");
        RoslynServicePath = Path.Combine(solutionDir, "src", "RoslynService.cs");
        McpServerPath = Path.Combine(solutionDir, "src", "McpServer.cs");

        Service = new RoslynService();
        var result = await Service.LoadSolutionAsync(SolutionPath);

        var json = JObject.FromObject(result);
        json["success"].Should().NotBeNull("every response envelope must include a success field");
        json["success"]!.Value<bool>().Should().BeTrue("Solution should load successfully");
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}
