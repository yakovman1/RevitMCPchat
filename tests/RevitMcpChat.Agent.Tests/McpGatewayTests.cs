using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RevitMcpChat.Agent;
using RevitMcpChat.Agent.Ipc;
using RevitMcpChat.Agent.Mcp;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class McpGatewayTests
{
    [Fact]
    public async Task LaunchPinsExactPid()
    {
        using var server = new FakeMcpServerFixture();
        await using var gateway = new McpGateway();

        await gateway.ConnectAsync(12345, server.ExecutablePath, CancellationToken.None);
        var tools = await gateway.ListToolsAsync(CancellationToken.None);

        Assert.Single(tools);
        Assert.Equal("revit_fake_test", tools[0].Name);
        Assert.Equal("--target pid:12345", File.ReadAllText(server.ArgumentsPath).Trim());
    }

    [Fact]
    public void NoServerReportsMissingDependency()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Null(RvtMcpLocator.FindInstalledServer(localAppData));
    }

    [Fact]
    public async Task LostTargetDoesNotRebind()
    {
        using var server = new FakeMcpServerFixture();
        await using var gateway = new McpGateway();
        await gateway.ConnectAsync(12345, server.ExecutablePath, CancellationToken.None);
        await gateway.ListToolsAsync(CancellationToken.None);

        var outcome = await gateway.CallToolAsync("revit_fake_test", "{}", CancellationToken.None);

        Assert.True(outcome.IsError);
        Assert.False(outcome.OutcomeUnknown);
        Assert.Contains("TARGET_LOST", outcome.Content, StringComparison.Ordinal);
        Assert.Equal(new[] { "--target pid:12345" }, File.ReadAllLines(server.ArgumentsPath));
    }

    [Fact]
    public async Task DispatcherReturnsUpdateGuidanceForIncompatibleRvtMcp()
    {
        using var server = new FakeMcpServerFixture("unrelated_tool");
        var temp = Path.Combine(Path.GetTempPath(), "RevitMcpChat-IncompatibleMcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var workspace = new AgentWorkspace(temp);
        try
        {
            var dispatcher = new AgentCommandDispatcher(workspace, 12345, server.ExecutablePath);
            var reply = await dispatcher.DispatchAsync(
                new AgentCommand { Kind = AgentCommandKinds.Reconnect },
                (_, _) => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal(AgentReplyKinds.Response, reply.Kind);
            var status = AgentPayloadJson.Deserialize<AgentStatus>(reply.PayloadJson);
            Assert.False(status.IsConnected);
            Assert.Contains("update", status.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(RvtMcpCompatibility.ProjectUrl, status.Message, StringComparison.Ordinal);
        }
        finally
        {
            await workspace.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(temp, recursive: true);
        }
    }

    private sealed class FakeMcpServerFixture : IDisposable
    {
        public FakeMcpServerFixture(string toolName = "revit_fake_test")
        {
            var directory = Path.Combine(Path.GetTempPath(), "RevitMcpChat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ArgumentsPath = Path.Combine(directory, "arguments.txt");
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var script = Path.Combine(AppContext.BaseDirectory, "FakeMcpServer.ps1");
            ExecutablePath = Path.Combine(directory, "fake-mcp-server.cmd");
            File.WriteAllText(ExecutablePath,
                "@echo off\r\n" +
                "echo %*>>\"" + ArgumentsPath + "\"\r\n" +
                "\"" + powershell + "\" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -ToolName \"" + toolName + "\"\r\n");
        }

        public string ExecutablePath { get; }
        public string ArgumentsPath { get; }

        public void Dispose()
        {
            var directory = Path.GetDirectoryName(ExecutablePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}


