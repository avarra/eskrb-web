namespace Eskrb.Api.Domain;

/// <summary>One recovery period for one patient, owned by a doctor group.</summary>
public class Episode
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public User? Patient { get; set; }
    public required string ProtocolId { get; set; }
    public Protocol? Protocol { get; set; }
    public Guid DoctorGroupId { get; set; }
    public DoctorGroup? DoctorGroup { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}
