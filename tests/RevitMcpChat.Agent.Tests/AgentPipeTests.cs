using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RevitMcpChat.Agent;
using RevitMcpChat.Agent.Ipc;
using RevitMcpChat.Agent.Settings;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class AgentPipeTests
{
    [Fact]
    public async Task TwoRevitPidsUseDifferentPipeSessions()
    {
        const int firstPid = 4811;
        const int secondPid = 7922;
        var firstPipe = AgentPipeNames.ForRevitPid(firstPid);
        var secondPipe = AgentPipeNames.ForRevitPid(secondPid);
        Assert.NotEqual(firstPipe, secondPipe);

        using var cancellation = new CancellationTokenSource();
        var firstServer = new AgentPipeServer(firstPipe, new PidEchoDispatcher(firstPid));
        var secondServer = new AgentPipeServer(secondPipe, new PidEchoDispatcher(secondPid));
        var firstServerTask = firstServer.RunAsync(cancellation.Token);
        var secondServerTask = secondServer.RunAsync(cancellation.Token);
        try
        {
            var firstEvents = new List<string>();
            var secondEvents = new List<string>();
            var firstReply = await new AgentPipeClient(firstPipe).SendAsync(
                new AgentCommand { Kind = AgentCommandKinds.SettingsGet },
                item =>
                {
                    firstEvents.Add(item.PayloadJson);
                    return Task.CompletedTask;
                },
                cancellation.Token);
            var secondReply = await new AgentPipeClient(secondPipe).SendAsync(
                new AgentCommand { Kind = AgentCommandKinds.SettingsGet },
                item =>
                {
                    secondEvents.Add(item.PayloadJson);
                    return Task.CompletedTask;
                },
                cancellation.Token);

            Assert.Equal(firstPid.ToString(), firstReply.PayloadJson);
            Assert.Equal(secondPid.ToString(), secondReply.PayloadJson);
            Assert.Equal(new[] { firstPid.ToString() }, firstEvents);
            Assert.Equal(new[] { secondPid.ToString() }, secondEvents);
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(firstServerTask, secondServerTask);
        }
    }

    [Fact]
    public async Task PipeRejectsInvalidCommand()
    {
        var temp = Path.Combine(Path.GetTempPath(), "RevitMcpChatInvalidPipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var workspace = new AgentWorkspace(temp);
        try
        {
            var pipeName = AgentPipeNames.ForRevitPid(Environment.ProcessId);
            using var cancellation = new CancellationTokenSource();
            var server = new AgentPipeServer(pipeName, new AgentCommandDispatcher(workspace, Environment.ProcessId));
            var serverTask = server.RunAsync(cancellation.Token);
            try
            {
                var reply = await new AgentPipeClient(pipeName).SendAsync(
                    new AgentCommand { Kind = "not-a-command" },
                    cancellation.Token);

                Assert.Equal("invalid_command", reply.ErrorCode);
            }
            finally
            {
                cancellation.Cancel();
                await serverTask;
            }
        }
        finally
        {
            await workspace.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task HistoryCreateCommandCreatesModelScopedChat()
    {
        var temp = Path.Combine(Path.GetTempPath(), "RevitMcpChatHistoryCreate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var workspace = new AgentWorkspace(temp);
        try
        {
            await workspace.SetActiveProviderAsync(new ProviderProfile
            {
                ProviderId = "openai",
                DisplayName = "OpenAI",
                ModelId = "gpt-4o-mini"
            });

            var pipeName = AgentPipeNames.ForRevitPid(Environment.ProcessId);
            using var cancellation = new CancellationTokenSource();
            var server = new AgentPipeServer(pipeName, new AgentCommandDispatcher(workspace, Environment.ProcessId));
            var serverTask = server.RunAsync(cancellation.Token);
            try
            {
                var modelIdentity = ModelIdentity.ForSaved(@"C:\Projects\CreateChat.rvt");
                var reply = await new AgentPipeClient(pipeName).SendAsync(new AgentCommand
                {
                    Kind = AgentCommandKinds.HistoryCreate,
                    PayloadJson = AgentPayloadJson.Serialize(new HistoryCreateRequest { ModelIdentity = modelIdentity })
                }, cancellation.Token);

                Assert.Equal(AgentReplyKinds.Response, reply.Kind);
                var created = AgentPayloadJson.Deserialize<HistoryChatRequest>(reply.PayloadJson);
                var transcript = await workspace.OpenChatAsync(created.ChatId);
                Assert.Equal(modelIdentity, transcript.ModelIdentity);
                Assert.Equal("openai", transcript.ProviderId);
                Assert.Equal("gpt-4o-mini", transcript.ModelId);
            }
            finally
            {
                cancellation.Cancel();
                await serverTask;
            }
        }
        finally
        {
            await workspace.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(temp, recursive: true);
        }
    }

    private sealed class PidEchoDispatcher : IAgentCommandDispatcher
    {
        private readonly int _pid;

        public PidEchoDispatcher(int pid) => _pid = pid;

        public async Task<AgentReply> DispatchAsync(
            AgentCommand command,
            Func<AgentReply, CancellationToken, Task> publishAsync,
            CancellationToken cancellationToken)
        {
            var payload = _pid.ToString();
            await publishAsync(new AgentReply
            {
                RequestId = command.RequestId,
                Kind = AgentReplyKinds.Event,
                PayloadJson = payload
            }, cancellationToken);

            return new AgentReply
            {
                RequestId = command.RequestId,
                Kind = AgentReplyKinds.Response,
                PayloadJson = payload
            };
        }
    }
}
