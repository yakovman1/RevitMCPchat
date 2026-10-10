using RevitMcpChat.Agent.Chat;
using RevitMcpChat.Agent.Mcp;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class ToolPolicyTests
{
    [Fact]
    public void ReadOnlyToolRunsWithoutApproval() =>
        Assert.False(new ToolPolicy().RequiresApproval(new McpToolDescriptor("revit_get_levels", "Read levels", "{}", true)));

    [Fact]
    public void UnknownToolRequiresApproval() =>
        Assert.True(new ToolPolicy().RequiresApproval(new McpToolDescriptor("custom_tool", "Unknown behavior", "{}", null)));

    [Fact]
    public void SendCodeRequiresApprovalEvenWhenReadOnlyHintIsTrue() =>
        Assert.True(new ToolPolicy().RequiresApproval(new McpToolDescriptor("revit_send_code_to_revit", "Run C#", "{}", true)));

    [Fact]
    public void BatchRequiresApprovalEvenWhenReadOnlyHintIsTrue() =>
        Assert.True(new ToolPolicy().RequiresApproval(new McpToolDescriptor("revit_batch_execute", "Run several actions", "{}", true)));
}
