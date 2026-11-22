namespace Eskrb.Api.Domain;

public static class NoteKinds
{
    /// <summary>How to take the medication. Shown to the patient; makes the check-in ask "have you taken your meds?".</summary>
    public const string Medication = "medication";
    /// <summary>General advice for the patient. Shown to the patient.</summary>
    public const string Advice = "advice";
    /// <summary>Staff-only working note. NEVER sent to the patient.</summary>
    public const string Private = "private";

    public static readonly string[] All = [Medication, Advice, Private];
    public static bool PatientVisible(string kind) => kind != Private;
}

/// <summary>A note staff attach to one episode.</summary>
public class EpisodeNote
{
    public Guid Id { get; set; }
    public Guid EpisodeId { get; set; }
    public Episode? Episode { get; set; }
    /// <summary>medication | advice | private (see <see cref="NoteKinds"/>)</summary>
    public required string Kind { get; set; }
    public string? Title { get; set; }
    public required string Body { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
