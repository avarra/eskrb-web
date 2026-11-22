using System.Text.Json;
using Eskrb.Api.Domain;

namespace Eskrb.Api.Rules;

/// <summary>Conditional questions: is a question shown, given the answers so far? Pure.</summary>
public static class Conditions
{
    public static bool IsMet(QuestionCondition? c, IReadOnlyDictionary<string, JsonElement> answers) =>
        c is null || (answers.TryGetValue(c.Question, out var a) && RulesEngine.Matches(a, c.Op, c.Value));

    /// <summary>The questions the patient would be asked for these answers (a question with an unmet condition is not asked).</summary>
    public static List<ProtocolQuestion> Asked(Protocol protocol, IReadOnlyDictionary<string, JsonElement> answers) =>
        protocol.Questions.Where(q => IsMet(q.ShowIf, answers)).ToList();
}
