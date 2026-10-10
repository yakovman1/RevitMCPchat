using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RevitMcpChat.Agent.Providers;

internal static class HttpStreamingParser
{
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        var dataLines = new List<string>();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    yield return string.Join("\n", dataLines);
                    dataLines.Clear();
                }

                continue;
            }

            if (line[0] == ':' || !line.StartsWith("data:", System.StringComparison.Ordinal))
            {
                continue;
            }

            var data = line.Substring(5);
            if (data.StartsWith(" ", System.StringComparison.Ordinal))
            {
                data = data.Substring(1);
            }

            dataLines.Add(data);
        }

        if (dataLines.Count > 0)
        {
            yield return string.Join("\n", dataLines);
        }
    }
}
