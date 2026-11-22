namespace Eskrb.Api.Domain;

/// <summary>When the patient last opened their messages on an episode; staff messages after this are "unread".</summary>
public class EpisodeReadState
{
    public Guid EpisodeId { get; set; }
    public DateTime PatientLastReadAt { get; set; }
}
