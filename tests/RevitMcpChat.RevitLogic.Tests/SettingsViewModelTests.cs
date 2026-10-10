using System;
using System.Threading.Tasks;
using RevitMCPchat.ViewModels;
using RevitMcpChat.Contracts;
using Xunit;

namespace RevitMcpChat.RevitLogic.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task SavingKeyClearsVisibleInput()
    {
        var client = new ViewModelTestClient();
        client.ReplyFactory = command => new AgentReply
        {
            RequestId = command.RequestId,
            Kind = AgentReplyKinds.Response,
            PayloadJson = AgentPayloadJson.Serialize(new SettingsSnapshot
            {
                SelectedProviderId = "openai",
                SelectedModelId = "gpt-4o-mini",
                Providers =
                {
                    new ProviderSettingsDto
                    {
                        ProviderId = "openai",
                        DisplayName = "OpenAI",
                        ModelId = "gpt-4o-mini",
                        Credential = new CredentialAvailability
                        {
                            ProviderId = "openai",
                            HasUserKey = true,
                            EffectiveSource = "user"
                        }
                    }
                }
            })
        };
        var viewModel = new SettingsViewModel(client)
        {
            ProviderId = "openai",
            DisplayName = "OpenAI",
            ModelId = "gpt-4o-mini",
            ApiKeyInput = "disposable-test-key"
        };

        await viewModel.SaveAsync();

        Assert.Equal(string.Empty, viewModel.ApiKeyInput);
        var request = AgentPayloadJson.Deserialize<ProviderSettingsUpdate>(Assert.Single(client.Commands).PayloadJson);
        Assert.Equal("disposable-test-key", request.ApiKey);
        Assert.True(viewModel.HasUserKey);
    }

    [Fact]
    public void ChangingProviderClearsUnsubmittedKey()
    {
        var viewModel = new SettingsViewModel(new ViewModelTestClient())
        {
            ApiKeyInput = "key-for-openai"
        };

        viewModel.ProviderId = "deepseek";

        Assert.Equal(string.Empty, viewModel.ApiKeyInput);
    }

    [Fact]
    public void DefaultProvidersUseCurrentModelIds()
    {
        var viewModel = new SettingsViewModel(new ViewModelTestClient());

        Assert.Equal("gpt-5-mini", GetModel("openai"));
        Assert.Equal("claude-sonnet-5", GetModel("anthropic"));
        Assert.Equal("gemini-3.8-flash", GetModel("gemini"));
        Assert.Equal("deepseek-flash", GetModel("deepseek"));
        Assert.Equal("yandexgpt", GetModel("yandex-ai-studio"));
        Assert.Equal("GigaChat-2-Lite", GetModel("gigachat"));

        string GetModel(string providerId) =>
            Assert.Single(viewModel.Providers, item => item.ProviderId == providerId).ModelId;
    }
}
