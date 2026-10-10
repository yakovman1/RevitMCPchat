using RevitMCPchat.Model;
using Xunit;

namespace RevitMcpChat.RevitLogic.Tests;

public sealed class ActiveDocumentEventStateTests
{
    [Fact]
    public void ActivatingWithoutDocumentClearsPreviousActiveDocument()
    {
        var state = new ActiveDocumentEventState();
        var document = new object();
        state.Activate(document);

        state.Activate(null);

        Assert.Null(state.ActiveDocument);
        Assert.False(state.IsActive(document));
    }

    [Fact]
    public void SavedEventIsAcceptedOnlyForActiveDocument()
    {
        var state = new ActiveDocumentEventState();
        var activeDocument = new object();

        state.Activate(activeDocument);

        Assert.False(state.IsActive(new object()));
        Assert.True(state.IsActive(activeDocument));
    }

    [Fact]
    public void ClosingInactiveDocumentKeepsActiveDocument()
    {
        var state = new ActiveDocumentEventState();
        var activeDocument = new object();
        var inactiveDocument = new object();
        state.Activate(activeDocument);

        state.BeginClose(23, inactiveDocument);
        Assert.False(state.CompleteClose(23, succeeded: true));

        Assert.Same(activeDocument, state.ActiveDocument);
    }

    [Fact]
    public void ClosingActiveDocumentClearsActiveDocument()
    {
        var state = new ActiveDocumentEventState();
        var activeDocument = new object();
        state.Activate(activeDocument);
        state.BeginClose(17, activeDocument);

        Assert.True(state.CompleteClose(17, succeeded: true));

        Assert.Null(state.ActiveDocument);
    }

    [Fact]
    public void CancelledCloseKeepsActiveDocument()
    {
        var state = new ActiveDocumentEventState();
        var activeDocument = new object();
        state.Activate(activeDocument);
        state.BeginClose(17, activeDocument);

        Assert.False(state.CompleteClose(17, succeeded: false));

        Assert.Same(activeDocument, state.ActiveDocument);
    }
}
