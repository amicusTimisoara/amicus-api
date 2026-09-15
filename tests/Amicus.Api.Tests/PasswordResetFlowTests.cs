using System.Net;
using System.Net.Http.Json;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Amicus.Api.Tests;

[Collection(AmicusCollection.Name)]
public sealed class PasswordResetFlowTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Registering_auto_confirms_and_sends_a_verify_email()
    {
        var client = _app.CreateClient();

        (await client.PostAsJsonAsync("/auth/register", new
        {
            email = "new@amicus.test", password = "correct-horse-battery",
        })).EnsureSuccessStatusCode();

        using var scope = _app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync("new@amicus.test");

        Assert.NotNull(user);
        // Login works immediately (auto-confirmed), AND a friendly verify email goes out.
        Assert.True(user!.EmailConfirmed, "a password registration must be auto-confirmed");
        Assert.Contains(_app.Email.Sent, e => e.Kind == "confirmation" && e.To == "new@amicus.test");
    }

    [Fact]
    public async Task Forgot_password_actually_sends_a_reset_to_a_registered_user()
    {
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/auth/register", new
        {
            email = "forgot@amicus.test", password = "correct-horse-battery",
        })).EnsureSuccessStatusCode();

        _app.Email.Sent.Clear(); // drop the registration's verify email
        var response = await client.PostAsJsonAsync("/auth/forgotPassword",
            new { email = "forgot@amicus.test" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The whole point of the auto-confirm fix: the reset is genuinely sent,
        // not silently swallowed because the email was unconfirmed.
        var reset = Assert.Single(_app.Email.Sent, e => e.Subject == "reset");
        Assert.Equal("forgot@amicus.test", reset.To);
        Assert.False(string.IsNullOrWhiteSpace(reset.Payload));
    }

    [Fact]
    public async Task A_reset_code_from_the_email_actually_changes_the_password()
    {
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/auth/register", new
        {
            email = "cycle@amicus.test", password = "correct-horse-battery",
        })).EnsureSuccessStatusCode();

        _app.Email.Sent.Clear(); // drop the registration's verify email
        await client.PostAsJsonAsync("/auth/forgotPassword", new { email = "cycle@amicus.test" });
        var code = Assert.Single(_app.Email.Sent, e => e.Subject == "reset").Payload;

        // Identity base64url-encodes the code before it reaches the email sender,
        // so the reset endpoint receives it exactly as delivered.
        var reset = await client.PostAsJsonAsync("/auth/resetPassword", new
        {
            email = "cycle@amicus.test",
            resetCode = code,
            newPassword = "a-different-passphrase",
        });
        reset.EnsureSuccessStatusCode();

        var anon = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/auth/login",
            new { email = "cycle@amicus.test", password = "correct-horse-battery" })).StatusCode);
        (await anon.PostAsJsonAsync("/auth/login",
            new { email = "cycle@amicus.test", password = "a-different-passphrase" }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_google_user_is_also_confirmed()
    {
        _app.Google.Accept("tok",
            new Amicus.Api.Auth.GoogleIdentity("g-1", "g@amicus.test", EmailVerified: true, "G"));
        var client = _app.CreateClient();
        (await client.PostAsJsonAsync("/auth/google", new { idToken = "tok" }))
            .EnsureSuccessStatusCode();

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();
        var user = await db.Users.SingleAsync(u => u.Email == "g@amicus.test");
        Assert.True(user.EmailConfirmed);
    }
}
