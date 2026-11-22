using System.Text.Json;
using Eskrb.Api.Domain;

namespace Eskrb.Api.Services;

public static class EpisodeProtocol
{
    public const string MedsQuestionId = "medsTaken";

    /// <summary>
    /// The protocol as THIS episode sees it: when staff gave the patient medication instructions, the check-in also asks
    /// "have you taken your meds?" (a no raises an attention flag). A copy; the stored protocol is never modified.
    /// A protocol that defines its own <c>medsTaken</c> question is left alone.
    /// </summary>
    public static Protocol WithMedication(this Protocol p, bool hasMedicationNotes)
    {
        if (!hasMedicationNotes || p.Questions.Any(q => q.Id == MedsQuestionId)) return p;
        return new Protocol
        {
            Id = p.Id, Name = p.Name, DurationDays = p.DurationDays,
            Questions =
            [
                .. p.Questions,
                new ProtocolQuestion
                {
                    Id = MedsQuestionId, Type = "boolean", Text = "Ali ste danes vzeli zdravila, kot so vam predpisana?",
                    SkipExtraction = true,
                },
            ],
            Rules =
            [
                .. p.Rules,
                new ProtocolRule
                {
                    Id = "meds-missed", Field = MedsQuestionId, Op = "==",
                    Value = JsonSerializer.SerializeToElement(false), Severity = "attention",
                },
            ],
        };
    }
}
