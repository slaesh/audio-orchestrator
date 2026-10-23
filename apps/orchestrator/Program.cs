using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
var builder = WebApplication.CreateBuilder(args);
var isDesignOrDocumentGen = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name?.StartsWith("GetDocument") == true
    || System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name?.StartsWith("ef") == true;

var dbPath = builder.Configuration.GetValue<string>("DbPath") ?? "data/orchestrator.db";
var dir = System.IO.Path.GetDirectoryName(dbPath);
if (!isDesignOrDocumentGen && !string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
{
    try { System.IO.Directory.CreateDirectory(dir); } catch { /* Ignore for design-time */ }
}

builder.Services.AddDbContext<AudioOrchestrator.Data.OrchestratorDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}")
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddOpenApi(options => {
    options.AddSchemaTransformer((schema, context, cancellationToken) => {
        // Fix for .NET 9 OpenAPI generator sometimes producing [integer, string] for ints with patterns
        if (schema.AnyOf != null || schema.OneOf != null) return Task.CompletedTask;
        
        if (schema.Type != null && schema.Type.Value.HasFlag(JsonSchemaType.Integer))
        {
            // Force it to be just an integer, removing any string-patterns that might confuse the generator
            schema.Type = JsonSchemaType.Integer;
            schema.Pattern = null;
        }
        return Task.CompletedTask;
    });
});

var dockerUri = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
    ? new Uri("npipe://./pipe/docker_engine")
    : new Uri("unix:///var/run/docker.sock");
builder.Services.AddSingleton<Docker.DotNet.IDockerClient>(_ => new Docker.DotNet.DockerClientConfiguration(dockerUri).CreateClient());

builder.Services.AddSingleton<AudioOrchestrator.Services.IFifoService, AudioOrchestrator.Services.FifoService>();
builder.Services.AddSingleton<AudioOrchestrator.Services.IContainerLifecycleService, AudioOrchestrator.Services.DockerLifecycleService>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<AudioOrchestrator.Services.ILibrespotApiClient, AudioOrchestrator.Services.LibrespotApiClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<AudioOrchestrator.Services.INodeReleaseService, AudioOrchestrator.Services.NodeReleaseService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "AudioOrchestrator");
});
builder.Services.AddScoped<AudioOrchestrator.Services.ISpotifyNodeReleaseService>(sp => (AudioOrchestrator.Services.NodeReleaseService)sp.GetRequiredService<AudioOrchestrator.Services.INodeReleaseService>());
builder.Services.AddSingleton<AudioOrchestrator.Services.IReconciliationService, AudioOrchestrator.Services.ReconciliationService>();
builder.Services.AddSingleton<AudioOrchestrator.Services.SnapcastConnectionService>();
builder.Services.AddSingleton<AudioOrchestrator.Services.ISnapcastService, AudioOrchestrator.Services.SnapcastService>();
builder.Services.AddScoped<AudioOrchestrator.Services.VolumeOrchestratorService>();
builder.Services.AddScoped<AudioOrchestrator.Services.FullStateBuilder>();

builder.Services.AddSingleton<AudioOrchestrator.Services.RadioMetadataService>();
builder.Services.AddSingleton<AudioOrchestrator.Services.IMqttClientFactorySeam, AudioOrchestrator.Services.DefaultMqttClientFactorySeam>();
builder.Services.AddSingleton<AudioOrchestrator.Services.HomeAssistantMqttService>();
builder.Services.AddSingleton<AudioOrchestrator.Services.IHomeAssistantMqttService>(sp => sp.GetRequiredService<AudioOrchestrator.Services.HomeAssistantMqttService>());
builder.Services.AddSingleton<AudioOrchestrator.Services.IAudioPlayerProcessRunner, AudioOrchestrator.Services.DefaultAudioPlayerProcessRunner>();
builder.Services.AddSingleton<AudioOrchestrator.Services.BroadcastService>();
builder.Services.AddSingleton<AudioOrchestrator.Services.IBroadcastService>(sp => sp.GetRequiredService<AudioOrchestrator.Services.BroadcastService>());

if (!isDesignOrDocumentGen)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioOrchestrator.Services.RadioMetadataService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioOrchestrator.Services.HomeAssistantMqttService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioOrchestrator.Services.BroadcastService>());
    builder.Services.AddHostedService<AudioOrchestrator.Services.SnapcastTcpListenerService>();
    builder.Services.AddHostedService<AudioOrchestrator.Services.StartupReconciliationService>();
    builder.Services.AddHostedService<AudioOrchestrator.Services.ContainerHealthWatchdog>();
}

builder.Services.AddSignalR();
builder.Services.AddHttpClient();

var app = builder.Build();

if (!isDesignOrDocumentGen)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AudioOrchestrator.Data.OrchestratorDbContext>();
    db.Database.Migrate();

    if (!db.RadioPlugins.Any())
    {
        var seededImage = builder.Configuration["DOCKER_RADIO_AUDALAXY_IMAGE"] 
            ?? AudioOrchestrator.Contracts.OrchestratorConstants.DefaultAudalaxyPluginImage;

        var audalaxyPlugin = new AudioOrchestrator.Data.Entities.RadioPluginEntity
        {
            Id = "audalaxy",
            Name = "Sunshine Live",
            DockerImage = seededImage,
            GitHubRepository = "slaesh/radio-node-audalaxy",
            Enabled = true
        };
        audalaxyPlugin.SetUrlPatterns(new[]
        {
            "sunshine-live\\.de",
            "sunshinelive",
            "sunsl\\.streamabc\\.net"
        });
        db.RadioPlugins.Add(audalaxyPlugin);
        db.SaveChanges();
    }

    if (!db.Settings.Any())
    {
        db.Settings.Add(new AudioOrchestrator.Data.Entities.OrchestratorSettingsEntity
        {
            Id = "default",
            VolumeProfile = AudioOrchestrator.Contracts.VolumeProfile.Cubic
        });
        db.SaveChanges();
    }
}

app.MapOpenApi();

app.UseStaticFiles();

app.MapControllers();
app.MapHub<AudioOrchestrator.Hubs.AudioHub>("/hubs/audio");
app.MapFallbackToFile("index.html");

app.Run();
