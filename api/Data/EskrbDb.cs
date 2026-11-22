using System.Text.Json;
using Eskrb.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Eskrb.Api.Data;

public class EskrbDb(DbContextOptions<EskrbDb> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<User> Users => Set<User>();
    public DbSet<AuthToken> AuthTokens => Set<AuthToken>();
    public DbSet<Protocol> Protocols => Set<Protocol>();
    public DbSet<DoctorGroup> DoctorGroups => Set<DoctorGroup>();
    public DbSet<DoctorGroupMember> DoctorGroupMembers => Set<DoctorGroupMember>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<Flag> Flags => Set<Flag>();
    public DbSet<Log> Logs => Set<Log>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<EpisodeNote> EpisodeNotes => Set<EpisodeNote>();
    public DbSet<Escalation> Escalations => Set<Escalation>();
    public DbSet<EpisodeReview> EpisodeReviews => Set<EpisodeReview>();
    public DbSet<EpisodeReadState> EpisodeReadStates => Set<EpisodeReadState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().Property(u => u.Role).HasConversion<string>();
        b.Entity<AuthToken>().HasIndex(t => t.TokenHash).IsUnique();

        b.Entity<Protocol>(p =>
        {
            p.Property(x => x.Questions).HasJson();
            p.Property(x => x.Rules).HasJson();
        });

        b.Entity<DoctorGroupMember>(m =>
        {
            m.HasOne(x => x.Group).WithMany(g => g.Members).HasForeignKey(x => x.GroupId);
            m.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            m.HasIndex(x => new { x.GroupId, x.UserId });
        });

        b.Entity<CheckIn>(c =>
        {
            c.Property(x => x.Answers).HasJson();
            c.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId);
            c.HasIndex(x => new { x.EpisodeId, x.Date });
        });

        b.Entity<Log>(l =>
        {
            l.Property(x => x.Extracted).HasJson();
            l.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId);
            l.HasIndex(x => new { x.EpisodeId, x.CreatedAt });
        });

        b.Entity<Message>(m =>
        {
            m.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId);
            m.HasOne(x => x.FromUser).WithMany().HasForeignKey(x => x.FromUserId);
            m.HasIndex(x => new { x.EpisodeId, x.CreatedAt });
        });

        b.Entity<EpisodeNote>(n =>
        {
            n.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId);
            n.HasIndex(x => x.EpisodeId);
        });

        b.Entity<Escalation>(x =>
        {
            x.HasOne(e => e.Episode).WithMany().HasForeignKey(e => e.EpisodeId);
            x.HasOne(e => e.FromUser).WithMany().HasForeignKey(e => e.FromUserId);
            x.HasIndex(e => new { e.EpisodeId, e.ResolvedAt });
        });

        b.Entity<EpisodeReview>(r =>
        {
            r.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId);
            r.HasOne(x => x.Reviewer).WithMany().HasForeignKey(x => x.ReviewedBy);
            r.HasIndex(x => new { x.EpisodeId, x.CreatedAt });
        });

        b.Entity<EpisodeReadState>().HasKey(x => x.EpisodeId);

        b.Entity<Flag>(f =>
        {
            f.HasOne(x => x.Episode).WithMany().HasForeignKey(x => x.EpisodeId);
            f.HasIndex(x => new { x.EpisodeId, x.ResolvedAt });
        });

        b.Entity<Episode>(e =>
        {
            e.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId);
            e.HasOne(x => x.Protocol).WithMany().HasForeignKey(x => x.ProtocolId);
            e.HasOne(x => x.DoctorGroup).WithMany().HasForeignKey(x => x.DoctorGroupId);
            e.HasIndex(x => x.PatientId);
            e.HasIndex(x => x.DoctorGroupId);
        });
    }

    internal static string Ser<T>(T v) => JsonSerializer.Serialize(v, Json);
    internal static T De<T>(string s) => JsonSerializer.Deserialize<T>(s, Json)!;
}

internal static class JsonPropertyExtensions
{
    /// <summary>Store a value as a JSON text column (portable across SQLite/Postgres).</summary>
    public static Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> HasJson<T>(
        this Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<T> prop) where T : class
    {
        prop.HasConversion(
            v => EskrbDb.Ser(v),
            s => EskrbDb.De<T>(s),
            new ValueComparer<T>(
                (a, b) => EskrbDb.Ser(a) == EskrbDb.Ser(b),
                v => EskrbDb.Ser(v).GetHashCode(),
                v => EskrbDb.De<T>(EskrbDb.Ser(v))));
        return prop;
    }
}
