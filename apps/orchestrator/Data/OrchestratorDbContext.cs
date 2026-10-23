using Microsoft.EntityFrameworkCore;

namespace AudioOrchestrator.Data;

public class OrchestratorDbContext : DbContext
{
    public OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : base(options) { }

    public DbSet<AudioOrchestrator.Data.Entities.GroupEntity> Groups { get; set; } = default!;
    public DbSet<AudioOrchestrator.Data.Entities.RoomEntity> Rooms { get; set; } = default!;
    public DbSet<AudioOrchestrator.Data.Entities.RadioStreamEntity> RadioStreams { get; set; } = default!;
    public DbSet<AudioOrchestrator.Data.Entities.SpotifySourceEntity> SpotifySources { get; set; } = default!;
    public DbSet<AudioOrchestrator.Data.Entities.SpotifyMetadataEntity> SpotifyMetadata { get; set; } = default!;
    public DbSet<AudioOrchestrator.Data.Entities.RadioPluginEntity> RadioPlugins { get; set; } = default!;
    public DbSet<AudioOrchestrator.Data.Entities.OrchestratorSettingsEntity> Settings { get; set; } = default!;
    public DbSet<AudioOrchestrator.Data.Entities.BroadcastJobEntity> BroadcastJobs { get; set; } = default!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AudioOrchestrator.Data.Entities.OrchestratorSettingsEntity>()
            .Property(s => s.VolumeProfile)
            .HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<AudioOrchestrator.Contracts.VolumeProfile>(v, true));

        modelBuilder.Entity<AudioOrchestrator.Data.Entities.BroadcastJobEntity>()
            .Property(b => b.Status)
            .HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<AudioOrchestrator.Contracts.BroadcastJobStatus>(v, true));

        modelBuilder.Entity<AudioOrchestrator.Data.Entities.BroadcastJobEntity>()
            .Property(b => b.SourceType)
            .HasConversion(
                v => v.ToString().ToLowerInvariant(),
                v => Enum.Parse<AudioOrchestrator.Contracts.BroadcastSourceType>(v, true));
    }
}
