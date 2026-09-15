using System.Net.Http.Json;
using Amicus.Api.Auth;
using Microsoft.AspNetCore.Identity;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Amicus.Api.Tests;

/// <summary>
/// Hosts the real app against a real Postgres.
///
/// Deliberately not an in-memory provider: the most important rule in this system
/// — one live booking per slot — is a partial unique index only Postgres enforces.
/// A fake provider would let every double-booking test pass while production
/// stayed broken.
/// </summary>
public sealed class AmicusAppFactory : WebApplicationFactory<Program>
{
    private const string TestDatabase = "amicus_test";

    /// <summary>Stubbed; nothing in the tests talks to Google's JWKS.</summary>
    public FakeGoogleVerifier Google { get; } = new();

    /// <summary>Records the emails Identity asked to send, so tests can assert a
    /// reset was actually triggered without a live SMTP server.</summary>
    public FakeEmailSender Email { get; } = new();

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-08-31T09:00:00Z"));

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("AMICUS_TEST_POSTGRES")
        ?? "Host=localhost;Port=5433;Database=postgres;Username=amicus;Password=amicus_dev";

    /// <summary>
    /// Extra settings for a one-off factory, e.g. a deliberately tiny rate limit.
    /// Applied after the defaults, so it can override them.
    /// </summary>
    public Dictionary<string, string?> Overrides { get; } = [];

    private static string TestConnectionString =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = TestDatabase,
        }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, NOT ConfigureAppConfiguration. Under minimal hosting, Program.cs
        // reads builder.Configuration while the WebApplicationBuilder is being
        // constructed — before the factory's ConfigureAppConfiguration delegates run —
        // so an in-memory source added there arrives too late and the app starts with
        // no connection string. UseSetting lands in host configuration, which
        // builder.Configuration already includes.
        builder.UseSetting("ConnectionStrings:Postgres", TestConnectionString);
        builder.UseSetting("Authentication:Google:ClientIds:0", "test-client-id");

        // Every in-process request comes from the same loopback address, so one
        // shared partition holds the whole suite. Left at production limits, the
        // register/login churn across tests would start returning 429 partway
        // through and the failures would look like unrelated flakiness.
        builder.UseSetting("RateLimits:GlobalPermitsPerMinute", "1000000");
        builder.UseSetting("RateLimits:AuthPermitsPerMinute", "1000000");

        foreach (var (key, value) in Overrides)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGoogleIdTokenVerifier>();
            services.AddSingleton<IGoogleIdTokenVerifier>(Google);

            services.RemoveAll<IEmailSender<AppUser>>();
            services.AddSingleton<IEmailSender<AppUser>>(Email);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>
    /// Creates and migrates the test database WITHOUT touching <c>Services</c>.
    /// Reading that property boots the host, whose startup tasks query the roles
    /// table — so going through DI here would migrate only after the app had
    /// already failed on a schema that did not exist yet.
    /// </summary>
    public async Task EnsureDatabaseAsync()
    {
        await CreateDatabaseIfMissingAsync();

        var options = new DbContextOptionsBuilder<AmicusDbContext>()
            .UseNpgsql(TestConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new AmicusDbContext(options);
        await db.Database.MigrateAsync();
    }

    private static async Task CreateDatabaseIfMissingAsync()
    {
        await using var connection = new NpgsqlConnection(AdminConnectionString);
        await connection.OpenAsync();

        await using var exists = new NpgsqlCommand(
            "select 1 from pg_database where datname = @name", connection);
        exists.Parameters.AddWithValue("name", TestDatabase);

        if (await exists.ExecuteScalarAsync() is not null)
        {
            return;
        }

        // CREATE DATABASE cannot take parameters. The name is a compile-time
        // constant, so there is nothing here to inject.
        await using var create = new NpgsqlCommand(
            $"create database \"{TestDatabase}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>Empties every table so each test starts from nothing.</summary>
    public async Task ResetAsync()
    {
        Email.Sent.Clear();
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();

        await db.Database.ExecuteSqlRawAsync(
            "truncate bookings, slots, slot_patterns, event_specialists, events, "
            + "specialists, users, user_roles, user_logins, user_claims, user_tokens cascade;");
    }

    /// <summary>Registers a user, optionally in roles, and returns a bearer client.</summary>
    public async Task<HttpClient> SignedInClientAsync(string email, params string[] roles)
    {
        var client = CreateClient();

        (await client.PostAsJsonAsync(
            "/auth/register", new { email, password = "correct-horse-battery" }))
            .EnsureSuccessStatusCode();

        if (roles.Length > 0)
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await users.FindByEmailAsync(email)
                ?? throw new InvalidOperationException($"{email} vanished after register.");

            foreach (var role in roles)
            {
                var added = await users.AddToRoleAsync(user, role);

                if (!added.Succeeded)
                {
                    throw new InvalidOperationException(
                        string.Join("; ", added.Errors.Select(e => e.Description)));
                }
            }
        }

        return await Authenticate(client, email);
    }

    public async Task<HttpClient> Authenticate(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync(
            "/auth/login", new { email, password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();

        var token = (await login.Content.ReadFromJsonAsync<AccessTokenPayload>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        return client;
    }

    public sealed record AccessTokenPayload(string AccessToken);
}

public sealed record SentEmail(string To, string Subject, string Kind, string Payload);

public sealed class FakeEmailSender : IEmailSender<AppUser>
{
    public List<SentEmail> Sent { get; } = [];

    public Task SendConfirmationLinkAsync(AppUser user, string email, string link)
    {
        // No-op, mirroring the production SmtpEmailSender: the app auto-confirms on
        // registration and does not send a 'confirm your address' email. Recording
        // it would test a behaviour the real sender doesn't have.
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string link)
    {
        Sent.Add(new SentEmail(email, "reset", "reset-link", link));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(AppUser user, string email, string code)
    {
        Sent.Add(new SentEmail(email, "reset", "reset-code", code));
        return Task.CompletedTask;
    }
}

public sealed class FakeGoogleVerifier : IGoogleIdTokenVerifier
{
    private readonly Dictionary<string, GoogleIdentity> _tokens = [];

    public void Accept(string idToken, GoogleIdentity identity) => _tokens[idToken] = identity;

    public Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken ct = default) =>
        Task.FromResult(_tokens.GetValueOrDefault(idToken));
}

public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// One factory and one database for the whole suite; each test calls
/// <see cref="AmicusAppFactory.ResetAsync"/> first. Cheaper than a database per
/// test, and the reset is what keeps them independent.
/// </summary>
public sealed class AmicusFixture : IAsyncLifetime
{
    public AmicusAppFactory App { get; } = new();

    public async Task InitializeAsync() => await App.EnsureDatabaseAsync();

    public async Task DisposeAsync() => await App.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class AmicusCollection : ICollectionFixture<AmicusFixture>
{
    public const string Name = "amicus";
}
