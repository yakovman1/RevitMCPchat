using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.RevitLogic.Tests;

public sealed class AgentLifecycleTests
{
    [Fact]
    public async Task ClosingOneHostStopsOnlyItsAgent()
    {
        var factory = new FakeProcessFactory();
        var firstPipe = AgentPipeNames.ForRevitPid(3111);
        var secondPipe = AgentPipeNames.ForRevitPid(6222);
        var first = new AgentProcessHost(
            "RevitMcpChat.Agent.exe",
            factory,
            (pipe, _) =>
            {
                factory.Find(pipe).Exit();
                return Task.CompletedTask;
            });
        var second = new AgentProcessHost(
            "RevitMcpChat.Agent.exe",
            factory,
            (pipe, _) =>
            {
                factory.Find(pipe).Exit();
                return Task.CompletedTask;
            });

        await first.StartAsync(3111, firstPipe, CancellationToken.None);
        await second.StartAsync(6222, secondPipe, CancellationToken.None);

        Assert.Contains("--revit-pid 3111", factory.Children[0].StartInfo.Arguments);
        Assert.Contains(firstPipe, factory.Children[0].StartInfo.Arguments);
        Assert.Contains("--revit-pid 6222", factory.Children[1].StartInfo.Arguments);
        Assert.Contains(secondPipe, factory.Children[1].StartInfo.Arguments);

        await first.StopAsync(CancellationToken.None);

        Assert.True(factory.Children[0].HasExited);
        Assert.False(factory.Children[1].HasExited);

        await second.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureRunningRestartsExitedAgentForSameRevitProcess()
    {
        var factory = new FakeProcessFactory();
        var pipe = AgentPipeNames.ForRevitPid(4224);
        var host = new AgentProcessHost(
            "RevitMcpChat.Agent.exe",
            factory,
            (pipeName, _) =>
            {
                factory.FindLast(pipeName).Exit();
                return Task.CompletedTask;
            });

        await host.StartAsync(4224, pipe, CancellationToken.None);
        factory.Children[0].Exit();

        await host.EnsureRunningAsync(4224, pipe, CancellationToken.None);

        Assert.Equal(2, factory.Children.Count);
        Assert.Contains("--revit-pid 4224", factory.Children[1].StartInfo.Arguments);
        Assert.Contains(pipe, factory.Children[1].StartInfo.Arguments);
        Assert.True(host.IsRunning);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureRunningDoesNotStartDuplicateAgent()
    {
        var factory = new FakeProcessFactory();
        var pipe = AgentPipeNames.ForRevitPid(5335);
        var host = new AgentProcessHost("RevitMcpChat.Agent.exe", factory);

        await host.StartAsync(5335, pipe, CancellationToken.None);
        await host.EnsureRunningAsync(5335, pipe, CancellationToken.None);

        Assert.Single(factory.Children);
    }

    [Fact]
    public void InstalledAgentPathResolvesRelativeToAddinInCustomInstallRoot()
    {
        var installRoot = Path.Combine(Path.GetTempPath(), "RevitMcpChat-AgentPath-" + System.Guid.NewGuid().ToString("N"));
        var addinDirectory = Path.Combine(installRoot, "addin", "2024");
        var addinPath = Path.Combine(addinDirectory, "RevitMCPchat.dll");
        var agentPath = Path.Combine(installRoot, "agent", "RevitMcpChat.Agent.exe");
        Directory.CreateDirectory(addinDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(agentPath)!);
        File.WriteAllText(addinPath, "test assembly placeholder");
        File.WriteAllText(agentPath, "test agent placeholder");

        try
        {
            Assert.Equal(agentPath, AgentProcessHost.FindInstalledAgentPath(addinPath));
        }
        finally
        {
            Directory.Delete(installRoot, recursive: true);
        }
    }

    private sealed class FakeProcessFactory : IAgentChildProcessFactory
    {
        public List<FakeChildProcess> Children { get; } = new();

        public IAgentChildProcess Create(ProcessStartInfo startInfo)
        {
            var child = new FakeChildProcess(startInfo);
            Children.Add(child);
            return child;
        }

        public FakeChildProcess Find(string pipeName) => Children.Find(child => child.StartInfo.Arguments.Contains(pipeName))!;
        public FakeChildProcess FindLast(string pipeName) => Children.FindLast(child => child.StartInfo.Arguments.Contains(pipeName))!;
    }

    private sealed class FakeChildProcess : IAgentChildProcess
    {
        private readonly TaskCompletionSource<bool> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeChildProcess(ProcessStartInfo startInfo) => StartInfo = startInfo;

        public ProcessStartInfo StartInfo { get; }
        public bool HasExited => _exit.Task.IsCompleted;

        public void Start() { }
        public void Kill() => Exit();
        public void Exit() => _exit.TrySetResult(true);
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);
        public void Dispose() { }
    }
}
