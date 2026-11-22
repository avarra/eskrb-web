using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eskrb.Api.Tests;

public class ExpoPushServiceTests : IDisposable
{
    private sealed class Stub(Func<JsonNode, JsonNode> reply) : HttpMessageHandler
    {
        public readonly List<(Uri Url, string? Auth, JsonNode Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            Calls.Add((r.RequestUri!, r.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply(body).ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private readonly SqliteConnection conn = new("Data Source=:memory:");
    private readonly EskrbDb db;
    private readonly Guid user = Guid.NewGuid();

    public ExpoPushServiceTests()
    {
        conn.Open();
        db = new EskrbDb(new DbContextOptionsBuilder<EskrbDb>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        db.Users.Add(new User { Id = user, Name = "u", Role = UserRole.Patient });
        db.SaveChanges();
    }

    public void Dispose() { db.Dispose(); conn.Dispose(); }

    private void AddDevices(int n, string prefix = "ok")
    {
        for (var i = 0; i < n; i++)
            db.PushDevices.Add(new PushDevice { Id = Guid.NewGuid(), UserId = user, ExpoToken = $"ExponentPushToken[{prefix}{i}]", Platform = "ios", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
    }

    private ExpoPushService Service(Stub stub, bool? enabled, string? accessToken = null)
    {
        var cfg = new Dictionary<string, string?> { ["Notifications:Enabled"] = enabled?.ToString(), ["Expo:AccessToken"] = accessToken };
        return new ExpoPushService(new HttpClient(stub), new ConfigurationBuilder().AddInMemoryCollection(cfg).Build(), db, NullLogger<ExpoPushService>.Instance);
    }

    private static JsonNode AllOk(JsonNode body) =>
        new JsonObject { ["data"] = new JsonArray(body.AsArray().Select((_, i) => (JsonNode)new JsonObject { ["status"] = "ok", ["id"] = $"t{i}" }).ToArray()) };

    [Theory]
    [InlineData("ExponentPushToken[abc-123_X]", true)]
    [InlineData("ExpoPushToken[abc]", true)]
    [InlineData("ExponentPushToken[]", false)]
    [InlineData("fcm:abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Token_format(string? token, bool valid) => Assert.Equal(valid, ExpoPushService.IsExpoPushToken(token));

    [Fact]
    public async Task Kill_switch_sends_nothing_but_it_is_on_by_default()
    {
        AddDevices(2);
        var off = new Stub(AllOk);
        var r = await Service(off, enabled: false).SendToUserAsync(user, new PushMessage("t", "b"));
        Assert.True(r.Disabled);
        Assert.Empty(off.Calls);

        var unset = new Stub(AllOk);   // no Notifications:Enabled configured -> enabled
        Assert.False((await Service(unset, enabled: null).SendToUserAsync(user, new PushMessage("t", "b"))).Disabled);
        Assert.Single(unset.Calls);
    }

    [Fact]
    public async Task Sends_the_documented_payload_skips_invalid_tokens_and_drops_unregistered_devices()
    {
        AddDevices(2);
        db.PushDevices.Add(new PushDevice { Id = Guid.NewGuid(), UserId = user, ExpoToken = "garbage", Platform = "ios", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.SaveChanges();
        var stub = new Stub(body => new JsonObject
        {
            ["data"] = new JsonArray(
                new JsonObject { ["status"] = "ok", ["id"] = "ticket-1" },
                new JsonObject { ["status"] = "error", ["message"] = "gone", ["details"] = new JsonObject { ["error"] = "DeviceNotRegistered" } }),
        });

        var r = await Service(stub, enabled: true, accessToken: "secret-access").SendToUserAsync(
            user, new PushMessage("Novo sporočilo", "Imate novo sporočilo.", new() { ["episodeId"] = "e1" }, Badge: 1));

        Assert.Equal((2, 1, 1, false), (r.Attempted, r.Accepted, r.Removed, r.Disabled));
        Assert.Equal(["ticket-1"], r.TicketIds);
        Assert.Equal(2, await db.PushDevices.CountAsync());   // one removed (DeviceNotRegistered); the invalid-format row is left alone

        var call = Assert.Single(stub.Calls);
        Assert.Equal("https://exp.host/--/api/v2/push/send", call.Url.ToString());
        Assert.Equal("Bearer secret-access", call.Auth);
        var msg = call.Body[0]!;
        Assert.Equal("Novo sporočilo", (string?)msg["title"]);
        Assert.Equal("default", (string?)msg["sound"]);
        Assert.Equal("high", (string?)msg["priority"]);
        Assert.Equal("e1", (string?)msg["data"]!["episodeId"]);
        Assert.Equal(1, (int?)msg["badge"]);
    }

    [Fact]
    public async Task Batches_of_at_most_100_messages_per_request()
    {
        AddDevices(250);
        var stub = new Stub(AllOk);
        var r = await Service(stub, enabled: true).SendToUserAsync(user, new PushMessage("t", "b"));
        Assert.Equal([100, 100, 50], stub.Calls.Select(c => c.Body.AsArray().Count).ToArray());
        Assert.Equal(250, r.Accepted);
    }

    [Fact]
    public async Task Receipts_are_fetched_by_id_and_errors_are_reported()
    {
        var stub = new Stub(_ => new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["a"] = new JsonObject { ["status"] = "ok" },
                ["b"] = new JsonObject { ["status"] = "error", ["message"] = "m", ["details"] = new JsonObject { ["error"] = "MessageRateExceeded" } },
            },
        });
        var receipts = await Service(stub, enabled: true).GetReceiptsAsync(["a", "b"]);
        Assert.Equal("ok", receipts["a"].Status);
        Assert.Equal("MessageRateExceeded", receipts["b"].Error);
        Assert.EndsWith("/getReceipts", stub.Calls[0].Url.ToString());
    }
}
