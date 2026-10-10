namespace RevitMCPchat.Model;

public sealed class ActiveDocumentEventState
{
    private object? _activeDocument;
    private object? _closingDocument;
    private int? _closingDocumentId;

    public object? ActiveDocument => _activeDocument;

    public void Activate(object? document) => _activeDocument = document;

    public bool IsActive(object? document) =>
        document is not null && ReferenceEquals(_activeDocument, document);

    public void BeginClose(int documentId, object? document)
    {
        if (IsActive(document))
        {
            _closingDocument = document;
            _closingDocumentId = documentId;
        }
    }

    public bool CompleteClose(int documentId, bool succeeded)
    {
        if (_closingDocumentId != documentId)
        {
            return false;
        }

        var closedActiveDocument = succeeded && ReferenceEquals(_activeDocument, _closingDocument);
        _closingDocumentId = null;
        _closingDocument = null;
        if (closedActiveDocument)
        {
            _activeDocument = null;
        }

        return closedActiveDocument;
    }
}
