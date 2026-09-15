using System.Net;
using System.Net.Http.Json;
using Amicus.Infrastructure.Identity;

namespace Amicus.Api.Tests;

[Collection(AmicusCollection.Name)]
public sealed class CategoryAndResetTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record SpecialistDto(
        Guid EventSpecialistId, Guid SpecialistId, string FullName, string Specialty,
        string Category, string? Bio, string? Location);
    private sealed record AdminSpecialistDto(
        Guid Id, string FullName, string Specialty, string Category, string? Bio, bool IsActive);
    private sealed record EventDto(Guid Id, string Slug, string Name);
    private sealed record BoardDto(SpecialistDto Specialist, List<object> Slots);

    [Fact]
    public async Task A_new_specialist_keeps_its_category_and_shows_it_to_students()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        var created = await admin.PostAsJsonAsync("/admin/specialists", new
        {
            fullName = "Av. Ana Popescu",
            specialty = "Avocat",
            category = 4, // Juridic
        });
        created.EnsureSuccessStatusCode();
        var specialistId = await created.Content.ReadFromJsonAsync<Guid>();

        // admin list carries it
        var adminList = await admin.GetFromJsonAsync<List<AdminSpecialistDto>>("/admin/specialists");
        Assert.Equal("Juridic", Assert.Single(adminList!).Category);

        // and it reaches the student-facing event detail
        var ev = await admin.PostAsJsonAsync("/admin/events", new
        {
            name = "Zi", slug = "zi", startsOn = "2026-09-01", endsOn = "2026-09-01",
        });
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        await admin.PostAsJsonAsync($"/admin/events/{eventId}/specialists",
            new { specialistId, location = "Sala 1" });
        await admin.PostAsync($"/admin/events/{eventId}/publish", null);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var detail = await student.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>(
            "/events/zi");
        var specialists = detail!["specialists"].EnumerateArray().First();
        Assert.Equal("Juridic", specialists.GetProperty("category").GetString());
    }

    [Fact]
    public async Task A_specialist_created_without_a_category_defaults_to_social()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        var created = await admin.PostAsJsonAsync("/admin/specialists", new
        {
            fullName = "Cineva", specialty = "Necunoscut",
        });
        created.EnsureSuccessStatusCode();

        var list = await admin.GetFromJsonAsync<List<AdminSpecialistDto>>("/admin/specialists");
        Assert.Equal("Social", Assert.Single(list!).Category);
    }

    [Fact]
    public async Task An_admin_can_set_a_category_on_an_existing_specialist()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        var created = await admin.PostAsJsonAsync("/admin/specialists", new
        {
            fullName = "Dr. Ion", specialty = "Medic",
        });
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var patched = await admin.PatchAsJsonAsync($"/admin/specialists/{id}", new { category = 3 });
        Assert.Equal(HttpStatusCode.NoContent, patched.StatusCode);

        var list = await admin.GetFromJsonAsync<List<AdminSpecialistDto>>("/admin/specialists");
        Assert.Equal("Medical", Assert.Single(list!).Category);
    }

    [Fact]
    public async Task A_student_cannot_patch_a_specialist()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var created = await admin.PostAsJsonAsync("/admin/specialists",
            new { fullName = "X", specialty = "Y" });
        var id = await created.Content.ReadFromJsonAsync<Guid>();

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var response = await student.PatchAsJsonAsync($"/admin/specialists/{id}", new { category = 1 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_reset_a_students_password_and_the_new_one_works()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        // a student exists (SignedInClientAsync registers them)
        await _app.SignedInClientAsync("forgot@amicus.test");

        var reset = await admin.PostAsJsonAsync("/admin/users/reset-password", new
        {
            email = "forgot@amicus.test",
            newPassword = "brand-new-passphrase",
        });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // old password no longer works, new one does
        var anon = _app.CreateClient();
        var oldLogin = await anon.PostAsJsonAsync("/auth/login", new
        {
            email = "forgot@amicus.test", password = "correct-horse-battery",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);

        var newLogin = await anon.PostAsJsonAsync("/auth/login", new
        {
            email = "forgot@amicus.test", password = "brand-new-passphrase",
        });
        newLogin.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Admin_reset_on_an_unknown_email_is_404_and_a_weak_password_is_400()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        var unknown = await admin.PostAsJsonAsync("/admin/users/reset-password", new
        {
            email = "nobody@amicus.test", newPassword = "brand-new-passphrase",
        });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await _app.SignedInClientAsync("weak@amicus.test");
        var weak = await admin.PostAsJsonAsync("/admin/users/reset-password", new
        {
            email = "weak@amicus.test", newPassword = "short",
        });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }

    [Fact]
    public async Task Forgot_password_succeeds_even_with_no_email_configured()
    {
        // Identity always answers 200 so the endpoint can't probe which emails exist;
        // the unconfigured sender logs a warning and no-ops rather than 500ing.
        await _app.SignedInClientAsync("real@amicus.test");
        var anon = _app.CreateClient();

        var response = await anon.PostAsJsonAsync("/auth/forgotPassword", new
        {
            email = "real@amicus.test",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_student_cannot_reach_the_admin_reset_endpoint()
    {
        var student = await _app.SignedInClientAsync("student@amicus.test");
        var response = await student.PostAsJsonAsync("/admin/users/reset-password", new
        {
            email = "anyone@amicus.test", newPassword = "brand-new-passphrase",
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
