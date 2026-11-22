using System.Net.Http.Headers;
using System.Text.Json;

namespace Eskrb.Api.Integrations;

/// <summary>A short-lived, single-use Soniox key the phone uses to stream audio to Soniox directly. Audio never touches us.</summary>
public record TemporaryKey(string ApiKey, DateTimeOffset ExpiresAt);

public class SttNotConfiguredException() : Exception("Soniox:ApiKey is not set.");
public class SttUpstreamException(string message) : Exception(message);

public interface ISttKeyIssuer
{
    /// <param name="clientReferenceId">Tracking id bound to the key in Soniox usage logs (an episode id, never a name).</param>
    Task<TemporaryKey> IssueAsync(string clientReferenceId, CancellationToken ct);
}

/// <summary>
/// Calls Soniox <c>POST /v1/auth/temporary-api-key</c> with the long-lived key (config <c>Soniox:ApiKey</c>: user-secrets / env,
/// never committed, never logged). Base URL defaults to the non-EU endpoint; set <c>Soniox:BaseUrl</c> to https://api.eu.soniox.com for an EU key (the key and the phone's websocket must be in the same region).
/// Request/response bodies are never logged.
/// </summary>
public class SonioxKeyIssuer(HttpClient http, IConfiguration config) : ISttKeyIssuer
{
    public async Task<TemporaryKey> IssueAsync(string clientReferenceId, CancellationToken ct)
    {
        var apiKey = config["Soniox:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) throw new SttNotConfiguredException();

        var baseUrl = (config["Soniox:BaseUrl"] ?? "https://api.soniox.com").TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/auth/temporary-api-key");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // One lifetime (default 15 min) both for opening the stream and for how long it may run; still single-use.
        var ttl = int.TryParse(config["Soniox:KeyTtlSeconds"], out var t) && t is > 0 and <= 3600 ? t : 900;
        req.Content = JsonContent.Create(new
        {
            usage_type = "transcribe_websocket",
            expires_in_seconds = ttl,
            single_use = true,
            max_session_duration_seconds = ttl,
            client_reference_id = clientReferenceId,
        });

        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new SttUpstreamException($"Soniox unreachable: {ex.GetType().Name}"); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new SttUpstreamException($"Soniox returned {(int)res.StatusCode}");
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            var key = json.TryGetProperty("api_key", out var k) ? k.GetString() : null;
            if (string.IsNullOrEmpty(key)) throw new SttUpstreamException("Soniox response had no api_key");
            var expires = json.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(e.GetString(), out var at) ? at : DateTimeOffset.UtcNow.AddSeconds(ttl);
            return new TemporaryKey(key, expires);
        }
    }
}
