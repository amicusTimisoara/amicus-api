using Amicus.Api.Auth;
using Amicus.Api.Email;
using Amicus.Api.Endpoints;
using Amicus.Api.Setup;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAmicusPersistence(builder.Configuration);
builder.Services.AddAmicusDataProtection(builder.Configuration);
builder.Services.AddAmicusIdentity();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AmicusDbContext>();

// Injected rather than calling DateTimeOffset.UtcNow inline, so tests can book a
// slot in a controlled "now" instead of depending on the wall clock.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddReverseProxySupport();
builder.Services.AddAmicusRateLimiting(builder.Configuration);

builder.Services
    .AddOptions<GoogleAuthOptions>()
    .Bind(builder.Configuration.GetSection(GoogleAuthOptions.SectionName));
builder.Services.AddScoped<IGoogleIdTokenVerifier, GoogleIdTokenVerifier>();

builder.Services
    .AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection(EmailOptions.SectionName));
// Registering this activates Identity's /forgotPassword + /resetPassword; without
// it they succeed but send nothing.
builder.Services.AddScoped<IEmailSender<AppUser>, SmtpEmailSender>();

builder.Services.AddAmicusCors(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Before the rate limiter, so per-IP partitions see the real client address
// rather than nginx's loopback — otherwise every request shares one bucket.
app.UseForwardedHeaders();
app.UseForwardedPrefix();

app.UseCors(CorsSetup.PolicyName);

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// /register, /login, /refresh, /manage/info — email + password, bearer tokens.
var auth = app.MapGroup("/auth").RequireRateLimiting(RateLimitSetup.AuthPolicy);
auth.MapIdentityApi<AppUser>();
auth.MapGoogleAuth();

app.MapEvents();
app.MapBookings();
app.MapAdmin();

// A friendly root so hitting the base URL in a browser shows the API is alive
// instead of a bare 404 — it is an API with no page, and a 404 there reads as
// "nothing is running". Names the health and OpenAPI paths for a human poking at it.
app.MapGet("/", () => Results.Ok(new
{
    service = "amicus-api",
    status = "ok",
    docs = "/openapi/v1.json",
    health = "/health",
}))
    .WithName("Root")
    .AllowAnonymous()
    .DisableRateLimiting();

// Deliberately exempt: an uptime monitor polling every few seconds must not be
// throttled, and it exposes nothing.
app.MapHealthChecks("/health").DisableRateLimiting();

// Liveness AND readiness: it round-trips the database, so a green /health means
// the API can actually serve, not merely that the process is up.
await StartupTasks.RunAsync(app.Services, app.Configuration, app.Logger);

app.Run();

/// <summary>Marker so the integration tests can host this app in-process.</summary>
public partial class Program;
