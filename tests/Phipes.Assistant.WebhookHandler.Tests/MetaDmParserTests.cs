using Phipes.Assistant.WebhookHandler.Services;
using Xunit;

namespace Phipes.Assistant.WebhookHandler.Tests;

public class MetaDmParserTests
{
    private const string OwnIg = "17841443825354506";
    private const string OwnPage = "1182442031626253";

    private static string IgPayload(string messageJson, string sender = "998877", long ts = 1763900000000) => $$"""
        {"object":"instagram","entry":[{"id":"{{OwnIg}}","time":1,"messaging":[
          {"sender":{"id":"{{sender}}"},"recipient":{"id":"{{OwnIg}}"},"timestamp":{{ts}},"message":{{messageJson}}}
        ]}]}
        """;

    [Fact]
    public void Extracts_instagram_text_message()
    {
        var r = MetaDmParser.Extract(IgPayload("""{"mid":"m_1","text":"Hola, ¿cotizan apps?"}"""), OwnIg, OwnPage);

        var dm = Assert.Single(r);
        Assert.Equal("instagram", dm.Platform);
        Assert.Equal("m_1", dm.MessageId);
        Assert.Equal("998877", dm.SenderId);
        Assert.Equal("Hola, ¿cotizan apps?", dm.Text);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1763900000000).UtcDateTime, dm.SentUtc);
    }

    [Fact]
    public void Ignores_echo_of_our_own_reply()
    {
        var r = MetaDmParser.Extract(IgPayload("""{"mid":"m_2","text":"Gracias","is_echo":true}"""), OwnIg, OwnPage);
        Assert.Empty(r);
    }

    [Fact]
    public void Ignores_messages_sent_by_own_account()
    {
        var r = MetaDmParser.Extract(IgPayload("""{"mid":"m_3","text":"x"}""", sender: OwnIg), OwnIg, OwnPage);
        Assert.Empty(r);
    }

    [Fact]
    public void Ignores_deleted_messages_and_non_message_events()
    {
        Assert.Empty(MetaDmParser.Extract(IgPayload("""{"mid":"m_4","is_deleted":true}"""), OwnIg, OwnPage));

        var read = """{"object":"instagram","entry":[{"messaging":[{"sender":{"id":"1"},"read":{"mid":"m_5"}}]}]}""";
        Assert.Empty(MetaDmParser.Extract(read, OwnIg, OwnPage));
    }

    [Fact]
    public void Describes_attachments_without_text()
    {
        var r = MetaDmParser.Extract(IgPayload("""{"mid":"m_6","attachments":[{"type":"image","payload":{"url":"x"}}]}"""), OwnIg, OwnPage);
        Assert.Equal("(adjunto: image)", Assert.Single(r).Text);
    }

    [Fact]
    public void Maps_page_messaging_to_facebook_and_ignores_comments_payloads()
    {
        var messenger = """{"object":"page","entry":[{"messaging":[{"sender":{"id":"555"},"timestamp":1,"message":{"mid":"m_7","text":"hola"}}]}]}""";
        Assert.Equal("facebook", Assert.Single(MetaDmParser.Extract(messenger, OwnIg, OwnPage)).Platform);

        var comment = """{"object":"page","entry":[{"changes":[{"field":"feed","value":{"item":"comment","verb":"add"}}]}]}""";
        Assert.Empty(MetaDmParser.Extract(comment, OwnIg, OwnPage));
        Assert.Empty(MetaDmParser.Extract("no es json", OwnIg, OwnPage));
    }
}
