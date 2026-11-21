using System.Text.Json;
using Eskrb.Api.Domain;
using Eskrb.Api.Rules;

namespace Eskrb.Api.Tests;

public class AnswerValidatorTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly Protocol Protocol = new()
    {
        Id = "p", Name = "p", DurationDays = 10,
        Questions =
        [
            new() { Id = "pain", Type = "scale", Text = "", Min = 0, Max = 10 },
            new() { Id = "temperature", Type = "number", Text = "" },
            new() { Id = "wound", Type = "choice", Text = "", Options = ["dry", "red", "discharge"] },
            new() { Id = "calfPain", Type = "boolean", Text = "" },
        ],
    };

    private static List<string> Validate(params (string, string)[] a) =>
        AnswerValidator.Validate(Protocol, a.ToDictionary(x => x.Item1, x => J(x.Item2)));

    [Fact]
    public void Valid_partial_answers_pass()
        => Assert.Empty(Validate(("pain", "3"), ("temperature", "36.6"), ("wound", "\"dry\""), ("calfPain", "false")));

    [Fact]
    public void Empty_is_rejected() => Assert.Single(Validate());

    [Fact]
    public void Unknown_question_is_rejected() => Assert.Single(Validate(("mood", "1")));

    [Theory]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("2.5")]
    [InlineData("\"3\"")]
    public void Scale_must_be_a_whole_number_in_range(string value) => Assert.Single(Validate(("pain", value)));

    [Theory]
    [InlineData("0")]
    [InlineData("10")]
    public void Scale_bounds_are_inclusive(string value) => Assert.Empty(Validate(("pain", value)));

    [Fact]
    public void Number_must_be_numeric() => Assert.Single(Validate(("temperature", "\"hot\"")));

    [Theory]
    [InlineData("\"purple\"")]
    [InlineData("1")]
    public void Choice_must_be_a_listed_option(string value) => Assert.Single(Validate(("wound", value)));

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public void Boolean_must_be_a_boolean(string value) => Assert.Single(Validate(("calfPain", value)));

    [Fact]
    public void Every_invalid_answer_is_reported()
        => Assert.Equal(3, Validate(("pain", "99"), ("wound", "\"x\""), ("mood", "1")).Count);
}
