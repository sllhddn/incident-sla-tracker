using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SlaTracker.Api.Data;
using SlaTracker.Api.Endpoints;
using SlaTracker.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Tracker")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Tracker");

builder.Services.AddDbContext<TrackerDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandler>();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    var migrator = new SchemaMigrator(connectionString, app.Services.GetRequiredService<ILogger<SchemaMigrator>>());
    await migrator.MigrateAsync();
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi();                 // /openapi/v1.json

app.MapGet("/health", async (TrackerDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "UP" }) : Results.StatusCode(503));

app.MapIncidents();
app.MapReleases();
app.MapReports();

app.Run();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program { }
