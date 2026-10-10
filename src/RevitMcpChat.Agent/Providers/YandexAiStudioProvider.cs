using System.Runtime.CompilerServices;
using RevitMcpChat.Agent.Chat;

namespace RevitMcpChat.Agent.Providers;

public sealed class YandexAiStudioProvider : IChatProvider
{
    private readonly OpenAiCompatibleProvider _inner;
    private readonly string? _folderId;

    public YandexAiStudioProvider(HttpClient httpClient, Uri baseUri, string apiKey, string? folderId)
    {
        _inner = new OpenAiCompatibleProvider(httpClient, baseUri, apiKey, "Api-Key");
        _folderId = folderId;
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var modelId = request.ModelId;
        if (!modelId.StartsWith("gpt://", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_folderId))
            {
                throw new ProviderRequestException(
                    "Yandex AI Studio requires a folder_id or a full gpt:// model URI.");
            }

            modelId = $"gpt://{_folderId}/{modelId.TrimEnd('/')}/latest";
        }

        var mappedRequest = new ProviderRequest(modelId, request.Messages, request.Tools);
        await foreach (var item in _inner.StreamAsync(mappedRequest, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
