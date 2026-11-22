using System.Text.Json;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;

namespace Eskrb.Api.Tests;

public class ConditionsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static Dictionary<string, JsonElement> A(params (string, string)[] a) => a.ToDictionary(x => x.Item1, x => J(x.Item2));
    private static QuestionCondition C(string q, string op, string value) => new() { Question = q, Op = op, Value = J(value) };

    [Fact]
    public void No_condition_means_always_asked() => Assert.True(Conditions.IsMet(null, A()));

    [Fact]
    public void Numeric_thresholds_below_or_over()
    {
        Assert.True(Conditions.IsMet(C("pain", ">=", "5"), A(("pain", "5"))));
        Assert.False(Conditions.IsMet(C("pain", ">=", "5"), A(("pain", "4"))));
        Assert.True(Conditions.IsMet(C("pain", "<", "3"), A(("pain", "2"))));
    }

    [Fact]
    public void In_matches_a_choice_answer_or_any_selected_option_of_a_multichoice()
    {
        var c = C("feeling", "in", "[\"bad\",\"very_bad\"]");
        Assert.True(Conditions.IsMet(c, A(("feeling", "\"bad\""))));
        Assert.False(Conditions.IsMet(c, A(("feeling", "\"good\""))));

        var m = C("wound", "in", "[\"redness\",\"discharge\"]");
        Assert.True(Conditions.IsMet(m, A(("wound", "[\"redness\"]"))));
        Assert.True(Conditions.IsMet(m, A(("wound", "[\"discharge\"]"))));
        Assert.False(Conditions.IsMet(m, A(("wound", "[\"none\"]"))));
    }

    [Fact]
    public void A_missing_parent_answer_means_not_asked() => Assert.False(Conditions.IsMet(C("pain", ">=", "5"), A()));

    [Fact]
    public void Multichoice_validation_and_conditional_enforcement()
    {
        var p = new Protocol
        {
            Id = "p", Name = "p", DurationDays = 1,
            Questions =
            [
                new() { Id = "symptoms", Type = "multichoice", Text = "", Options = ["fever", "calf", "none"], ExclusiveOptions = ["none"] },
                new() { Id = "temp", Type = "number", Text = "", ShowIf = C("symptoms", "in", "[\"fever\"]"), Required = true },
            ],
        };
        Assert.Empty(AnswerValidator.Validate(p, A(("symptoms", "[\"fever\",\"calf\"]"), ("temp", "38.5"))));
        Assert.Single(AnswerValidator.Validate(p, A(("symptoms", "[\"fever\"]"))));                         // temp required once asked
        Assert.Empty(AnswerValidator.Validate(p, A(("symptoms", "[\"calf\"]"))));                           // not asked, so not required
        Assert.Single(AnswerValidator.Validate(p, A(("symptoms", "[\"calf\"]"), ("temp", "38"))));          // answered but not asked
        Assert.Single(AnswerValidator.Validate(p, A(("symptoms", "[\"none\",\"fever\"]"), ("temp", "38"))));
        Assert.Single(AnswerValidator.Validate(p, A(("symptoms", "[\"fever\",\"fever\"]"), ("temp", "38"))));
        Assert.Single(AnswerValidator.Validate(p, A(("symptoms", "[]"))));
    }

    [Fact]
    public void Rules_can_use_in_on_choices_and_multichoices()
    {
        var rule = new ProtocolRule { Id = "r", Field = "symptoms", Op = "in", Value = J("[\"fainting\",\"breathing_chest\"]"), Severity = "urgent" };
        Assert.Single(RulesEngine.Evaluate(A(("symptoms", "[\"fainting\"]")), [rule]));
        Assert.Empty(RulesEngine.Evaluate(A(("symptoms", "[\"fever\"]")), [rule]));
    }
}
