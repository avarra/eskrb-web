using System.Text.Json;
using Eskrb.Api.Domain;

namespace Eskrb.Api.Rules;

/// <summary>Checks check-in answers against the protocol's questions. Pure.</summary>
public static class AnswerValidator
{
    /// <summary>
    /// Returns human-readable errors; empty = valid. Unanswered questions are allowed unless marked required.
    /// Photo questions are never answered in <paramref name="answers"/>; the files attached to the request are
    /// passed as <paramref name="photos"/> (question ids) and only count towards completeness.
    /// </summary>
    public static List<string> Validate(Protocol protocol, IReadOnlyDictionary<string, JsonElement> answers,
        IReadOnlySet<string>? photos = null)
    {
        photos ??= new HashSet<string>();
        var errors = new List<string>();
        if (answers.Count == 0 && photos.Count == 0) errors.Add("No answers provided.");

        var questions = protocol.Questions.ToDictionary(q => q.Id);
        foreach (var (id, a) in answers)
        {
            if (!questions.TryGetValue(id, out var q)) { errors.Add($"Unknown question '{id}'."); continue; }
            var error = CheckAnswer(q, a);
            if (error is not null) errors.Add($"'{id}' {error}.");
        }
        foreach (var q in protocol.Questions.Where(q => q.Required))
            if (!answers.ContainsKey(q.Id) && !photos.Contains(q.Id)) errors.Add($"'{q.Id}' is required.");
        return errors;
    }

    /// <summary>Null if the value is acceptable for the question, else the reason (e.g. "must be a number").</summary>
    public static string? CheckAnswer(ProtocolQuestion q, JsonElement a) => q.Type switch
    {
        "scale" => CheckScale(q, a),
        "number" => a.ValueKind == JsonValueKind.Number ? null : "must be a number",
        "choice" => a.ValueKind == JsonValueKind.String && q.Options?.Contains(a.GetString()!) == true
            ? null : $"must be one of: {string.Join(", ", q.Options ?? [])}",
        "boolean" => a.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "must be true or false",
        "text" => a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()) && a.GetString()!.Length <= 2000
            ? null : "must be non-empty text of at most 2000 characters",
        "photo" => "is a photo: send it as a multipart file part named after the question id",
        _ => $"unsupported question type '{q.Type}'",
    };

    private static string? CheckScale(ProtocolQuestion q, JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Number || !a.TryGetInt32(out var v)) return "must be a whole number";
        if (q.Min is { } min && v < min) return $"must be >= {min}";
        if (q.Max is { } max && v > max) return $"must be <= {max}";
        return null;
    }
}
