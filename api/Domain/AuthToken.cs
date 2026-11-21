namespace Eskrb.Api.Domain;

/// <summary>Opaque bearer token. Only the SHA-256 hash is stored.</summary>
public class AuthToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? Label { get; set; }
}
