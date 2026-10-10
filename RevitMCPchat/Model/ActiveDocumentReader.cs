using System;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.Model;

public sealed class ActiveDocumentReader
{
    private readonly ConditionalWeakTable<Document, DocumentSession> _sessions = new();

    public ActiveModelInfo Read(UIApplication application)
    {
        if (application is null)
        {
            throw new ArgumentNullException(nameof(application));
        }

        var document = application.ActiveUIDocument?.Document;
        return document is null ? NoDocument() : Read(document);
    }

    public ActiveModelInfo Read(Document document)
    {
        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        var location = GetModelLocation(document);
        if (!string.IsNullOrWhiteSpace(location))
        {
            return new ActiveModelInfo
            {
                Identity = ModelIdentity.ForSaved(location!),
                DisplayName = document.Title,
                IsSaved = true,
                IsOpen = true
            };
        }

        return new ActiveModelInfo
        {
            Identity = ModelIdentity.ForUnsaved(GetSessionId(document)),
            DisplayName = document.Title,
            IsSaved = false,
            IsOpen = true
        };
    }

    public string GetUnsavedIdentity(Document document) => ModelIdentity.ForUnsaved(GetSessionId(document));

    private Guid GetSessionId(Document document) => _sessions.GetValue(document, _ => new DocumentSession()).Id;

    private static string? GetModelLocation(Document document)
    {
        if (document.IsWorkshared)
        {
            try
            {
                var centralPath = document.GetWorksharingCentralModelPath();
                if (centralPath is not null)
                {
                    var userVisiblePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                    if (!string.IsNullOrWhiteSpace(userVisiblePath))
                    {
                        return userVisiblePath;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Fall back to the local document path when Revit has no central model path.
            }
        }

        return string.IsNullOrWhiteSpace(document.PathName) ? null : document.PathName;
    }

    private static ActiveModelInfo NoDocument() => new()
    {
        DisplayName = "No open Revit model",
        IsOpen = false,
        IsSaved = false
    };

    private sealed class DocumentSession
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
}
