namespace Eskrb.Api.Domain;

public enum UserRole
{
    Patient,
    Caretaker,
    Staff,
    Admin,
}

public class User
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public UserRole Role { get; set; }
}
