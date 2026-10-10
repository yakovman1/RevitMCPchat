using System.Text;

namespace RevitMcpChat.Agent.Console;

public static class ConsoleSecretReader
{
    public static string ReadFromConsole(string prompt)
    {
        if (System.Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Key entry requires an interactive console so the value is not echoed.");
        }

        return ReadSecret(System.Console.Out, () => System.Console.ReadKey(intercept: true), prompt);
    }

    public static string ReadSecret(TextWriter output, Func<ConsoleKeyInfo> readKey, string prompt)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(readKey);
        output.Write(prompt);
        output.Flush();

        var secret = new StringBuilder();
        while (true)
        {
            var key = readKey();
            if (key.Key == ConsoleKey.Enter)
            {
                output.WriteLine();
                return secret.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
            }
        }
    }
}
