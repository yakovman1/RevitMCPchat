using System;

namespace RevitMcpChat.Agent.Providers;

public sealed class ProviderAuthenticationException : Exception
{
    public ProviderAuthenticationException(string message) : base(message) { }
}

public sealed class ProviderRequestException : Exception
{
    public ProviderRequestException(string message) : base(message) { }
}
