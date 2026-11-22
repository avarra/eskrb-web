using System.Text.Json;
using Eskrb.Api.Domain;
using Eskrb.Api.Integrations;

namespace Eskrb.Api.Tests;

public class OpenAiExtractorTests
{
    private static readonly List<ProtocolQuestion> Questions =
    [
        new() { Id = "pain", Type = "scale", Text = "Pain?", Min = 0, Max = 10 },
        new() { Id = "temperature", Type = "number", Text = "Temp?", Unit = "°C" },
        new() { Id = "wound", Type = "choice", Text = "Wound?", Options = ["dry", "red", "discharge"] },
        new() { Id = "calfPain", Type = "boolean", Text = "Calf?" },
    ];

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void Request_is_strict_schema_with_every_question_required_nullable_and_nothing_stored()
    {
        var req = J(OpenAiExtractor.BuildRequest("m", "low", Questions, "boli me noga").ToJsonString());
        Assert.False(req.GetProperty("store").GetBoolean());
        Assert.Equal("low", req.GetProperty("reasoning").GetProperty("effort").GetString());
        var fmt = req.GetProperty("text").GetProperty("format");
        Assert.True(fmt.GetProperty("strict").GetBoolean());
        var schema = fmt.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(4, schema.GetProperty("required").GetArrayLength());
        var wound = schema.GetProperty("properties").GetProperty("wound").GetProperty("anyOf")[0]
            .GetProperty("properties").GetProperty("value").GetProperty("enum");
        Assert.Equal(3, wound.GetArrayLength());
        Assert.Contains("boli me noga", req.GetProperty("input")[1].GetProperty("content").GetString());
    }

    private static string Reply(string json) => JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new object[] { new { type = "reasoning" }, new { type = "message", content = new[] { new { type = "output_text", text = json } } } },
    });

    [Fact]
    public void Parse_reads_values_and_skips_nulls()
    {
        var r = OpenAiExtractor.Parse(J(Reply("""
            {"pain":{"value":7,"quote":"bolečina 7"},"temperature":null,"wound":{"value":"red","quote":"rdeča"},"calfPain":null}
            """)), Questions);
        Assert.Equal(2, r.Count);
        Assert.Equal(7, r["pain"].Value.GetInt32());
        Assert.Equal("rdeča", r["wound"].Quote);
    }

    [Fact]
    public void Refusal_gives_nothing_and_incomplete_throws()
    {
        var refusal = JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "refusal", refusal = "no" } } } } });
        Assert.Empty(OpenAiExtractor.Parse(J(refusal), Questions));
        Assert.Throws<InvalidOperationException>(() => OpenAiExtractor.Parse(J("""{"status":"incomplete","output":[]}"""), Questions));
    }

    [Fact]
    public void Multichoice_questions_become_an_array_of_enum_values_with_labels_in_the_description()
    {
        var q = new ProtocolQuestion
        {
            Id = "symptoms", Type = "multichoice", Text = "Simptomi?", Options = ["fever", "none"],
            Labels = new() { ["fever"] = "Vročina", ["none"] = "Nič od naštetega" }, ExclusiveOptions = ["none"],
        };
        var req = OpenAiExtractor.BuildRequest("m", "low", [q], "note");
        var prop = req["text"]!["format"]!["schema"]!["properties"]!["symptoms"]!;
        var value = prop["anyOf"]![0]!["properties"]!["value"]!;
        Assert.Equal("array", (string?)value["type"]);
        Assert.Equal(["fever", "none"], value["items"]!["enum"]!.AsArray().Select(x => (string?)x).ToArray());
        Assert.Contains("fever = Vročina", (string?)prop["description"]);
    }
}
