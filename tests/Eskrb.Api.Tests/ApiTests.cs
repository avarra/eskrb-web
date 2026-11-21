using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

public class ApiFixture : WebApplicationFactory<Program>
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "eskrb-tests-" + Guid.NewGuid().ToString("N"));
    public readonly FakeExtractor Extractor = new();

    public ApiFixture()
    {
        Directory.CreateDirectory(Dir);
        // Program reads config before the factory's callbacks run, so use environment variables.
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"Data Source={Path.Combine(Dir, "t.db")}");
        Environment.SetEnvironmentVariable("Media__Path", Path.Combine(Dir, "media"));
        foreach (var u in new[] { "ana", "marko", "nina", "doctor", "admin" })
            Environment.SetEnvironmentVariable($"Seed__Tokens__{u}", $"tok-{u}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IExtractor>();
            s.AddSingleton<IExtractor>(Extractor);
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
        api.Extractor.Behavior = _ => new() { ["calfPain"] = new(J("true"), "boli me meča") };
        var r = await TextLog("ana", "Že dva dni me boli me meča in sem utrujena.", "dictated");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = await Body(r);
        Assert.Equal("call_112", b.GetProperty("patientAction").GetString());
        var flag = Assert.Single(b.GetProperty("flags").EnumerateArray());
        Assert.Equal("calf", flag.GetProperty("ruleId").GetString());
        Assert.Equal("boli me meča", flag.GetProperty("quote").GetString());   // the patient's own words
        Assert.True(b.GetProperty("extracted").GetProperty("calfPain").GetBoolean());
        Assert.False(b.GetProperty("extractionFailed").GetBoolean());
    }

    [Fact]
    public async Task Quote_that_is_not_in_the_text_falls_back_to_the_whole_text()
    {
        api.Extractor.Behavior = _ => new() { ["calfPain"] = new(J("true"), "something invented") };
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

    // ---------- photos ----------

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
        api.Extractor.Behavior = _ => new() { ["calfPain"] = new(J("true"), "meča") };
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
