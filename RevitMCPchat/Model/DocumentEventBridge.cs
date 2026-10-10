using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using RevitMCPchat.Agent;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.Model;

public sealed class DocumentEventBridge : IDisposable
{
    private readonly UIControlledApplication _application;
    private readonly ActiveDocumentReader _reader;
    private readonly AgentPipeClient _pipeClient;
    private readonly ActiveDocumentReadHandler _readHandler;
    private readonly ExternalEvent _readActiveDocumentEvent;
    private readonly ActiveDocumentEventState _activeDocumentState = new();
    private readonly object _stateLock = new();
    private ActiveModelInfo _currentModel = new();
    private bool _disposed;

    public DocumentEventBridge(
        UIControlledApplication application,
        ActiveDocumentReader reader,
        AgentPipeClient pipeClient)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _readHandler = new ActiveDocumentReadHandler(reader);
        _readActiveDocumentEvent = ExternalEvent.Create(_readHandler);

        _application.ViewActivated += OnViewActivated;
        _application.ControlledApplication.DocumentSaved += OnDocumentSaved;
        _application.ControlledApplication.DocumentSavedAs += OnDocumentSavedAs;
        _application.ControlledApplication.DocumentClosing += OnDocumentClosing;
        _application.ControlledApplication.DocumentClosed += OnDocumentClosed;
    }

    public event EventHandler<ActiveModelInfo>? ActiveModelChanged;

    public ActiveModelInfo CurrentModel
    {
        get
        {
            lock (_stateLock)
            {
                return new ActiveModelInfo
                {
                    Identity = _currentModel.Identity,
                    DisplayName = _currentModel.DisplayName,
                    IsSaved = _currentModel.IsSaved,
                    IsOpen = _currentModel.IsOpen
                };
            }
        }
    }

    public async Task<ActiveModelInfo> ReadActiveModelAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DocumentEventBridge));
        }

        var activeDocument = await _readHandler.ReadAsync(_readActiveDocumentEvent, cancellationToken);
        _activeDocumentState.Activate(activeDocument.Document);
        SetCurrentModel(activeDocument.Model, notifyAgent: false);
        await NotifyActiveModelAsync(activeDocument.Model);
        return activeDocument.Model;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _application.ViewActivated -= OnViewActivated;
        _application.ControlledApplication.DocumentSaved -= OnDocumentSaved;
        _application.ControlledApplication.DocumentSavedAs -= OnDocumentSavedAs;
        _application.ControlledApplication.DocumentClosing -= OnDocumentClosing;
        _application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
        _readHandler.Dispose();
        _readActiveDocumentEvent.Dispose();
    }

    private void OnViewActivated(object? sender, ViewActivatedEventArgs args)
    {
        _activeDocumentState.Activate(args.Document);
        SetCurrentModel(_reader.ReadActiveDocument(args.Document));
    }

    private void OnDocumentSaved(object? sender, DocumentSavedEventArgs args)
    {
        if (_activeDocumentState.IsActive(args.Document))
        {
            SetCurrentModel(_reader.Read(args.Document));
        }
    }

    private void OnDocumentSavedAs(object? sender, DocumentSavedAsEventArgs args)
    {
        if (args.Document is null || !_activeDocumentState.IsActive(args.Document))
        {
            return;
        }

        var current = _reader.Read(args.Document);
        var previousIdentity = string.IsNullOrWhiteSpace(args.OriginalPath)
            ? _reader.GetUnsavedIdentity(args.Document)
            : ModelIdentity.ForSaved(args.OriginalPath);
        var transition = ModelIdentityChange.FromSave(previousIdentity, current.Identity);
        SetCurrentModel(current);

        if (transition?.ShouldBindExistingChats == true)
        {
            _ = NotifyModelSavedAsync(transition);
        }
    }

    private void OnDocumentClosing(object? sender, DocumentClosingEventArgs args) =>
        _activeDocumentState.BeginClose(args.DocumentId, args.Document);

    private void OnDocumentClosed(object? sender, DocumentClosedEventArgs args)
    {
        if (_activeDocumentState.CompleteClose(
            args.DocumentId,
            succeeded: args.Status == RevitAPIEventStatus.Succeeded))
        {
            SetCurrentModel(_reader.ReadActiveDocument(null));
        }
    }

    private async Task NotifyModelSavedAsync(ModelIdentityChange transition)
    {
        try
        {
            await _pipeClient.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.ModelSaved,
                PayloadJson = AgentPayloadJson.Serialize(transition)
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The next agent startup can still read the saved model identity from Revit.
        }
        catch (InvalidOperationException)
        {
            // Revit is shutting down while the model-save notification is in flight.
        }
        catch (Exception)
        {
            // Pipe errors must not interrupt Revit's save event.
        }
    }

    private async Task NotifyActiveModelAsync(ActiveModelInfo model)
    {
        try
        {
            await _pipeClient.SendAsync(new AgentCommand
            {
                Kind = AgentCommandKinds.ModelActive,
                PayloadJson = AgentPayloadJson.Serialize(model)
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The current model is re-sent when the pane next requests an active-model read.
        }
    }

    private void SetCurrentModel(ActiveModelInfo model, bool notifyAgent = true)
    {
        lock (_stateLock)
        {
            _currentModel = model;
        }

        if (notifyAgent)
        {
            _ = NotifyActiveModelAsync(model);
        }

        ActiveModelChanged?.Invoke(this, model);
    }

    private sealed class ActiveDocumentReadHandler : IExternalEventHandler, IDisposable
    {
        private readonly ActiveDocumentReader _reader;
        private readonly ConcurrentQueue<TaskCompletionSource<ActiveDocumentSnapshot>> _pending = new();
        private bool _disposed;

        public ActiveDocumentReadHandler(ActiveDocumentReader reader) => _reader = reader;

        public string GetName() => "Revit MCP Chat active model reader";

        public Task<ActiveDocumentSnapshot> ReadAsync(ExternalEvent externalEvent, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ActiveDocumentReadHandler));
            }

            var completion = new TaskCompletionSource<ActiveDocumentSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(completion);
            var request = externalEvent.Raise();
            if (request == ExternalEventRequest.Denied)
            {
                completion.TrySetException(new InvalidOperationException("Revit denied the active-model read request."));
            }

            return AwaitWithCancellationAsync(completion.Task, cancellationToken);
        }

        public void Execute(UIApplication application)
        {
            ActiveDocumentSnapshot activeDocument;
            try
            {
                var document = application.ActiveUIDocument?.Document;
                activeDocument = new ActiveDocumentSnapshot(
                    _reader.ReadActiveDocument(document),
                    document);
            }
            catch (Exception exception)
            {
                while (_pending.TryDequeue(out var failed))
                {
                    failed.TrySetException(exception);
                }

                return;
            }

            while (_pending.TryDequeue(out var completion))
            {
                completion.TrySetResult(activeDocument);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            while (_pending.TryDequeue(out var completion))
            {
                completion.TrySetCanceled();
            }
        }

        private static async Task<ActiveDocumentSnapshot> AwaitWithCancellationAsync(
            Task<ActiveDocumentSnapshot> task,
            CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return await task.ConfigureAwait(false);
            }

            var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellation))
            {
                if (task != await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false))
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await task.ConfigureAwait(false);
        }

        public sealed class ActiveDocumentSnapshot
        {
            public ActiveDocumentSnapshot(ActiveModelInfo model, Document? document)
            {
                Model = model;
                Document = document;
            }

            public ActiveModelInfo Model { get; }
            public Document? Document { get; }
        }
    }
}
