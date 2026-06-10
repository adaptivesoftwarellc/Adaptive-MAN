using Microsoft.EntityFrameworkCore;
using Observability.Domain.Alerting;
using Observability.Domain.Applications;
using Observability.Domain.Audit;
using Observability.Domain.Identity;
using Observability.Domain.Telemetry;
using DomainApplication = Observability.Domain.Applications.Application;

namespace Observability.Infrastructure.Persistence;

public class ObservabilityDbContext : DbContext
{
    public ObservabilityDbContext(DbContextOptions<ObservabilityDbContext> options) : base(options) { }

    public DbSet<DomainApplication> Applications => Set<DomainApplication>();
    public DbSet<AppEnvironment> AppEnvironments => Set<AppEnvironment>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<ErrorRecord> Errors => Set<ErrorRecord>();
    public DbSet<SafetyViolation> SafetyViolations => Set<SafetyViolation>();
    public DbSet<BackgroundJobFailure> BackgroundJobFailures => Set<BackgroundJobFailure>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserApplicationAssignment> UserApplicationAssignments => Set<UserApplicationAssignment>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<FiredAlert> FiredAlerts => Set<FiredAlert>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DomainApplication>(e =>
        {
            e.ToTable("Applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Description).HasMaxLength(1000);
        });

        b.Entity<AppEnvironment>(e =>
        {
            e.ToTable("AppEnvironments");
            e.HasKey(x => x.Id);
            e.Property(x => x.EnvironmentName).HasMaxLength(64).IsRequired();
            e.Property(x => x.AllowedOriginsJson).HasColumnType("nvarchar(max)");
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentName }).IsUnique();
            e.HasOne(x => x.Application)
                .WithMany(a => a.Environments)
                .HasForeignKey(x => x.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("ApiKeys");
            e.HasKey(x => x.Id);
            e.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.Property(x => x.CreatedByUserId).HasMaxLength(64);
        });

        b.Entity<EventRecord>(e =>
        {
            e.ToTable("Events");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventName).HasMaxLength(64).IsRequired();
            e.Property(x => x.DistinctId).HasMaxLength(128).IsRequired();
            e.Property(x => x.SessionId).HasMaxLength(64);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.NormalizedRoute).HasMaxLength(512);
            e.Property(x => x.EndpointGroup).HasMaxLength(128);
            e.Property(x => x.FeatureArea).HasMaxLength(64);
            e.Property(x => x.ReleaseSha).HasMaxLength(64);
            e.Property(x => x.PropertiesJson).HasColumnType("nvarchar(max)").IsRequired();
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.CreatedAt });
            e.HasIndex(x => new { x.ApplicationId, x.EventName, x.CreatedAt });
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.SessionId, x.OccurredAt });
        });

        b.Entity<ErrorRecord>(e =>
        {
            e.ToTable("Errors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.ErrorType).HasMaxLength(128).IsRequired();
            e.Property(x => x.ExceptionType).HasMaxLength(256);
            e.Property(x => x.EndpointGroup).HasMaxLength(128);
            e.Property(x => x.JobName).HasMaxLength(128);
            e.Property(x => x.NormalizedRoute).HasMaxLength(512);
            e.Property(x => x.ReleaseSha).HasMaxLength(64);
            e.Property(x => x.LastCorrelationId).HasMaxLength(64);
            e.Property(x => x.PropertiesJson).HasColumnType("nvarchar(max)").IsRequired();
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.LastSeenAt });
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.LastCorrelationId });
        });

        b.Entity<SafetyViolation>(e =>
        {
            e.ToTable("SafetyViolations");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventName).HasMaxLength(64).IsRequired();
            e.Property(x => x.RejectedField).HasMaxLength(128).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.CreatedAt });
        });

        b.Entity<BackgroundJobFailure>(e =>
        {
            e.ToTable("BackgroundJobFailures");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobName).HasMaxLength(128).IsRequired();
            e.Property(x => x.ErrorType).HasMaxLength(128).IsRequired();
            e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReleaseSha).HasMaxLength(64);
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.Fingerprint }).IsUnique();
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.LastSeenAt });
        });

        b.Entity<Session>(e =>
        {
            e.ToTable("Sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.SessionId).HasMaxLength(64).IsRequired();
            e.Property(x => x.DistinctId).HasMaxLength(128).IsRequired();
            e.Property(x => x.ReleaseSha).HasMaxLength(64);
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.SessionId }).IsUnique();
            e.HasIndex(x => new { x.ApplicationId, x.EnvironmentId, x.LastSeenAt });
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.ActorType).HasMaxLength(32).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.DetailsJson).HasColumnType("nvarchar(max)").IsRequired();
            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => new { x.Action, x.OccurredAt });
        });

        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.Role).HasConversion<int>();
        });

        b.Entity<UserApplicationAssignment>(e =>
        {
            e.ToTable("UserApplicationAssignments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.ApplicationId }).IsUnique();
            e.HasOne(x => x.User)
                .WithMany(u => u.ApplicationAssignments)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AlertRule>(e =>
        {
            e.ToTable("AlertRules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.RuleType).HasConversion<int>();
            e.Property(x => x.EventName).HasMaxLength(64);
            e.HasIndex(x => new { x.ApplicationId, x.IsEnabled });
        });

        b.Entity<FiredAlert>(e =>
        {
            e.ToTable("FiredAlerts");
            e.HasKey(x => x.Id);
            e.Property(x => x.RuleType).HasConversion<int>();
            e.Property(x => x.DedupKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(512).IsRequired();
            e.Property(x => x.DetailsJson).HasColumnType("nvarchar(max)").IsRequired();
            e.HasIndex(x => new { x.AlertRuleId, x.DedupKey, x.FiredAt });
            e.HasIndex(x => new { x.ApplicationId, x.FiredAt });
        });
    }
}
