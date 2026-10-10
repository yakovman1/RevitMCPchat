using System.Collections.Generic;
using RevitMcpChat.Agent.Console;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class ChatCommandOptionsTests
{
    [Fact]
    public void UnknownChatOptionsAreRejected()
    {
        var parsed = ChatCommandOptions.TryParse(
            new[] { "chat", "--base-urll", "https://example.test" },
            1,
            out _,
            out var error);

        Assert.False(parsed);
        Assert.Contains("Unknown chat option", error);
    }

    [Fact]
    public void ModelIdentityMustBeExplicitInsteadOfDerivedFromRevitPid()
    {
        var options = new Dictionary<string, string?> { ["--revit-pid"] = "1234" };

        Assert.False(ChatCommandOptions.TryResolveModelIdentity(options, out _));
        Assert.True(ChatCommandOptions.TryResolveModelIdentity(
            new Dictionary<string, string?> { ["--model-identity"] = "file:C:/models/house.rvt" },
            out var identity));
        Assert.Equal("file:C:/models/house.rvt", identity);
    }
}
