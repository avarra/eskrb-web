using System.Text.Json;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;
using Eskrb.Api.Services;

namespace Eskrb.Api.Tests;

public class EpisodeProtocolTests
{
    private static readonly Protocol P = new() { Id = "p", Name = "p", DurationDays = 1, Questions = [new() { Id = "pain", Type = "scale", Text = "", Min = 0, Max = 10 }] };

    [Fact]
    public void Without_medication_notes_the_protocol_is_untouched() => Assert.Same(P, P.WithMedication(false));

    [Fact]
    public void Medication_notes_add_a_meds_question_and_a_no_raises_an_attention_flag_without_changing_the_original()
    {
        var p = P.WithMedication(true);
        Assert.Contains(p.Questions, q => q.Id == "medsTaken" && q.Type == "boolean");
        Assert.Single(P.Questions);
        var no = RulesEngine.Evaluate(new Dictionary<string, JsonElement> { ["medsTaken"] = JsonSerializer.SerializeToElement(false) }, p.Rules);
        Assert.Equal("attention", Assert.Single(no).Severity);
        Assert.Empty(RulesEngine.Evaluate(new Dictionary<string, JsonElement> { ["medsTaken"] = JsonSerializer.SerializeToElement(true) }, p.Rules));
    }
}
