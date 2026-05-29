using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SharpLensMcp.Tests;

/// <summary>
/// Base class for RoslynService integration tests.
/// Loads the SharpLensMcp solution once per test class via IClassFixture.
/// </summary>
public abstract class RoslynServiceTestBase : IClassFixture<SolutionFixture>
{
    protected RoslynService Service { get; }
    protected string SolutionPath { get; }
    protected string RoslynServicePath { get; }
    protected string McpServerPath { get; }

    protected RoslynServiceTestBase(SolutionFixture fixture)
    {
        Service = fixture.Service;
        SolutionPath = fixture.SolutionPath;
        RoslynServicePath = fixture.RoslynServicePath;
        McpServerPath = fixture.McpServerPath;
    }

    protected void AssertSuccess(object response)
    {
        var json = JObject.FromObject(response);
        json["success"].Should().NotBeNull("response envelope must include success field");
        json["success"]!.Value<bool>().Should().BeTrue(
            $"Expected success but got: {json["error"]}");
    }

    protected void AssertError(object response, string? errorCodeContains = null)
    {
        var json = JObject.FromObject(response);
        json["success"].Should().NotBeNull("response envelope must include success field");
        json["success"]!.Value<bool>().Should().BeFalse();

        if (errorCodeContains != null)
        {
            json["error"].Should().NotBeNull("error responses must carry an error object");
            json["error"]!["Code"].Should().NotBeNull("error.Code field is required");
            json["error"]!["Code"]!.Value<string>().Should().Contain(errorCodeContains);
        }
    }

    protected JToken GetData(object response)
    {
        var json = JObject.FromObject(response);
        AssertSuccess(response);
        return json["data"]!;
    }
}
