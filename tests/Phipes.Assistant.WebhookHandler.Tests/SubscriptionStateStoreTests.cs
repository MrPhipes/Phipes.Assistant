using Microsoft.Extensions.Logging.Abstractions;
using Phipes.Assistant.WebhookHandler.Configuration;
using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

// Tests del SubscriptionStateStore: load+save sobre archivos temporales reales (no
// mocks), porque el comportamiento depende del filesystem (atomic rename, archivo
// inexistente, archivo malformado).
public sealed class SubscriptionStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _statePath;
    private readonly SubscriptionStateStore _store;

    public SubscriptionStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"phipes-substate-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _statePath = Path.Combine(_tempDir, "subscriptions.json");
        Environment.SetEnvironmentVariable("HANDLER_SUBSCRIPTION_STATE_PATH", _statePath);
        _store = new SubscriptionStateStore(NullLogger<SubscriptionStateStore>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HANDLER_SUBSCRIPTION_STATE_PATH", null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SaveThenApply_RoundtripsIds()
    {
        var original = new[]
        {
            new SubscriptionDefinition { Id = "old-chat-id",  Resource = "/me/chats/getAllMessages",  Label = "chat", ExpirationMinutes = 55 },
            new SubscriptionDefinition { Id = "old-mail-id",  Resource = "/me/messages?$select=x",   Label = "mail", ExpirationMinutes = 4230 }
        };
        await _store.SaveAsync(original);

        var fresh = new[]
        {
            new SubscriptionDefinition { Id = "config-chat",  Resource = "/me/chats/getAllMessages",  Label = "chat", ExpirationMinutes = 55 },
            new SubscriptionDefinition { Id = "config-mail",  Resource = "/me/messages?$select=x",   Label = "mail", ExpirationMinutes = 4230 }
        };
        _store.ApplyPersistedIds(fresh);

        Assert.Equal("old-chat-id", fresh[0].Id);
        Assert.Equal("old-mail-id", fresh[1].Id);
    }

    [Fact]
    public void ApplyPersistedIds_NoFile_LeavesConfigUntouched()
    {
        var defs = new[]
        {
            new SubscriptionDefinition { Id = "from-config", Resource = "/x", Label = "chat" }
        };
        _store.ApplyPersistedIds(defs);
        Assert.Equal("from-config", defs[0].Id);
    }

    [Fact]
    public void ApplyPersistedIds_MalformedFile_LeavesConfigUntouched()
    {
        File.WriteAllText(_statePath, "this is not json {{{");
        var defs = new[]
        {
            new SubscriptionDefinition { Id = "config-chat", Resource = "/x", Label = "chat" }
        };
        _store.ApplyPersistedIds(defs);
        Assert.Equal("config-chat", defs[0].Id);
    }

    [Fact]
    public async Task ApplyPersistedIds_LabelNotInState_LeavesConfigUntouched()
    {
        // Persistir solo "chat"
        await _store.SaveAsync(new[]
        {
            new SubscriptionDefinition { Id = "persisted-chat", Resource = "/me/chats/getAllMessages", Label = "chat" }
        });

        // Config tiene chat + mail. Solo chat debe overridearse.
        var defs = new[]
        {
            new SubscriptionDefinition { Id = "config-chat", Resource = "/x", Label = "chat" },
            new SubscriptionDefinition { Id = "config-mail", Resource = "/y", Label = "mail" }
        };
        _store.ApplyPersistedIds(defs);
        Assert.Equal("persisted-chat", defs[0].Id);
        Assert.Equal("config-mail",    defs[1].Id);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingFile()
    {
        await _store.SaveAsync(new[] { new SubscriptionDefinition { Id = "v1", Label = "chat", Resource = "/x" } });
        await _store.SaveAsync(new[] { new SubscriptionDefinition { Id = "v2", Label = "chat", Resource = "/x" } });

        var defs = new[] { new SubscriptionDefinition { Id = "from-config", Label = "chat", Resource = "/x" } };
        _store.ApplyPersistedIds(defs);
        Assert.Equal("v2", defs[0].Id);
    }
}
