using System.Text;

namespace RevitMcpChat.Agent.History;

public sealed class SecretRedactor
{
    private readonly object _gate = new();
    private readonly HashSet<string> _secrets = new(StringComparer.Ordinal);

    public void RegisterSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            _secrets.Add(secret);
        }
    }

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string[] secrets;
        lock (_gate)
        {
            secrets = _secrets.OrderByDescending(secret => secret.Length).ToArray();
        }

        foreach (var secret in secrets)
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }
}
