using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eskrb.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Eskrb.Api.Integrations;

/// <summary>
/// Expo Push Service client (https://docs.expo.dev/push-notifications/sending-notifications/).
/// Config (user-secrets/env, never git): <c>Notifications:Enabled</c> (default true; set false as a kill switch: nothing is sent),
/// <c>Expo:PushUrl</c> (default the public endpoint), <c>Expo:AccessToken</c> (optional, "enhanced push security").
/// Never logs tokens, titles or bodies.
/// </summary>
public class ExpoPushService(HttpClient http, IConfiguration config, EskrbDb db, ILogger<ExpoPushService> logger) : INotificationService
{
    public const int MaxPerRequest = 100;   // Expo limit per send request
    public const int MaxReceiptIds = 1000;  // Expo limit per getReceipts request
    private static readonly Regex TokenFormat = new(@"^Expo(nent)?PushToken\[[^\]\s]+\]$", RegexOptions.Compiled);

    public static bool IsExpoPushToken(string? token) => token is not null && TokenFormat.IsMatch(token);

    private string BaseUrl => config["Expo:PushUrl"] ?? "https://exp.host/--/api/v2/push";

    public Task<NotifyResult> SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct = default) =>
        SendToUsersAsync([userId], message, ct);

    public async Task<NotifyResult> SendToUsersAsync(IEnumerable<Guid> userIds, PushMessage message, CancellationToken ct = default)
    {
        if (!config.GetValue("Notifications:Enabled", true))
        {
            logger.LogInformation("Push notifications are switched off (Notifications:Enabled=false); nothing sent");
            return new NotifyResult(0, 0, 0, true, []);
        }

        var ids = userIds.Distinct().ToList();
        var devices = await db.PushDevices.Where(d => ids.Contains(d.UserId)).ToListAsync(ct);
        var valid = devices.Where(d => IsExpoPushToken(d.ExpoToken)).ToList();

        int accepted = 0, removed = 0;
        var tickets = new List<string>();
        foreach (var chunk in valid.Chunk(MaxPerRequest))
        {
            var response = await PostAsync("send", new JsonArray(chunk.Select(d => (JsonNode)BuildMessage(d.ExpoToken, message)).ToArray()), ct);
            var results = response?["data"]?.AsArray();
            if (results is null) { logger.LogWarning("Expo push send returned no tickets"); continue; }

            for (var i = 0; i < results.Count && i < chunk.Length; i++)
            {
                var t = results[i]!;
                if (t["status"]?.GetValue<string>() == "ok")
                {
                    accepted++;
                    if (t["id"]?.GetValue<string>() is { } id) tickets.Add(id);
                }
                else if (t["details"]?["error"]?.GetValue<string>() == "DeviceNotRegistered")
                {
                    db.PushDevices.Remove(chunk[i]); // the app was uninstalled or the token rotated: stop sending to it
                    removed++;
                }
                else logger.LogWarning("Expo push ticket error: {Error}", t["details"]?["error"]?.GetValue<string>() ?? "unknown");
            }
        }
        if (removed > 0) await db.SaveChangesAsync(ct);
        return new NotifyResult(valid.Count, accepted, removed, false, tickets);
    }

    public async Task<Dictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyCollection<string> ticketIds, CancellationToken ct = default)
    {
        var all = new Dictionary<string, PushReceipt>();
        if (!config.GetValue("Notifications:Enabled", true)) return all;
        foreach (var chunk in ticketIds.Chunk(MaxReceiptIds))
        {
            var response = await PostAsync("getReceipts", new JsonObject { ["ids"] = new JsonArray(chunk.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) }, ct);
            if (response?["data"] is not JsonObject data) continue;
            foreach (var (id, r) in data)
                all[id] = new PushReceipt(r?["status"]?.GetValue<string>() ?? "error", r?["details"]?["error"]?.GetValue<string>(), r?["message"]?.GetValue<string>());
        }
        return all;
    }

    /// <summary>The wire format for one message (public so it can be unit-tested).</summary>
    public static JsonObject BuildMessage(string token, PushMessage m)
    {
        var o = new JsonObject
        {
            ["to"] = token, ["title"] = m.Title, ["body"] = m.Body, ["sound"] = "default", ["priority"] = "high",
        };
        if (m.Data is not null) o["data"] = JsonSerializer.SerializeToNode(m.Data);
        if (m.Badge is { } b) o["badge"] = b;
        return o;
    }

    private async Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl.TrimEnd('/')}/{path}") { Content = JsonContent.Create(body) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (config["Expo:AccessToken"] is { Length: > 0 } token) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            // 429 / 5xx: the caller may retry later with backoff; 400 means our payload is wrong. Never log the body (patient context).
            logger.LogWarning("Expo push {Path} failed with HTTP {Status}", path, (int)res.StatusCode);
            return null;
        }
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }
}
