using System.Net;
using System.Net.Http.Json;
using Amicus.Api.Auth;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Amicus.Api.Tests;

[Collection(AmicusCollection.Name)]
public sealed class GoogleSignInTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record TokenDto(string AccessToken);
    private sealed record InfoDto(string Email);

    [Fact]
    public async Task A_new_google_user_gets_an_account_and_a_usable_token()
    {
        _app.Google.Accept(
            "good-token",
            new GoogleIdentity("google-sub-1", "new@amicus.test", EmailVerified: true, "Noua", "https://pic/noua.jpg"));

        var client = _app.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/google", new { idToken = "good-token" });

        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken;
        Assert.NotEmpty(token);

        // The token works against the ordinary Identity endpoints, which is the
        // point: a client stores and refreshes it the same way either way.
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var info = await client.GetFromJsonAsync<InfoDto>("/auth/manage/info");
        Assert.Equal("new@amicus.test", info!.Email);

        using var scope = _app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync("new@amicus.test");

        Assert.NotNull(user);
        Assert.True(user!.EmailConfirmed);
        Assert.Equal("Noua", user.DisplayName);
        Assert.Equal("https://pic/noua.jpg", user.PhotoUrl);
        Assert.Equal(
            "Google",
            Assert.Single(await users.GetLoginsAsync(user)).LoginProvider);
    }

    [Fact]
    public async Task Signing_in_twice_reuses_the_same_account()
    {
        _app.Google.Accept(
            "good-token",
            new GoogleIdentity("google-sub-1", "repeat@amicus.test", EmailVerified: true, "Rep", null));

        var client = _app.CreateClient();

        (await client.PostAsJsonAsync("/auth/google", new { idToken = "good-token" }))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/auth/google", new { idToken = "good-token" }))
            .EnsureSuccessStatusCode();

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();

        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.UserLogins.CountAsync());
    }

    [Fact]
    public async Task Google_links_onto_an_existing_password_account()
    {
        var client = _app.CreateClient();

        (await client.PostAsJsonAsync(
            "/auth/register",
            new { email = "both@amicus.test", password = "correct-horse-battery" }))
            .EnsureSuccessStatusCode();

        _app.Google.Accept(
            "good-token",
            new GoogleIdentity("google-sub-9", "both@amicus.test", EmailVerified: true, "Both", null));

        (await client.PostAsJsonAsync("/auth/google", new { idToken = "good-token" }))
            .EnsureSuccessStatusCode();

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();

        // One account with two ways in — not a duplicate, and not a lockout.
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.UserLogins.CountAsync());

        // The password still works afterwards.
        var login = await client.PostAsJsonAsync(
            "/auth/login", new { email = "both@amicus.test", password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task An_unverified_google_email_is_refused()
    {
        // Accounts are matched by email, so honouring an unverified address would let
        // anyone who sets their Google profile email to a victim's take that account.
        _app.Google.Accept(
            "unverified",
            new GoogleIdentity("google-sub-2", "victim@amicus.test", EmailVerified: false, null, null));

        var response = await _app.CreateClient()
            .PostAsJsonAsync("/auth/google", new { idToken = "unverified" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task An_unrecognised_token_is_rejected_without_creating_anything()
    {
        var response = await _app.CreateClient()
            .PostAsJsonAsync("/auth/google", new { idToken = "forged" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();
        Assert.Equal(0, await db.Users.CountAsync());
    }

    [Fact]
    public async Task A_missing_token_is_a_bad_request_not_a_crash()
    {
        var response = await _app.CreateClient()
            .PostAsJsonAsync("/auth/google", new { idToken = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
