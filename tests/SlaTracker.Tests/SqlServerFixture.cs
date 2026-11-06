using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.MsSql;

namespace SlaTracker.Tests;

/// <summary>A clock the tests can move, so SLA deadlines can be tested without waiting.</summary>
public sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>One real SQL Server container for all integration tests (needs Docker).</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder().Build();

    public MutableClock Clock { get; } = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    public string ConnectionString { get; private set; } = "";
    public TrackerApp App { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "SlaTracker" }.ConnectionString;
        Environment.SetEnvironmentVariable("ConnectionStrings__Tracker", ConnectionString);
        App = new TrackerApp(ConnectionString, Clock);
        _ = App.Server;   // boots the app, which runs the migrations
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class TrackerApp(string connectionString, TimeProvider clock) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Tracker", connectionString);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton(clock);
        });
    }
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture> { }
