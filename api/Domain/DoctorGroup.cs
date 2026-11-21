namespace Eskrb.Api.Domain;

/// <summary>A hospital/wing-local team of staff that owns a set of episodes.</summary>
public class DoctorGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public List<DoctorGroupMember> Members { get; set; } = [];
}

/// <summary>
/// Dynamic membership of a staff user in a group. <see cref="Level"/> drives escalation:
/// 1 = first responder, higher = escalated to. A membership is active while now is within
/// [ActiveFrom, ActiveUntil) (null bounds are open), which also allows temporary substitutes.
/// </summary>
public class DoctorGroupMember
{
    public Guid Id { get; set; }
    public Guid GroupId { get; set; }
    public DoctorGroup? Group { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public int Level { get; set; } = 1;
    /// <summary>Free-text title for display, e.g. "Nurse", "Resident", "Consultant".</summary>
    public string? Title { get; set; }
    public DateTime? ActiveFrom { get; set; }
    public DateTime? ActiveUntil { get; set; }
}
