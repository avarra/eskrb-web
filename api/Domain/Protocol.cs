using System.Text.Json;

namespace Eskrb.Api.Domain;

/// <summary>Global (not per-hospital) recovery program. Questions and rules are data, not code.</summary>
public class Protocol
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public int DurationDays { get; set; }
    public List<ProtocolQuestion> Questions { get; set; } = [];
    public List<ProtocolRule> Rules { get; set; } = [];
}

public class ProtocolQuestion
{
    public required string Id { get; set; }
    /// <summary>scale | number | choice | multichoice (answer = array of option ids) | boolean | photo | text (freeform, max 2000 chars; the LLM extracts the other questions from it)</summary>
    public required string Type { get; set; }
    public required string Text { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public string? Unit { get; set; }
    /// <summary>Stable option ids for choice/multichoice questions (answers are stored and compared by id).</summary>
    public List<string>? Options { get; set; }
    /// <summary>Display text per option id (Slovenian, may include emoji). Clients show <c>labels[id] ?? id</c>.</summary>
    public Dictionary<string, string>? Labels { get; set; }
    /// <summary>multichoice: options that exclude every other option (e.g. "none of the above"); selecting one must be the only selection.</summary>
    public List<string>? ExclusiveOptions { get; set; }
    /// <summary>Only ask this question when the condition on an EARLIER answer is met (otherwise it is skipped and must not be answered).</summary>
    public QuestionCondition? ShowIf { get; set; }
    /// <summary>Selecting one of these options must interrupt the normal flow at once with a safety message (see <see cref="QuestionAlert"/>).</summary>
    public QuestionAlert? ImmediateAlert { get; set; }
    /// <summary>When set, the question may be skipped with this button text (e.g. "Preskoči", "Nisem izmeril"); a skipped question has no answer.</summary>
    public string? SkipLabel { get; set; }
    /// <summary>A check-in is rejected unless this question is answered (for photo questions: a file is attached).</summary>
    public bool Required { get; set; }
    /// <summary>True = purely rule-based question: the LLM never fills it from free text (it must be answered directly).</summary>
    public bool SkipExtraction { get; set; }
}

/// <summary>
/// Shows a question only when the answer to <see cref="Question"/> matches. <see cref="Op"/>: <c>== != &gt; &gt;= &lt; &lt;=</c>
/// (number/string/bool) or <c>in</c> (Value is an array: a choice answer is one of them, a multichoice answer shares any of them).
/// </summary>
public class QuestionCondition
{
    public required string Question { get; set; }
    public required string Op { get; set; }
    public JsonElement Value { get; set; }
}

/// <summary>Safety stop: when the patient picks one of <see cref="Options"/> the app must stop the questionnaire, submit what it has, and show this.</summary>
public class QuestionAlert
{
    public List<string> Options { get; set; } = [];
    public required string Title { get; set; }
    public required string Text { get; set; }
    /// <summary>The patient action the screen stands for (call_112 | contact_team); the server decides flags/patientAction from the rules, this is only for display.</summary>
    public string Action { get; set; } = "call_112";
}

public class ProtocolRule
{
    public required string Id { get; set; }
    public required string Field { get; set; }
    public required string Op { get; set; }
    /// <summary>Number, string or bool depending on the question type; an array for op <c>in</c>.</summary>
    public JsonElement Value { get; set; }
    /// <summary>attention | urgent</summary>
    public required string Severity { get; set; }
}
