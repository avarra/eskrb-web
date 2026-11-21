using System.Text.Json;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;

namespace Eskrb.Api.Tests;

public class RulesEngineTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, JsonElement> Answers(params (string, string)[] a) =>
        a.ToDictionary(x => x.Item1, x => J(x.Item2));

    private static ProtocolRule Rule(string id, string field, string op, string value, string sev = "attention") =>
        new() { Id = id, Field = field, Op = op, Value = J(value), Severity = sev };

    [Theory]
    [InlineData(">=", "38.0", "38.0", true)]
    [InlineData(">=", "38.0", "37.9", false)]
    [InlineData(">", "38.0", "38.0", false)]
    [InlineData("<=", "5", "5", true)]
    [InlineData("<", "5", "5", false)]
    [InlineData("==", "8", "8", true)]
    [InlineData("!=", "8", "8", false)]
    public void Numeric_operators(string op, string threshold, string answer, bool fires)
    {
        var hits = RulesEngine.Evaluate(Answers(("x", answer)), [Rule("r", "x", op, threshold)]);
        Assert.Equal(fires, hits.Count == 1);
    }

    [Fact]
    public void Integer_answer_compares_with_decimal_threshold()
    {
        var hits = RulesEngine.Evaluate(Answers(("temperature", "39")), [Rule("fever", "temperature", ">=", "38.0")]);
        Assert.Single(hits);
    }

    [Fact]
    public void String_equality()
    {
        var rules = new[] { Rule("d", "wound", "==", "\"discharge\"") };
        Assert.Single(RulesEngine.Evaluate(Answers(("wound", "\"discharge\"")), rules));
        Assert.Empty(RulesEngine.Evaluate(Answers(("wound", "\"dry\"")), rules));
    }

    [Fact]
    public void Boolean_equality()
    {
        var rules = new[] { Rule("calf", "calfPain", "==", "true", "urgent") };
        Assert.Single(RulesEngine.Evaluate(Answers(("calfPain", "true")), rules));
        Assert.Empty(RulesEngine.Evaluate(Answers(("calfPain", "false")), rules));
    }

    [Fact]
    public void Unanswered_field_does_not_fire()
    {
        Assert.Empty(RulesEngine.Evaluate(Answers(("pain", "1")), [Rule("fever", "temperature", ">=", "38")]));
    }

    [Fact]
    public void Type_mismatch_does_not_fire_or_throw()
    {
        Assert.Empty(RulesEngine.Evaluate(Answers(("pain", "\"high\"")), [Rule("p", "pain", ">=", "8")]));
        Assert.Empty(RulesEngine.Evaluate(Answers(("wound", "\"dry\"")), [Rule("w", "wound", ">=", "\"a\"")]));
        Assert.Empty(RulesEngine.Evaluate(Answers(("calfPain", "true")), [Rule("c", "calfPain", ">", "false")]));
    }

    [Fact]
    public void Unknown_operator_does_not_fire()
    {
        Assert.Empty(RulesEngine.Evaluate(Answers(("pain", "9")), [Rule("p", "pain", "~=", "8")]));
    }

    [Fact]
    public void Hit_carries_rule_severity_and_answer()
    {
        var hit = Assert.Single(RulesEngine.Evaluate(Answers(("pain", "9")), [Rule("high-pain", "pain", ">=", "8")]));
        Assert.Equal("high-pain", hit.RuleId);
        Assert.Equal("pain", hit.Field);
        Assert.Equal("attention", hit.Severity);
        Assert.Equal(9, hit.Answer.GetInt32());
    }

    [Fact]
    public void Multiple_rules_can_fire()
    {
        var rules = new[] { Rule("fever", "temperature", ">=", "38"), Rule("calf", "calfPain", "==", "true", "urgent") };
        var hits = RulesEngine.Evaluate(Answers(("temperature", "39"), ("calfPain", "true")), rules);
        Assert.Equal(2, hits.Count);
    }

    [Theory]
    [InlineData(new string[] { }, "none")]
    [InlineData(new[] { "attention" }, "contact_team")]
    [InlineData(new[] { "attention", "attention" }, "contact_team")]
    [InlineData(new[] { "urgent" }, "call_112")]
    [InlineData(new[] { "attention", "urgent" }, "call_112")]
    public void Patient_action_from_severities(string[] severities, string expected)
    {
        Assert.Equal(expected, PatientActions.From(severities));
    }
}
