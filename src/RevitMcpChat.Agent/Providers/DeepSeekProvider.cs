using System.Runtime.CompilerServices;
using RevitMcpChat.Agent.Chat;

namespace RevitMcpChat.Agent.Providers;

public sealed class DeepSeekProvider : IChatProvider
{
    private readonly OpenAiCompatibleProvider _inner;

    public DeepSeekProvider(HttpClient httpClient, Uri baseUri, string apiKey)
    {
        _inner = new OpenAiCompatibleProvider(httpClient, baseUri, apiKey);
    }

    public async IAsyncEnumerable<ProviderEvent> StreamAsync(
        ProviderRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _inner.StreamAsync(request, cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
