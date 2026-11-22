using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Eskrb.Api.Data;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Eskrb.Api.Tests;

/// <summary>Scriptable extractor standing in for the LLM.</summary>
public class FakeExtractor : IExtractor
{
    public Func<string, Dictionary<string, ExtractedValue>> Behavior { get; set; } = _ => [];
    public Task<Dictionary<string, ExtractedValue>> ExtractAsync(Protocol protocol, string text, CancellationToken ct) =>
        Task.FromResult(Behavior(text));
}

public class FakeNotifier : INotificationService
{
    public readonly List<(List<Guid> Users, PushMessage Message)> Sent = [];
    public Task<NotifyResult> SendToUserAsync(Guid userId, PushMessage m, CancellationToken ct = default) => SendToUsersAsync([userId], m, ct);
    public Task<NotifyResult> SendToUsersAsync(IEnumerable<Guid> userIds, PushMessage m, CancellationToken ct = default)
    {
        Sent.Add((userIds.ToList(), m));
        return Task.FromResult(new NotifyResult(userIds.Count(), userIds.Count(), 0, false, []));
    }
    public Task<Dictionary<string, PushReceipt>> GetReceiptsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.FromResult(new Dictionary<string, PushReceipt>());
}

public class FakeIssuer : ISttKeyIssuer
{
    public string? LastReference;
    public Task<TemporaryKey> IssueAsync(string clientReferenceId, CancellationToken ct)
    {
        LastReference = clientReferenceId;
        return Task.FromResult(new TemporaryKey("snx_temp_fake", DateTimeOffset.UtcNow.AddSeconds(60)));
    }
}

public class ApiFixture : WebApplicationFactory<Program>
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "eskrb-tests-" + Guid.NewGuid().ToString("N"));
    public readonly FakeExtractor Extractor = new();
    public readonly FakeIssuer Issuer = new();
    public readonly FakeNotifier Notifier = new();

    public ApiFixture()
    {
        Directory.CreateDirectory(Dir);
        // Program reads config before the factory's callbacks run, so use environment variables.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={Path.Combine(Dir, "t.db")}");
        Environment.SetEnvironmentVariable("Media__Path", Path.Combine(Dir, "media"));
        foreach (var u in new[] { "ana", "marko", "nina", "luka", "doctor", "senior", "admin" })
            Environment.SetEnvironmentVariable($"Seed__Tokens__{u}", $"tok-{u}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IExtractor>();
            s.AddSingleton<IExtractor>(Extractor);
            s.RemoveAll<INotificationService>();
            s.AddSingleton<INotificationService>(Notifier);
            s.RemoveAll<ISttKeyIssuer>();
            s.AddSingleton<ISttKeyIssuer>(Issuer);
        });
    }

    public HttpClient As(string user)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"tok-{user}");
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}

internal static class Rm
{
    public static void RemoveAll<T>(this IServiceCollection s)
    {
        foreach (var d in s.Where(d => d.ServiceType == typeof(T)).ToList()) s.Remove(d);
    }
}

[Collection("api")]
public class ApiTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string AnaEpisode = "00000000-0000-0000-0000-00000000e001";
    private const string MarkoEpisode = "00000000-0000-0000-0000-00000000e002";

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private Task<HttpResponseMessage> TextLog(string user, string text, string source = "typed", string episode = AnaEpisode) =>
        api.As(user).PostAsJsonAsync($"/episodes/{episode}/logs", new { type = "text", source, text });

    private static MultipartFormDataContent Photo(byte[] bytes, string contentType = "image/png")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { file, "file", "photo.png" } };
    }

    // ---------- text logs: extraction -> validation -> rules ----------

    [Fact]
    public async Task Text_log_with_extracted_urgent_value_flags_and_asks_for_112()
    {
        api.Extractor.Behavior = _ => new() { ["symptoms"] = new(J("[\"breathing_chest\"]"), "zmanjkuje sape") };
        var r = await TextLog("ana", "Že dva dni mi zmanjkuje sape in sem utrujena.", "dictated");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = await Body(r);
        Assert.Equal("call_112", b.GetProperty("patientAction").GetString());
        var flag = Assert.Single(b.GetProperty("flags").EnumerateArray());
        Assert.Equal("breathing", flag.GetProperty("ruleId").GetString());
        Assert.Equal("zmanjkuje sape", flag.GetProperty("quote").GetString());   // the patient's own words
        Assert.Equal("breathing_chest", b.GetProperty("extracted").GetProperty("symptoms")[0].GetString());
        Assert.False(b.GetProperty("extractionFailed").GetBoolean());
    }

    [Fact]
    public async Task Quote_that_is_not_in_the_text_falls_back_to_the_whole_text()
    {
        api.Extractor.Behavior = _ => new() { ["symptoms"] = new(J("[\"calf\"]"), "something invented") };
        var b = await Body(await TextLog("ana", "Meča me boli."));
        Assert.Equal("Meča me boli.", b.GetProperty("flags")[0].GetProperty("quote").GetString());
    }

    [Fact]
    public async Task Invalid_or_unknown_extracted_values_are_dropped_and_raise_no_flags()
    {
        api.Extractor.Behavior = _ => new()
        {
            ["pain"] = new(J("99"), "x"),               // out of range
            ["wound"] = new(J("\"purple\""), "x"),      // not an option
            ["mood"] = new(J("1"), "x"),                // unknown question
        };
        var b = await Body(await TextLog("ana", "x"));
        Assert.Empty(b.GetProperty("extracted").EnumerateObject());
        Assert.Empty(b.GetProperty("flags").EnumerateArray());
        Assert.Equal("none", b.GetProperty("patientAction").GetString());
    }

    [Fact]
    public async Task Extractor_failure_still_saves_the_log_and_says_so()
    {
        api.Extractor.Behavior = _ => throw new InvalidOperationException("llm down");
        var r = await TextLog("ana", "Boli me noga.");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = await Body(r);
        Assert.True(b.GetProperty("extractionFailed").GetBoolean());
        Assert.Empty(b.GetProperty("flags").EnumerateArray());
        api.Extractor.Behavior = _ => [];
    }

    [Fact]
    public async Task Text_log_validation()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await TextLog("ana", "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TextLog("ana", "hi", "spoken")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TextLog("ana", new string('x', 4001))).StatusCode);
        var wrongType = await api.As("ana").PostAsJsonAsync($"/episodes/{AnaEpisode}/logs", new { type = "voice", source = "typed", text = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
    }

    [Fact]
    public async Task Text_log_access_rules()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await TextLog("marko", "hi", episode: AnaEpisode)).StatusCode);   // not your episode
        Assert.Equal(HttpStatusCode.Forbidden, (await TextLog("doctor", "hi")).StatusCode);                      // staff cannot post logs
        var anon = await api.CreateClient().PostAsJsonAsync($"/episodes/{AnaEpisode}/logs", new { type = "text", source = "typed", text = "hi" });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
    }

    // ---------- Soniox temporary key ----------

    [Fact]
    public async Task Temporary_stt_key_is_for_patients_with_an_active_episode_and_rate_limited()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsync("/stt/temporary-key", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("doctor").PostAsync("/stt/temporary-key", null)).StatusCode);

        var ok = await api.As("marko").PostAsync("/stt/temporary-key", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("snx_temp_fake", body.GetProperty("apiKey").GetString());
        Assert.True(body.TryGetProperty("expiresAt", out _));
        Assert.True(Guid.TryParse(api.Issuer.LastReference, out _)); // bound to an episode id, not a name

        HttpStatusCode last = default;
        for (var n = 0; n < 8; n++) last = (await api.As("marko").PostAsync("/stt/temporary-key", null)).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    // ---------- error bodies ----------

    [Theory]
    [InlineData("/me", null, 401)]
    [InlineData("/users", "ana", 403)]
    [InlineData("/does-not-exist", "ana", 404)]
    public async Task Errors_always_carry_a_readable_detail(string path, string? as_, int status)
    {
        var client = as_ is null ? api.CreateClient() : api.As(as_);
        var r = await client.GetAsync(path);
        Assert.Equal(status, (int)r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.True(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Wrong_content_type_and_bad_json_say_what_is_wrong()
    {
        var wrong = await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/logs", new StringContent("x", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrong.StatusCode);
        Assert.Contains("Content-Type", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        var bad = await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/checkins", new StringContent("{bad", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()));
    }

    // ---------- photo questions in check-ins ----------

    [Fact]
    public async Task Photo_question_is_answered_with_a_file_part_and_shown_inside_the_check_in()
    {
        MultipartFormDataContent Form(string answers, string part)
        {
            var c = new MultipartFormDataContent { { new StringContent(answers), "answers" } };
            var f = new ByteArrayContent(Png); f.Headers.ContentType = new("image/png");
            c.Add(f, part, "w.png");
            return c;
        }
        // the wound photo is only asked when the wound shows a change (conditional question)
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/checkins", Form("{\"pain\":2,\"wound\":[\"none\"]}", "woundPhoto"))).StatusCode);
        var ok = await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/checkins", Form("{\"pain\":2,\"wound\":[\"redness\"]}", "woundPhoto"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var url = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("answers").GetProperty("woundPhoto").GetString()!;
        Assert.StartsWith("/media/", url);
        Assert.Equal(HttpStatusCode.OK, (await api.As("ana").GetAsync(url)).StatusCode);

        // not duplicated as a loose note in the timeline
        var tl = await (await api.As("ana").GetAsync($"/episodes/{AnaEpisode}/timeline")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(tl.EnumerateArray(), i => i.GetProperty("mediaUrl").GetString() == url);

        // wrong part name, JSON-supplied photo answer: rejected
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/checkins", Form("{\"pain\":2}", "pain"))).StatusCode);
        var json = await api.As("ana").PostAsJsonAsync($"/episodes/{AnaEpisode}/checkins", new { answers = new { wound = new[] { "redness" }, woundPhoto = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
    }

    [Fact]
    public async Task Check_in_free_text_fills_only_unanswered_questions_and_the_rules_still_decide()
    {
        api.Extractor.Behavior = _ => new()
        {
            ["symptoms"] = new(J("[\"calf\"]"), "boli me meča"),
            ["pain"] = new(J("9"), "huda bolečina"), // already answered directly: must not be overridden
        };
        var r = await api.As("ana").PostAsJsonAsync($"/episodes/{AnaEpisode}/checkins",
            new { answers = new { pain = 2, notes = "Danes me boli meča." } });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = await Body(r);
        Assert.Equal(2, b.GetProperty("answers").GetProperty("pain").GetInt32());
        Assert.Equal("calf", b.GetProperty("answers").GetProperty("symptoms")[0].GetString());
        Assert.Equal("contact_team", b.GetProperty("patientAction").GetString());   // calf = attention; only breathing/chest/fainting are urgent

        // extractor down: check-in is still saved, just without inferred values
        api.Extractor.Behavior = _ => throw new InvalidOperationException("llm down");
        var down = await api.As("ana").PostAsJsonAsync($"/episodes/{AnaEpisode}/checkins", new { answers = new { pain = 2, notes = "x" } });
        Assert.Equal(HttpStatusCode.Created, down.StatusCode);
        api.Extractor.Behavior = _ => [];
    }

    // ---------- triage, review, unread ----------

    [Fact]
    public async Task Triage_puts_each_seeded_patient_in_one_column_and_review_resolves_flags()
    {
        static List<string> Names(JsonElement col) => col.EnumerateArray().Select(c => c.GetProperty("patientName").GetString()!).ToList();
        var t = await Body(await api.As("doctor").GetAsync("/staff/triage"));
        Assert.Equal(["Marko Horvat"], Names(t.GetProperty("needsAttention")));
        Assert.Equal("urgent", t.GetProperty("needsAttention")[0].GetProperty("topFlag").GetProperty("severity").GetString());
        Assert.Equal(["Nina Kovač"], Names(t.GetProperty("missedCheckIn")));
        Assert.Equal(["Luka Krajnc"], Names(t.GetProperty("awaitingPatientReply")));
        Assert.Equal(["Ana Novak"], Names(t.GetProperty("stable")));

        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("marko").GetAsync("/staff/triage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/staff/triage")).StatusCode);

        var review = await Body(await api.As("doctor").PostAsync($"/staff/episodes/{MarkoEpisode}/review", null));
        Assert.True(review.GetProperty("flagsResolved").GetInt32() >= 2);
        Assert.True(review.GetProperty("checkIns").GetInt32() >= 1);
        var after = await Body(await api.As("doctor").GetAsync("/staff/triage"));
        Assert.Empty(after.GetProperty("needsAttention").EnumerateArray());
        Assert.Contains("Marko Horvat", Names(after.GetProperty("stable")));

        var tl = (await Body(await api.As("marko").GetAsync($"/episodes/{MarkoEpisode}/timeline"))).EnumerateArray();
        Assert.All(tl.Where(i => i.GetProperty("kind").GetString() != "message"), i => Assert.True(i.GetProperty("reviewed").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("doctor").PostAsync($"/staff/episodes/{Guid.NewGuid()}/review", null)).StatusCode);
    }

    [Fact]
    public async Task Patient_sees_unread_staff_messages_until_marking_them_read()
    {
        const string luka = "00000000-0000-0000-0000-00000000e004";
        var me = await Body(await api.As("luka").GetAsync("/me/episode"));
        Assert.Equal(1, me.GetProperty("unreadMessages").GetInt32());   // the seeded follow-up request

        Assert.Equal(HttpStatusCode.NoContent, (await api.As("luka").PostAsync($"/episodes/{luka}/read", null)).StatusCode);
        Assert.Equal(0, (await Body(await api.As("luka").GetAsync("/me/episode"))).GetProperty("unreadMessages").GetInt32());

        await Task.Delay(15);
        await api.As("doctor").PostAsJsonAsync($"/episodes/{luka}/messages", new { body = "Hvala, vse v redu." });
        Assert.Equal(1, (await Body(await api.As("luka").GetAsync("/me/episode"))).GetProperty("unreadMessages").GetInt32());
        // the patient's own messages never count, and other patients can't touch this episode
        await api.As("luka").PostAsJsonAsync($"/episodes/{luka}/messages", new { body = "Prav." });
        Assert.Equal(1, (await Body(await api.As("luka").GetAsync("/me/episode"))).GetProperty("unreadMessages").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("ana").PostAsync($"/episodes/{luka}/read", null)).StatusCode);
    }

    // ---------- episode notes ----------

    [Fact]
    public async Task Notes_are_staff_managed_and_private_ones_never_reach_the_patient()
    {
        const string luka = "00000000-0000-0000-0000-00000000e004";
        string url(string tail = "") => $"/episodes/{luka}/notes{tail}";

        var med = await Body(await api.As("doctor").PostAsJsonAsync(url(), new { kind = "medication", title = "Antibiotik", body = "Ena tableta zjutraj in zvečer." }));
        await api.As("doctor").PostAsJsonAsync(url(), new { kind = "advice", body = "Noge držite dvignjene." });
        await api.As("doctor").PostAsJsonAsync(url(), new { kind = "private", body = "Pazi: alergija na penicilin?" });

        // patient: only medication + advice, read-only; staff: all three
        var mine = (await Body(await api.As("luka").GetAsync(url()))).EnumerateArray().Select(n => n.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["medication", "advice"], mine);
        Assert.Equal(3, (await Body(await api.As("doctor").GetAsync(url()))).GetArrayLength());
        Assert.DoesNotContain("penicilin", await (await api.As("luka").GetAsync("/me/episode")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("luka").PostAsJsonAsync(url(), new { kind = "advice", body = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("ana").GetAsync(url())).StatusCode);
        Assert.Equal(2, (await Body(await api.As("luka").GetAsync("/me/episode"))).GetProperty("notes").GetArrayLength());

        // edit (kind, title, body) and delete
        var id = med.GetProperty("id").GetString();
        var edited = await Body(await api.As("doctor").PatchAsJsonAsync(url($"/{id}"), new { body = "Dve tableti zjutraj." }));
        Assert.Equal("Dve tableti zjutraj.", edited.GetProperty("body").GetString());
        Assert.Equal("Antibiotik", edited.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("doctor").PatchAsJsonAsync(url($"/{id}"), new { kind = "nonsense" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.As("doctor").DeleteAsync(url($"/{id}"))).StatusCode);
        Assert.Equal(1, (await Body(await api.As("luka").GetAsync("/me/episode"))).GetProperty("notes").GetArrayLength());
    }

    // ---------- conditional + multi-choice questions ----------

    [Fact]
    public async Task Conditional_and_multichoice_answers_are_validated_and_drive_the_flags()
    {
        async Task<HttpResponseMessage> CheckIn(string who, string ep, object answers) =>
            await api.As(who).PostAsJsonAsync($"/episodes/{ep}/checkins", new { answers });
        const string nina = "00000000-0000-0000-0000-00000000e003";

        // the protocol exposes labels, conditions and the safety alert for the phone
        var q = (await Body(await api.As("nina").GetAsync("/me/episode"))).GetProperty("protocol").GetProperty("questions").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
        Assert.Equal("multichoice", q["symptoms"].GetProperty("type").GetString());
        Assert.Equal("none", q["symptoms"].GetProperty("exclusiveOptions")[0].GetString());
        Assert.Equal("Vročina", q["symptoms"].GetProperty("labels").GetProperty("fever").GetString());
        Assert.Equal("breathing_chest", q["symptoms"].GetProperty("immediateAlert").GetProperty("options")[0].GetString());
        Assert.Equal("painWhen", q["painWhen"].GetProperty("id").GetString());
        Assert.Equal(5, q["painWhen"].GetProperty("showIf").GetProperty("value").GetInt32());
        Assert.Equal("Nisem izmeril", q["temperature"].GetProperty("skipLabel").GetString());

        // a follow-up that is not asked is rejected; once its condition holds it is accepted
        var notAsked = await CheckIn("nina", nina, new { pain = 2, painWhen = "walking" });
        Assert.Equal(HttpStatusCode.BadRequest, notAsked.StatusCode);
        Assert.Contains("not asked", (await Body(notAsked)).GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.Created, (await CheckIn("nina", nina, new { pain = 6, painWhen = "walking" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn("nina", nina, new { symptoms = new[] { "none" }, temperature = 37 })).StatusCode);   // asked only for "fever"
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn("nina", nina, new { feeling = "good", feelingCause = "pain" })).StatusCode);

        // multichoice rules: exclusive option, unknown option, duplicates, empty
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn("nina", nina, new { symptoms = new[] { "none", "fever" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn("nina", nina, new { symptoms = new[] { "nonsense" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn("nina", nina, new { symptoms = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn("nina", nina, new { symptoms = "fever" })).StatusCode);

        // fever + measured temperature: both rules fire (attention -> contact the team)
        var fever = await Body(await CheckIn("nina", nina, new { symptoms = new[] { "fever" }, temperature = 38.6 }));
        Assert.Equal("contact_team", fever.GetProperty("patientAction").GetString());
        Assert.Equal(["fever", "fever-reported"], fever.GetProperty("flags").EnumerateArray().Select(f => f.GetProperty("ruleId").GetString()!).Order().ToArray());
        Assert.Contains(fever.GetProperty("flags").EnumerateArray(), f => f.GetProperty("quote").GetString() == "symptoms: Vročina");   // labels in the flag quote

        // the safety answers are urgent (the phone interrupts and submits right away)
        var chest = await Body(await CheckIn("nina", nina, new { symptoms = new[] { "breathing_chest" } }));
        Assert.Equal("call_112", chest.GetProperty("patientAction").GetString());
        Assert.Equal("call_112", (await Body(await CheckIn("nina", nina, new { symptoms = new[] { "fainting", "calf" } }))).GetProperty("patientAction").GetString());
        Assert.Equal("none", (await Body(await CheckIn("nina", nina, new { symptoms = new[] { "none" }, wound = new[] { "none" } }))).GetProperty("patientAction").GetString());
    }

    // ---------- escalation ----------

    [Fact]
    public async Task Escalating_puts_an_item_in_the_seniors_needs_attention_until_resolved()
    {
        // other tests may leave open flags on Ana, so look at the card's escalation field rather than the column
        async Task<JsonElement?> AnaEscalation(string who)
        {
            var t = await Body(await api.As(who).GetAsync("/staff/triage"));
            foreach (var col in t.EnumerateObject().Where(c => c.Value.ValueKind == JsonValueKind.Array))
                foreach (var c in col.Value.EnumerateArray().Where(c => c.GetProperty("patientName").GetString() == "Ana Novak"))
                    return c.TryGetProperty("escalation", out var e) && e.ValueKind == JsonValueKind.Object ? e : null;
            return null;
        }
        var item = (await Body(await api.As("doctor").GetAsync($"/episodes/{AnaEpisode}/timeline"))).EnumerateArray()
            .First(i => i.GetProperty("kind").GetString() == "checkin");
        var body = new { sourceType = "checkin", sourceId = item.GetProperty("id").GetString(), comment = "Prosim, poglejte še vi." };

        var created = await api.As("doctor").PostAsJsonAsync($"/episodes/{AnaEpisode}/escalations", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var esc = await Body(created);
        Assert.Equal(2, esc.GetProperty("toLevel").GetInt32());
        Assert.Equal("Dr. Maja Zupan", esc.GetProperty("fromName").GetString());

        // senior sees Ana under needs attention with the comment; the sender's own board is unchanged
        Assert.Equal("Prosim, poglejte še vi.", (await AnaEscalation("senior"))!.Value.GetProperty("comment").GetString());
        var seniorCols = await Body(await api.As("senior").GetAsync("/staff/triage"));
        Assert.Contains(seniorCols.GetProperty("needsAttention").EnumerateArray(), c => c.GetProperty("patientName").GetString() == "Ana Novak");
        Assert.Null(await AnaEscalation("doctor"));

        var eid = esc.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("doctor").PostAsync($"/episodes/{AnaEpisode}/escalations/{eid}/resolve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.As("senior").PostAsync($"/episodes/{AnaEpisode}/escalations/{eid}/resolve", null)).StatusCode);
        Assert.Null(await AnaEscalation("senior"));

        // rules: nobody above the senior, admins have no level, patients can't, the item must belong to the episode
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("senior").PostAsJsonAsync($"/episodes/{AnaEpisode}/escalations", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("admin").PostAsJsonAsync($"/episodes/{AnaEpisode}/escalations", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("ana").PostAsJsonAsync($"/episodes/{AnaEpisode}/escalations", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("doctor").PostAsJsonAsync($"/episodes/{AnaEpisode}/escalations", new { sourceType = "log", sourceId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("doctor").PostAsJsonAsync($"/episodes/{AnaEpisode}/escalations", new { sourceType = "x", sourceId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Review_records_a_status_and_comment_and_whole_episodes_can_be_escalated()
    {
        const string nina = "00000000-0000-0000-0000-00000000e003";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("doctor").PostAsJsonAsync($"/staff/episodes/{nina}/review", new { status = "nonsense" })).StatusCode);
        var ok = await api.As("doctor").PostAsJsonAsync($"/staff/episodes/{nina}/review", new { status = "contacted", comment = "Poklical sem jo." });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var history = await Body(await api.As("doctor").GetAsync($"/staff/episodes/{nina}/reviews"));
        Assert.Equal("contacted", history[0].GetProperty("status").GetString());
        Assert.Equal("Poklical sem jo.", history[0].GetProperty("comment").GetString());
        Assert.Equal("Dr. Maja Zupan", history[0].GetProperty("reviewerName").GetString());

        var card = (await Body(await api.As("doctor").GetAsync("/staff/triage"))).EnumerateObject()
            .Where(c => c.Value.ValueKind == JsonValueKind.Array).SelectMany(c => c.Value.EnumerateArray())
            .First(c => c.GetProperty("patientName").GetString() == "Nina Kovač");
        Assert.Equal("contacted", card.GetProperty("lastReview").GetProperty("status").GetString());

        // a whole episode can be escalated (sourceId = the episode id)
        var esc = await api.As("doctor").PostAsJsonAsync($"/episodes/{nina}/escalations", new { sourceType = "episode", sourceId = nina, comment = "Ne odziva se." });
        Assert.Equal(HttpStatusCode.Created, esc.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("doctor").PostAsJsonAsync($"/episodes/{nina}/escalations", new { sourceType = "episode", sourceId = Guid.NewGuid() })).StatusCode);
    }

    // ---------- push device registration ----------

    [Fact]
    public async Task Push_tokens_register_move_between_users_and_unregister()
    {
        const string token = "ExponentPushToken[test-device-1]";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("ana").PostAsJsonAsync("/me/push-token", new { expoToken = "nope", platform = "ios" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("ana").PostAsJsonAsync("/me/push-token", new { expoToken = token, platform = "windows" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync("/me/push-token", new { expoToken = token, platform = "ios" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await api.As("ana").PostAsJsonAsync("/me/push-token", new { expoToken = token, platform = "ios", deviceId = "d1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.As("ana").PostAsJsonAsync("/me/push-token", new { expoToken = token, platform = "ios" })).StatusCode); // idempotent
        // same phone signs in as someone else: the token moves, it is never duplicated
        Assert.Equal(HttpStatusCode.NoContent, (await api.As("marko").PostAsJsonAsync("/me/push-token", new { expoToken = token, platform = "ios" })).StatusCode);

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EskrbDb>();
            var row = Assert.Single(db.PushDevices.Where(d => d.ExpoToken == token).ToList());
            Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000002"), row.UserId);
        }

        // ana can't unregister marko's device; marko can
        await api.As("ana").SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/me/push-token") { Content = JsonContent.Create(new { expoToken = token }) });
        using (var scope = api.Services.CreateScope())
            Assert.Single(scope.ServiceProvider.GetRequiredService<EskrbDb>().PushDevices.Where(d => d.ExpoToken == token).ToList());
        var del = await api.As("marko").SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/me/push-token") { Content = JsonContent.Create(new { expoToken = token }) });
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        using (var scope = api.Services.CreateScope())
            Assert.Empty(scope.ServiceProvider.GetRequiredService<EskrbDb>().PushDevices.Where(d => d.ExpoToken == token).ToList());
    }

    [Fact]
    public async Task Admin_can_list_devices_and_send_a_manual_notification()
    {
        await api.As("luka").PostAsJsonAsync("/me/push-token", new { expoToken = "ExponentPushToken[admin-list-1]", platform = "android" });
        var list = await Body(await api.As("admin").GetAsync("/admin/push-devices"));
        var luka = list.EnumerateArray().First(u => u.GetProperty("name").GetString() == "Luka Krajnc");
        Assert.Equal("android", luka.GetProperty("devices")[0].GetProperty("platform").GetString());
        Assert.DoesNotContain("ExponentPushToken", await (await api.As("admin").GetAsync("/admin/push-devices")).Content.ReadAsStringAsync());

        var before = api.Notifier.Sent.Count;
        var sent = await api.As("admin").PostAsJsonAsync("/admin/notifications", new { audience = "user", userId = "00000000-0000-0000-0000-000000000004", title = "Test", body = "Preizkus." });
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal(before + 1, api.Notifier.Sent.Count);
        Assert.Equal("Test", api.Notifier.Sent[^1].Message.Title);
        var everyone = await api.As("admin").PostAsJsonAsync("/admin/notifications", new { audience = "patients", title = "T", body = "B" });
        Assert.True((await Body(everyone)).GetProperty("attempted").GetInt32() >= 4);

        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("doctor").PostAsJsonAsync("/admin/notifications", new { audience = "patients", title = "T", body = "B" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("ana").GetAsync("/admin/push-devices")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("admin").PostAsJsonAsync("/admin/notifications", new { audience = "everyone", title = "T", body = "B" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("admin").PostAsJsonAsync("/admin/notifications", new { audience = "user", userId = Guid.NewGuid(), title = "T", body = "B" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("admin").PostAsJsonAsync("/admin/notifications", new { audience = "patients", title = "", body = "B" })).StatusCode);
    }

    // ---------- recovery twin (buddy) ----------

    [Fact]
    public async Task Seeded_pair_ana_and_luka_has_a_five_day_streak_and_a_conversation()
    {
        // luka sees ana: first name only, a 5-day streak (a gap a week ago), checked in today
        var raw = await (await api.As("luka").GetAsync("/me/buddy")).Content.ReadAsStringAsync();
        var view = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("active", view.GetProperty("status").GetString());
        Assert.Equal("Ana", view.GetProperty("buddy").GetProperty("firstName").GetString());
        Assert.Equal(5, view.GetProperty("buddy").GetProperty("streak").GetInt32());
        Assert.True(view.GetProperty("buddy").GetProperty("checkedInToday").GetBoolean());
        Assert.DoesNotContain("Novak", raw);

        var ana = await Body(await api.As("ana").GetAsync("/me/buddy"));
        Assert.Equal("Luka", ana.GetProperty("buddy").GetProperty("firstName").GetString());
        Assert.True(ana.GetProperty("unread").GetInt32() >= 1);           // luka's latest messages are unread
        var thread = (await Body(await api.As("ana").GetAsync("/me/buddy/messages"))).EnumerateArray().ToList();
        Assert.True(thread.Count >= 8);
        Assert.Contains(thread, m => m.GetProperty("fromMe").GetBoolean());
        Assert.Contains(thread, m => m.GetProperty("kind").GetString() == "cheer");
        Assert.Contains(thread, m => m.GetProperty("source").GetString() == "dictated");

        // ana is active (day 20 of 42) with the seeded history
        Assert.Equal(HttpStatusCode.OK, (await api.As("ana").GetAsync("/me/episode")).StatusCode);
    }

    [Fact]
    public async Task Buddy_pairing_messages_cheers_moderation_and_leaving()
    {
        string marko = MarkoEpisode;
        const string nina = "00000000-0000-0000-0000-00000000e003";

        // staff: candidates exclude already paired patients (the seeded ana+luka pair) and the patient themselves
        var cands = (await Body(await api.As("doctor").GetAsync($"/staff/buddies/candidates?episodeId={marko}"))).EnumerateArray().Select(c => c.GetProperty("patientName").GetString()).ToList();
        Assert.Contains("Nina Kovač", cands);
        Assert.DoesNotContain("Ana Novak", cands);
        Assert.DoesNotContain("Luka Krajnc", cands);
        Assert.DoesNotContain("Marko Horvat", cands);

        // pairing rules
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("doctor").PostAsJsonAsync("/staff/buddies", new { episodeAId = marko, episodeBId = nina, consentConfirmed = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("doctor").PostAsJsonAsync("/staff/buddies", new { episodeAId = marko, episodeBId = marko, consentConfirmed = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("doctor").PostAsJsonAsync("/staff/buddies", new { episodeAId = marko, episodeBId = AnaEpisode, consentConfirmed = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("marko").PostAsJsonAsync("/staff/buddies", new { episodeAId = marko, episodeBId = nina, consentConfirmed = true })).StatusCode);
        var made = await api.As("doctor").PostAsJsonAsync("/staff/buddies", new { episodeAId = marko, episodeBId = nina, consentConfirmed = true });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var pairId = (await Body(made)).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("doctor").PostAsJsonAsync("/staff/buddies", new { episodeAId = marko, episodeBId = nina, consentConfirmed = true })).StatusCode);

        // the buddy sees only a first name and a few non-health facts
        var raw = await (await api.As("marko").GetAsync("/me/buddy")).Content.ReadAsStringAsync();
        var view = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("active", view.GetProperty("status").GetString());
        Assert.Equal("Nina", view.GetProperty("buddy").GetProperty("firstName").GetString());
        Assert.DoesNotContain("Kovač", raw);
        Assert.Equal(["firstName", "dayNumber", "checkedInToday", "streak", "missedDays", "finished"],
            view.GetProperty("buddy").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Buddy_Catalogue.Count, view.GetProperty("cheers").GetArrayLength());

        // cheers: a fitting one works, one that does not fit is refused, unknown is a 400
        Assert.Equal(HttpStatusCode.Created, (await api.As("marko").PostAsJsonAsync("/me/buddy/cheers", new { cheerId = "fingers-crossed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("marko").PostAsJsonAsync("/me/buddy/cheers", new { cheerId = "streak-30" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("marko").PostAsJsonAsync("/me/buddy/cheers", new { cheerId = "nope" })).StatusCode);

        // text: typed + dictated, validation
        Assert.Equal(HttpStatusCode.Created, (await api.As("marko").PostAsJsonAsync("/me/buddy/messages", new { text = "Dober dan!", source = "dictated" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("marko").PostAsJsonAsync("/me/buddy/messages", new { text = "poglejte https://x.si" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("marko").PostAsJsonAsync("/me/buddy/messages", new { text = new string('x', 301) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("marko").PostAsJsonAsync("/me/buddy/messages", new { text = "ok", source = "audio" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync("/me/buddy")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("doctor").GetAsync("/me/buddy")).StatusCode);

        // nina reads, unread resets, can report; outsiders cannot report on this thread
        Assert.Equal(2, (await Body(await api.As("nina").GetAsync("/me/buddy"))).GetProperty("unread").GetInt32());
        var thread = (await Body(await api.As("nina").GetAsync("/me/buddy/messages"))).EnumerateArray().ToList();
        Assert.Equal(["cheer", "text"], thread.Select(m => m.GetProperty("kind").GetString()).ToArray());
        Assert.All(thread, m => Assert.False(m.GetProperty("fromMe").GetBoolean()));
        await api.As("nina").PostAsync("/me/buddy/read", null);
        Assert.Equal(0, (await Body(await api.As("nina").GetAsync("/me/buddy"))).GetProperty("unread").GetInt32());
        var textId = thread[1].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await api.As("nina").PostAsync($"/me/buddy/messages/{textId}/report", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("ana").PostAsync($"/me/buddy/messages/{textId}/report", null)).StatusCode);

        // staff see the thread + the report, hide the message; the patient no longer gets it
        var staffThread = (await Body(await api.As("doctor").GetAsync($"/staff/buddies/{pairId}/messages"))).EnumerateArray().ToList();
        Assert.True(staffThread[1].GetProperty("reported").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await api.As("doctor").DeleteAsync($"/staff/buddies/{pairId}/messages/{textId}")).StatusCode);
        Assert.Single((await Body(await api.As("nina").GetAsync("/me/buddy/messages"))).EnumerateArray());
        var list = (await Body(await api.As("doctor").GetAsync("/staff/buddies"))).EnumerateArray().First(p => p.GetProperty("id").GetString() == pairId);
        Assert.Equal(1, list.GetProperty("reported").GetInt32());

        // leaving ends it neutrally (no who/why to the other side) and blocks further messages
        Assert.Equal(HttpStatusCode.NoContent, (await api.As("nina").DeleteAsync("/me/buddy")).StatusCode);
        var after = await Body(await api.As("marko").GetAsync("/me/buddy"));
        Assert.Equal("ended", after.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("buddy").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("marko").PostAsJsonAsync("/me/buddy/messages", new { text = "Še tu?" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("doctor").DeleteAsync($"/staff/buddies/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.As("doctor").DeleteAsync($"/staff/buddies/{pairId}")).StatusCode);   // staff unpair is idempotent
    }

    private static readonly IReadOnlyList<Eskrb.Api.Rules.Cheer> Buddy_Catalogue = Eskrb.Api.Rules.Buddy.Catalogue;

    // ---------- charts ----------

    [Fact]
    public async Task Charts_give_numeric_series_with_rule_thresholds_and_per_day_activity()
    {
        var c = await Body(await api.As("doctor").GetAsync($"/episodes/{MarkoEpisode}/charts"));
        var vars = c.GetProperty("variables").EnumerateArray().ToDictionary(v => v.GetProperty("questionId").GetString()!);
        Assert.Contains("pain", vars.Keys);
        Assert.DoesNotContain("symptoms", vars.Keys);                       // multichoice answers are not charted
        var luka = await Body(await api.As("doctor").GetAsync("/episodes/00000000-0000-0000-0000-00000000e004/charts"));
        Assert.Contains(luka.GetProperty("variables").EnumerateArray(), v => v.GetProperty("questionId").GetString() == "temperature");   // the fever day
        var pain = vars["pain"];
        Assert.True(pain.GetProperty("points").GetArrayLength() >= 5);
        Assert.Contains(pain.GetProperty("rules").EnumerateArray(), r => r.GetProperty("op").GetString() == ">=" && r.GetProperty("value").GetDouble() == 8 && r.GetProperty("severity").GetString() == "attention");

        var days = c.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(c.GetProperty("dayNumber").GetInt32(), days.Count);
        Assert.True(days[^1].GetProperty("urgent").GetInt32() >= 1);       // marko's urgent flag today
        Assert.True(days[^1].GetProperty("checkIns").GetInt32() >= 1);

        // ana: a three-day gap about a week ago shows up as days without check-ins
        var ana = (await Body(await api.As("doctor").GetAsync($"/episodes/{AnaEpisode}/charts"))).GetProperty("days").EnumerateArray().ToList();
        var today = Eskrb.Api.Services.AppTime.Today;
        Assert.Equal(3, ana.Count(d => d.GetProperty("checkIns").GetInt32() == 0 && DateOnly.Parse(d.GetProperty("date").GetString()!) >= today.AddDays(-7) && DateOnly.Parse(d.GetProperty("date").GetString()!) <= today.AddDays(-5)));

        Assert.Equal(HttpStatusCode.Forbidden, (await api.As("marko").GetAsync($"/episodes/{MarkoEpisode}/charts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/episodes/{MarkoEpisode}/charts")).StatusCode);
    }

    // ---------- photo timeline ----------

    [Fact]
    public async Task Photo_timeline_groups_check_in_photos_by_question_and_loose_photos_separately()
    {
        // seeded ana has a wound photo every few days (synthetic images), oldest first
        var series = (await Body(await api.As("doctor").GetAsync($"/episodes/{AnaEpisode}/photos"))).EnumerateArray().ToList();
        var wound = series.First(x => x.GetProperty("questionId").GetString() == "woundPhoto");
        var photos = wound.GetProperty("photos").EnumerateArray().ToList();
        Assert.True(photos.Count >= 3);
        Assert.Equal(photos.Select(x => x.GetProperty("at").GetDateTime()).OrderBy(x => x).ToList(), photos.Select(x => x.GetProperty("at").GetDateTime()).ToList());
        Assert.Equal(HttpStatusCode.OK, (await api.As("doctor").GetAsync(photos[0].GetProperty("mediaUrl").GetString()!)).StatusCode);

        // the seed puts a photo story on at least three patients
        foreach (var episode in new[] { MarkoEpisode, "00000000-0000-0000-0000-00000000e003", "00000000-0000-0000-0000-00000000e004" })
        {
            var s = (await Body(await api.As("doctor").GetAsync($"/episodes/{episode}/photos"))).EnumerateArray().ToList();
            Assert.True(s.First(x => x.GetProperty("questionId").GetString() == "woundPhoto").GetProperty("photos").GetArrayLength() >= 3, episode);
        }

        // a loose photo note appears under "_other", never under the question
        var form = new MultipartFormDataContent();
        var png = new ByteArrayContent(Png); png.Headers.ContentType = new("image/png");
        form.Add(png, "photo", "p.png");
        Assert.Equal(HttpStatusCode.Created, (await api.As("luka").PostAsync("/episodes/00000000-0000-0000-0000-00000000e004/logs", form)).StatusCode);
        var lukaSeries = (await Body(await api.As("luka").GetAsync("/episodes/00000000-0000-0000-0000-00000000e004/photos"))).EnumerateArray().ToList();
        Assert.Contains(lukaSeries, x => x.GetProperty("questionId").GetString() == "_other");
        Assert.DoesNotContain(lukaSeries.Single(x => x.GetProperty("questionId").GetString() == "woundPhoto").GetProperty("photos").EnumerateArray(),
            x => x.GetProperty("checkInId").ValueKind == JsonValueKind.Null);   // question photos belong to check-ins; the loose one is only under _other

        // access: another patient cannot, staff outside the group cannot (404), anonymous is 401
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("marko").GetAsync($"/episodes/{AnaEpisode}/photos")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/episodes/{AnaEpisode}/photos")).StatusCode);
    }

    // ---------- photos ----------

    [Fact]
    public async Task Several_photos_in_one_request_become_one_log_each_and_bad_ones_save_nothing()
    {
        MultipartFormDataContent Two(byte[] second, string name2)
        {
            var c = new MultipartFormDataContent();
            var a = new ByteArrayContent(Png); a.Headers.ContentType = new("image/png");
            var b = new ByteArrayContent(second); b.Headers.ContentType = new("image/png");
            c.Add(a, "wound", "a.png"); c.Add(b, name2, "b.png");
            return c;
        }
        var ok = await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/logs", Two(Png, "other"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("logs").GetArrayLength());
        Assert.Equal(body.GetProperty("log").GetProperty("id").GetString(), body.GetProperty("logs")[0].GetProperty("id").GetString());

        var before = (await (await api.As("ana").GetAsync($"/episodes/{AnaEpisode}/timeline")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength();
        var bad = await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/logs", Two("<html/>"u8.ToArray(), "other"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var after = (await (await api.As("ana").GetAsync($"/episodes/{AnaEpisode}/timeline")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Photo_is_stored_and_served_only_to_people_allowed_to_see_the_episode()
    {
        var r = await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/logs", Photo(Png));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var log = (await Body(r)).GetProperty("log");
        Assert.Equal("photo", log.GetProperty("type").GetString());
        var url = log.GetProperty("mediaUrl").GetString()!;

        var owner = await api.As("ana").GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal("image/png", owner.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Png, await owner.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, (await api.As("doctor").GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.As("admin").GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("marko").GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Photo_must_really_be_an_image_whatever_the_content_type_claims()
    {
        var fake = Photo("<html>not an image</html>"u8.ToArray(), "image/jpeg");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/logs", fake)).StatusCode);
        var empty = Photo([]);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/logs", empty)).StatusCode);
    }

    [Fact]
    public async Task Audio_is_never_accepted()
    {
        var wav = Photo("RIFF\0\0\0\0WAVEfmt "u8.ToArray(), "audio/wav");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("ana").PostAsync($"/episodes/{AnaEpisode}/logs", wav)).StatusCode);
    }

    // ---------- messages + timeline ----------

    [Fact]
    public async Task Staff_and_patient_can_exchange_messages_and_the_timeline_shows_them_newest_first()
    {
        var staffMsg = await api.As("doctor").PostAsJsonAsync($"/episodes/{MarkoEpisode}/messages", new { body = "Kako ste danes?" });
        Assert.Equal(HttpStatusCode.Created, staffMsg.StatusCode);
        Assert.Equal("staff", (await Body(staffMsg)).GetProperty("fromRole").GetString());
        await Task.Delay(15);
        var reply = await api.As("marko").PostAsJsonAsync($"/episodes/{MarkoEpisode}/messages", new { body = "Bolje, hvala." });
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);

        var tl = await Body(await api.As("marko").GetAsync($"/episodes/{MarkoEpisode}/timeline"));
        var items = tl.EnumerateArray().ToList();
        Assert.Equal("Bolje, hvala.", items[0].GetProperty("body").GetString());
        Assert.Equal("patient", items[0].GetProperty("fromRole").GetString());
        Assert.Equal("Kako ste danes?", items[1].GetProperty("body").GetString());
    }

    [Fact]
    public async Task Message_validation_and_access()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("doctor").PostAsJsonAsync($"/episodes/{AnaEpisode}/messages", new { body = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.As("doctor").PostAsJsonAsync($"/episodes/{AnaEpisode}/messages", new { body = new string('x', 2001) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("marko").PostAsJsonAsync($"/episodes/{AnaEpisode}/messages", new { body = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsJsonAsync($"/episodes/{AnaEpisode}/messages", new { body = "hi" })).StatusCode);
    }

    [Fact]
    public async Task Timeline_hides_flags_from_the_patient_but_shows_them_to_staff()
    {
        api.Extractor.Behavior = _ => new() { ["symptoms"] = new(J("[\"calf\"]"), "meča") };
        await TextLog("nina", "Boli me meča.", episode: "00000000-0000-0000-0000-00000000e003");
        api.Extractor.Behavior = _ => [];
        await api.As("nina").PostAsJsonAsync("/episodes/00000000-0000-0000-0000-00000000e003/checkins", new { answers = new { pain = 2 } });

        var forStaff = (await Body(await api.As("doctor").GetAsync("/episodes/00000000-0000-0000-0000-00000000e003/timeline"))).EnumerateArray().ToList();
        var forPatient = (await Body(await api.As("nina").GetAsync("/episodes/00000000-0000-0000-0000-00000000e003/timeline"))).EnumerateArray().ToList();

        Assert.Contains(forStaff, i => i.GetProperty("kind").GetString() == "log"
            && i.GetProperty("flags").GetArrayLength() == 1 && i.GetProperty("extracted").ValueKind == JsonValueKind.Object);
        Assert.Contains(forStaff, i => i.GetProperty("kind").GetString() == "checkin");
        Assert.All(forPatient, i =>
        {
            Assert.Equal(JsonValueKind.Null, i.GetProperty("flags").ValueKind);
            Assert.Equal(JsonValueKind.Null, i.GetProperty("extracted").ValueKind);
        });
    }

    [Fact]
    public async Task Timeline_access_rules()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await api.As("marko").GetAsync($"/episodes/{AnaEpisode}/timeline")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.As("admin").GetAsync($"/episodes/{AnaEpisode}/timeline")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().GetAsync($"/episodes/{AnaEpisode}/timeline")).StatusCode);
    }
}

/// <summary>Own database: ending an episode must not affect the other tests.</summary>
[Collection("api")]
public class EndedEpisodeTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private Task<HttpResponseMessage> TextLog(string user, string text, string episode) =>
        api.As(user).PostAsJsonAsync($"/episodes/{episode}/logs", new { type = "text", source = "typed", text });

    private static MultipartFormDataContent Photo(byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", "photo.png" } };
    }

    [Fact]
    public async Task Ended_episode_blocks_patient_notes_and_replies_but_staff_can_still_write()
    {
        const string nina = "00000000-0000-0000-0000-00000000e003";
        Assert.Equal(HttpStatusCode.OK, (await api.As("doctor").PostAsync($"/episodes/{nina}/end", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await TextLog("nina", "hi", nina)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("nina").PostAsync($"/episodes/{nina}/logs", Photo(Png))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await api.As("nina").PostAsJsonAsync($"/episodes/{nina}/messages", new { body = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await api.As("doctor").PostAsJsonAsync($"/episodes/{nina}/messages", new { body = "Nasvidenje" })).StatusCode);
    }
}
