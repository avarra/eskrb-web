using System.Text.Json;
using Eskrb.Api.Domain;

namespace Eskrb.Api.Rules;

public record RuleHit(string RuleId, string Field, string Severity, JsonElement Answer);

public static class PatientActions
{
    public const string None = "none";
    public const string ContactTeam = "contact_team";
    public const string Call112 = "call_112";

    /// <summary>Any urgent flag -> call_112; else any attention flag -> contact_team; else none.</summary>
    public static string From(IEnumerable<string> severities)
    {
        var list = severities.ToList();
        if (list.Contains("urgent")) return Call112;
        if (list.Contains("attention")) return ContactTeam;
        return None;
    }
}

/// <summary>
/// Deterministic red-flag rules. Pure: answers + protocol rules in, hits out. The LLM never decides this.
/// A rule whose field is unanswered, or whose operand types don't match the answer, does not fire.
/// </summary>
public static class RulesEngine
{
    public static List<RuleHit> Evaluate(IReadOnlyDictionary<string, JsonElement> answers, IEnumerable<ProtocolRule> rules)
    {
        var hits = new List<RuleHit>();
        foreach (var r in rules)
            if (answers.TryGetValue(r.Field, out var a) && Matches(a, r.Op, r.Value))
                hits.Add(new RuleHit(r.Id, r.Field, r.Severity, a));
        return hits;
    }

    /// <summary>One comparison of an answer against an expected value; shared by rules and question conditions.</summary>
    public static bool Matches(JsonElement answer, string op, JsonElement expected)
    {
        if (op == "in")
        {
            if (expected.ValueKind != JsonValueKind.Array) return false;
            var wanted = expected.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToHashSet();
            return answer.ValueKind switch
            {
                JsonValueKind.String => wanted.Contains(answer.GetString()),
                JsonValueKind.Array => answer.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && wanted.Contains(a.GetString())),
                _ => false,
            };
        }

        if (answer.ValueKind == JsonValueKind.Number && expected.ValueKind == JsonValueKind.Number)
            return Compare(answer.GetDouble().CompareTo(expected.GetDouble()), op);

        if (answer.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String)
            return op switch
            {
                "==" => answer.GetString() == expected.GetString(),
                "!=" => answer.GetString() != expected.GetString(),
                _ => false,
            };

        if (answer.ValueKind is JsonValueKind.True or JsonValueKind.False
            && expected.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return op switch
            {
                "==" => answer.GetBoolean() == expected.GetBoolean(),
                "!=" => answer.GetBoolean() != expected.GetBoolean(),
                _ => false,
            };

        return false;
    }

    private static bool Compare(int c, string op) => op switch
    {
        "==" => c == 0,
        "!=" => c != 0,
        ">" => c > 0,
        ">=" => c >= 0,
        "<" => c < 0,
        "<=" => c <= 0,
        _ => false,
    };
}
