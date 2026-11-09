using ESPresense.Extensions;
using ESPresense.Models;
using ESPresense.Services;
using MQTTnet.Diagnostics.Logger;
using Serilog;
using Serilog.Events;
using ESPresense.Optimizers;
using ESPresense.Controllers;
using ESPresense.Utils;
using Microsoft.AspNetCore.DataProtection;
using Flurl.Http.Newtonsoft;
using Flurl.Http;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

FlurlHttp.Clients.UseNewtonsoft();

builder.Host.UseSerilog((context, cfg) => cfg.ReadFrom.Configuration(context.Configuration));

Log.Logger.Information(MathNet.Numerics.Control.Describe().Trim('\r', '\n'));

var configDir = Environment.GetEnvironmentVariable("CONFIG_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".espresense");
var storageDir = Path.Combine(configDir, ".storage");
Directory.CreateDirectory(storageDir);

builder.Services.AddSingleton(a => new ConfigLoader(configDir, a.GetRequiredService<ILogger<ConfigLoader>>()));
builder.Services.AddHostedService(a => a.GetRequiredService<ConfigLoader>());

builder.Services.AddDataProtection()
    .UseEphemeralDataProtectionProvider();

builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IMqttNetLogger>(a => new MqttNetLogger(a.GetRequiredService<ILogger<MqttNetLogger>>()));
builder.Services.AddSingleton<MqttCoordinator>();
builder.Services.AddSingleton<IMqttCoordinator>(provider => provider.GetRequiredService<MqttCoordinator>());
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<GlobalEventDispatcher>();
builder.Services.AddSingleton<DeviceTracker>();
builder.Services.AddSingleton<SupervisorConfigLoader>();
builder.Services.AddSingleton(a => new DeviceHistoryStore(storageDir, a.GetRequiredService<ConfigLoader>(), a.GetRequiredService<ILogger<DeviceHistoryStore>>()));
builder.Services.AddSingleton<DeviceSettingsStore>();
builder.Services.AddSingleton<NodeSettingsStore>();
builder.Services.AddSingleton<NodeTelemetryStore>();
builder.Services.AddSingleton<FirmwareTypeStore>();
builder.Services.AddSingleton<IFirmwareTypeStore>(provider => provider.GetRequiredService<FirmwareTypeStore>());
builder.Services.AddSingleton<NodeStateMapper>();
builder.Services.AddSingleton<FirmwareUpdateJobService>();
builder.Services.AddSingleton<FirmwareCatalogService>();
builder.Services.AddSingleton<DeviceService>();
builder.Services.AddSingleton<DeviceCaptureService>();
builder.Services.AddSingleton<IDeviceStatePublisher, DeviceStatePublisher>();
builder.Services.AddSingleton<IDeviceHistoryWriter, DeviceHistoryWriter>();
builder.Services.AddSingleton<LeaseService>();
builder.Services.AddSingleton<ILeaseService>(provider => provider.GetRequiredService<LeaseService>());
builder.Services.AddSingleton(sp => new PairErrorTracker(
    sp.GetRequiredService<State>(),
    Path.Combine(storageDir, "pair-errors.json")));
builder.Services.AddSingleton(sp => new WalkTestService(
    sp.GetRequiredService<State>(),
    sp.GetRequiredService<PairErrorTracker>(),
    sp.GetRequiredService<NodeSettingsStore>(),
    Path.Combine(storageDir, "walktest-points.json")));
builder.Services.AddSingleton<AutoTuneService>();
// Der eine Szenarien-Replay, den alle Wizard-Messungen benutzen (siehe ScenarioReplay).
builder.Services.AddSingleton<ESPresense.Locators.ScenarioReplay>();
builder.Services.AddSingleton<LocatorTuneService>();
builder.Services.AddSingleton<WizardService>();
// Subscribes to device messages on construction, so it must be created eagerly - see below.
builder.Services.AddSingleton<DeviceIdentityTracker>();
builder.Services.AddSingleton<WizardDiagnostics>();
builder.Services.AddSingleton<WalkPointHygiene>();
builder.Services.AddSingleton<NextActions>();
builder.Services.AddSingleton(sp => new AutoApply(
    sp.GetRequiredService<LocatorSweepService>(),
    sp.GetRequiredService<LocatorTuneService>(),
    sp.GetRequiredService<ConfigLoader>(),
    Path.Combine(storageDir, "auto-applied.json")));
builder.Services.AddSingleton<WalkPointPlanner>();
builder.Services.AddSingleton<CalibrationSweepService>();
builder.Services.AddSingleton<LocatorSweepService>();
builder.Services.AddSingleton<DeviceSetupService>();
builder.Services.AddSingleton(sp => new CalibrationBenchmark(
    sp.GetRequiredService<State>(),
    sp.GetRequiredService<WalkTestService>(),
    sp.GetRequiredService<ConfigLoader>(),
    sp.GetRequiredService<ESPresense.Locators.ScenarioReplay>(),
    sp.GetRequiredService<NodeSettingsStore>(),
    Path.Combine(storageDir, "benchmark-history.json")));
builder.Services.AddSingleton(sp => new NodeMoveTracker(
    sp.GetRequiredService<State>(),
    sp.GetRequiredService<NodeSettingsStore>(),
    Path.Combine(storageDir, "node-moves.json")));
// Registered as singleton + forwarded so WizardController can call TriggerNow() on the same instance.
builder.Services.AddSingleton<ESPresense.Optimizers.OptimizationSnapshotStore>();
builder.Services.AddSingleton<OptimizationRunner>();

builder.Services.AddHostedService<MultiScenarioLocator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<OptimizationRunner>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<NodeMoveTracker>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceTracker>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<DeviceSettingsStore>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<NodeSettingsStore>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<NodeTelemetryStore>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<TelemetryService>());
builder.Services.AddHostedService<DeviceCleanupService>();
builder.Services.AddSingleton<State>();
builder.Services.AddSingleton<CalibrationService>();
builder.Services.AddSingleton(provider => new OptimizerDebugService(provider.GetRequiredService<OptimizationRunner>(), provider.GetRequiredService<State>()));
builder.Services.AddSingleton<DeviceEnricher>();
builder.Services.AddControllersWithViews().AddJsonOptions(opt => JsonDefaults.ConfigureWeb(opt.JsonSerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SchemaFilter<CalibrationCellSchemaFilter>());
builder.Services.AddHealthChecks();

builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "ESPresense.Companion", Version = "1.0.0" };
})
    .WithResources<McpResources>()
    .WithTools<McpResources>()
    .WithTools<McpOptimizerTools>()
    .WithHttpTransport();

var app = builder.Build();

// Eagerly resolve trackers that subscribe to MQTT in their constructor: as lazy singletons they
// would only come alive on the first HTTP request and miss every message until then.
app.Services.GetRequiredService<DeviceIdentityTracker>();

// Walk-Punkt-Hygiene einmal beim Start: legt einzelne Knotenmessungen still, die aus sich
// heraus unbrauchbar sind (zu wenige Messwerte, Pegel staerker als physikalisch moeglich).
// Bewusst NUR die modellunabhaengigen Regeln - Urteile, die die Kalibrierung selbst zum
// Massstab nehmen, bleiben Vorschlaege in der Diagnose. Siehe WalkPointHygiene.
try
{
    var hygiene = app.Services.GetRequiredService<WalkPointHygiene>();
    hygiene.Apply();

    // Danach die Einzelknoten-Widersprueche: zwei Aufnahmen am selben Ort, bei denen GENAU EIN
    // Knoten aus der Reihe faellt. Reihenfolge zaehlt - Apply() nimmt vorher unbrauchbare
    // Messungen heraus, die den Vergleich sonst mit verzerren wuerden.
    // ⚠ Mehrere Durchgaenge, aber GEDECKELT. Jede Stilllegung verschiebt die Mediane des Paares,
    // wodurch ein bisher zweitschlimmster Knoten ueber die Schwelle rutschen kann - das ist echt
    // (er lag immer so weit daneben), aber es ist auch genau die Kaskade, vor der der Deckel
    // schuetzt. Drei Durchgaenge reichen erfahrungsgemaess; was danach bleibt, steht in der
    // Diagnose und ist damit sichtbar statt stillschweigend weggeraeumt.
    var diagnostics = app.Services.GetRequiredService<WizardDiagnostics>();
    for (var runde = 1; runde <= 3; runde++)
    {
        var einzel = diagnostics.Analyze().ConflictingWalkPairs
            .Where(c => c.Irreconcilable && c.SingleNodeOnly && c.MaxDeltaNode != null)
            .Select(c => (c.IdA, c.IdB, c.MaxDeltaNode!, c.MaxDeltaDb, c.ExplainableDb))
            .ToList();
        if (einzel.Count == 0) break;
        var getan = hygiene.ResolveSingleNodeConflicts(einzel);
        Log.Information("Walk-Punkt-Hygiene Runde {Runde}: {Faelle} Einzelknoten-Widersprueche, " +
                        "{Getan} Messungen stillgelegt", runde, einzel.Count, getan.Count);
        if (getan.Count == 0) break;   // Deckel erreicht oder nichts mehr zu tun
    }
}
catch (Exception ex) { Log.Warning(ex, "Walk-Punkt-Hygiene beim Start fehlgeschlagen"); }

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(15)
});

app.UseSerilogRequestLogging(o =>
{
    o.EnrichDiagnosticContext = (dc, ctx) => dc.Set("UserAgent", ctx?.Request.Headers.UserAgent);
    o.GetLevel = (ctx, ms, ex) => ex != null ? LogEventLevel.Error : ctx.Response.StatusCode > 499 ? LogEventLevel.Error : ms > 500 ? LogEventLevel.Warning : ctx.Request.Path.Value.IndexOf("/state", StringComparison.OrdinalIgnoreCase) > 0 ? LogEventLevel.Verbose : LogEventLevel.Debug;
});

app.UseSwagger(c => c.RouteTemplate = "api/swagger/{documentName}/swagger.{json|yaml}");
app.UseSwaggerUI(c => c.RoutePrefix = "api/swagger");

// Without Cache-Control, browsers (and the HA ingress iframe) heuristically cached index.html off Last-Modified
// and kept running the previous build after an add-on update. Hashed assets never change. Everything else is
// good for a minute, then revalidates (ETag makes that a cheap 304). Not "public": ingress responses are per-user.
const string spaCacheControl = "max-age=60, must-revalidate";
var spaFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/_app/immutable")
            ? "public, max-age=31536000, immutable"
            : spaCacheControl
};
app.UseStaticFiles(spaFiles);
app.UseRouting();
app.MapMcp("/api/mcp");
app.MapHealthChecks("/healthz");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller}/{action=Index}/{id?}");

// Client-side routes (/devices/{id}, ...) get index.html with its relative paths rebased to the request's depth.
app.MapSpaFallback(spaCacheControl);

app.Run();
