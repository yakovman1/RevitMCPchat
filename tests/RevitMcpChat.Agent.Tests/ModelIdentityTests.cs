using System;
using RevitMcpChat.Agent.History;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class ModelIdentityTests
{
    [Fact]
    public void SavedIdentityNormalizesPathCaseAndSeparators()
    {
        var path = @"C:\Projects\A.rvt";

        Assert.Equal(ModelIdentity.ForSaved(path), ModelIdentity.ForSaved("c:/projects/a.rvt"));
    }

    [Fact]
    public void UnsavedIdentityIsStableForSessionAndUniqueAcrossSessions()
    {
        var sessionId = Guid.NewGuid();

        Assert.Equal(ModelIdentity.ForUnsaved(sessionId), ModelIdentity.ForUnsaved(sessionId));
        Assert.NotEqual(ModelIdentity.ForUnsaved(sessionId), ModelIdentity.ForUnsaved(Guid.NewGuid()));
    }

    [Fact]
    public void SaveAsCreatesDifferentIdentity()
    {
        Assert.NotEqual(
            ModelIdentity.ForSaved(@"C:\Projects\Original.rvt"),
            ModelIdentity.ForSaved(@"C:\Projects\Copy.rvt"));
    }
}
