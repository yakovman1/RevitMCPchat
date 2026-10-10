using System;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Ipc;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.RevitLogic.Tests;

public sealed class ModelIdentityBridgeTests
{
    [Fact]
    public void DocumentSaveBindsUnsavedIdentity()
    {
        var unsavedIdentity = ModelIdentity.ForUnsaved(Guid.NewGuid());
        var savedIdentity = ModelIdentity.ForSaved(@"C:\Projects\FirstSave.rvt");

        var transition = ModelIdentityChange.FromSave(unsavedIdentity, savedIdentity);

        Assert.NotNull(transition);
        Assert.True(transition!.ShouldBindExistingChats);
        Assert.Equal(unsavedIdentity, transition.PreviousIdentity);
        Assert.Equal(savedIdentity, transition.CurrentIdentity);
    }

    [Fact]
    public void SaveAsCreatesNewIdentity()
    {
        var originalIdentity = ModelIdentity.ForSaved(@"C:\Projects\Original.rvt");
        var savedAsIdentity = ModelIdentity.ForSaved(@"C:\Projects\Copy.rvt");

        var transition = ModelIdentityChange.FromSave(originalIdentity, savedAsIdentity);

        Assert.NotNull(transition);
        Assert.False(transition!.ShouldBindExistingChats);
        Assert.NotEqual(transition.PreviousIdentity, transition.CurrentIdentity);
    }

    [Fact]
    public async Task ActiveDocumentChangeBeforeApprovalBlocksCall()
    {
        var gate = new ModelBoundApprovalGate();
        var prompt = new ToolApprovalPrompt
        {
            ConversationId = Guid.NewGuid(),
            ModelIdentity = ModelIdentity.ForSaved(@"C:\Projects\A.rvt"),
            Approval = new ToolApprovalRequest { CallId = "call-1", ToolName = "revit_create_wall" }
        };
        var pending = gate.RequestAsync(prompt, (_, _) => System.Threading.Tasks.Task.CompletedTask, CancellationToken.None);

        var decision = await gate.DecideAsync(new ToolApprovalDecision
        {
            ConversationId = prompt.ConversationId,
            CallId = prompt.Approval.CallId,
            ActiveModelIdentity = ModelIdentity.ForSaved(@"C:\Projects\B.rvt"),
            Approved = true
        }, CancellationToken.None);

        Assert.Equal("active_model_changed", decision.ErrorCode);
        Assert.False(await pending);
    }
}
