using System.Net;
using System.Net.Http.Json;

namespace Amicus.Api.Tests;

[Collection(AmicusCollection.Name)]
public sealed class AccountTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record AccountInfoDto(
        string Email, string? DisplayName, string? PhotoUrl, bool IsEmailConfirmed);

    [Fact]
    public async Task Me_returns_the_signed_in_users_profile()
    {
        var client = await _app.SignedInClientAsync("me@amicus.test");

        var info = await client.GetFromJsonAsync<AccountInfoDto>("/account/me");

        Assert.NotNull(info);
        Assert.Equal("me@amicus.test", info!.Email);
        Assert.True(info.IsEmailConfirmed);   // auto-confirmed on register
        Assert.Null(info.DisplayName);        // password account, no name yet
        Assert.Null(info.PhotoUrl);           // and no photo
    }

    [Fact]
    public async Task Patch_sets_and_clears_the_display_name()
    {
        var client = await _app.SignedInClientAsync("name@amicus.test");

        var set = await client.PatchAsJsonAsync("/account/me", new { displayName = "  Ana P.  " });
        set.EnsureSuccessStatusCode();
        var afterSet = await set.Content.ReadFromJsonAsync<AccountInfoDto>();
        Assert.Equal("Ana P.", afterSet!.DisplayName);   // trimmed

        // it persists
        var reread = await client.GetFromJsonAsync<AccountInfoDto>("/account/me");
        Assert.Equal("Ana P.", reread!.DisplayName);

        // empty clears it
        var clear = await client.PatchAsJsonAsync("/account/me", new { displayName = "" });
        var afterClear = await clear.Content.ReadFromJsonAsync<AccountInfoDto>();
        Assert.Null(afterClear!.DisplayName);
    }

    [Fact]
    public async Task A_google_user_gets_a_photo_and_name_on_their_profile()
    {
        _app.Google.Accept("tok", new Amicus.Api.Auth.GoogleIdentity(
            "g-photo", "photo@amicus.test", EmailVerified: true, "Foto U", "https://pic/foto.jpg"));
        var client = _app.CreateClient();
        var login = await client.PostAsJsonAsync("/auth/google", new { idToken = "tok" });
        var token = (await login.Content.ReadFromJsonAsync<Token>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var info = await client.GetFromJsonAsync<AccountInfoDto>("/account/me");
        Assert.Equal("Foto U", info!.DisplayName);
        Assert.Equal("https://pic/foto.jpg", info.PhotoUrl);
    }

    [Fact]
    public async Task Anonymous_cannot_read_or_change_the_profile()
    {
        var anon = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/account/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PatchAsJsonAsync("/account/me", new { displayName = "x" })).StatusCode);
    }

    private sealed record Token(string AccessToken);
}
