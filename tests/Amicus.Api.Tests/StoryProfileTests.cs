using System.Net.Http.Json;
using System.Text.Json;
using Amicus.Infrastructure.Identity;

namespace Amicus.Api.Tests;

/// <summary>
/// The story axis — see <see cref="Amicus.Domain.StoryProfile"/> for why it is a
/// separate field rather than more values on <c>SpecialistCategory</c>.
///
/// These assert the pair travels together, because the whole point is that a
/// book can be one thing on each axis at once and a student can find them by
/// either.
/// </summary>
[Collection(AmicusCollection.Name)]
public sealed class StoryProfileTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record AdminSpecialistDto(
        Guid Id, string FullName, string Specialty, string Category, string? Profile,
        string? Bio, bool IsActive);
    private sealed record EventDto(Guid Id, string Slug, string Name);

    private async Task<Guid> PublishWithAsync(HttpClient admin, Guid specialistId, string slug)
    {
        var ev = await admin.PostAsJsonAsync("/admin/events", new
        {
            name = "Zi", slug, startsOn = "2026-09-01", endsOn = "2026-09-01",
        });
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        await admin.PostAsJsonAsync($"/admin/events/{eventId}/specialists",
            new { specialistId, location = "Sala 1" });
        await admin.PostAsync($"/admin/events/{eventId}/publish", null);
        return eventId;
    }

    [Fact]
    public async Task A_book_carries_both_axes_to_the_student()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        // A physician whose story is that they survived a tragedy: Medical on one
        // axis, Tragedie on the other. Collapsing these into one field would force
        // a choice that loses a student searching for the other.
        var created = await admin.PostAsJsonAsync("/admin/specialists", new
        {
            fullName = "Dr. Ana Popescu",
            specialty = "Medic de familie",
            category = 3, // Medical
            profile = 7,  // Tragedie
        });
        created.EnsureSuccessStatusCode();
        var specialistId = await created.Content.ReadFromJsonAsync<Guid>();

        var adminList = await admin.GetFromJsonAsync<List<AdminSpecialistDto>>("/admin/specialists");
        var row = Assert.Single(adminList!);
        Assert.Equal("Medical", row.Category);
        Assert.Equal("Tragedie", row.Profile);

        await PublishWithAsync(admin, specialistId, "zi");

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var detail = await student.GetFromJsonAsync<Dictionary<string, JsonElement>>("/events/zi");
        var specialist = detail!["specialists"].EnumerateArray().First();
        Assert.Equal("Medical", specialist.GetProperty("category").GetString());
        Assert.Equal("Tragedie", specialist.GetProperty("profile").GetString());
    }

    [Fact]
    public async Task A_book_without_a_profile_is_untagged_rather_than_guessed()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        // No profile supplied. Unlike Category — which falls back to Social — an
        // absent story stays absent: inventing one would put words in a real
        // person's mouth.
        var created = await admin.PostAsJsonAsync("/admin/specialists", new
        {
            fullName = "Ion Ionescu",
            specialty = "Contabil",
        });
        created.EnsureSuccessStatusCode();

        var adminList = await admin.GetFromJsonAsync<List<AdminSpecialistDto>>("/admin/specialists");
        Assert.Null(Assert.Single(adminList!).Profile);
    }

    [Fact]
    public async Task A_profile_can_be_added_later_without_touching_the_category()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        var created = await admin.PostAsJsonAsync("/admin/specialists", new
        {
            fullName = "Levis Nistor",
            specialty = "Pastor",
            category = 1, // Spiritual
        });
        var specialistId = await created.Content.ReadFromJsonAsync<Guid>();

        // Books are recruited before anyone agrees how to label their story, so
        // tagging is a later, separate edit — and must not disturb the category.
        var patched = await admin.PatchAsJsonAsync($"/admin/specialists/{specialistId}", new
        {
            profile = 10, // Pastor
        });
        patched.EnsureSuccessStatusCode();

        var adminList = await admin.GetFromJsonAsync<List<AdminSpecialistDto>>("/admin/specialists");
        var row = Assert.Single(adminList!);
        Assert.Equal("Pastor", row.Profile);
        Assert.Equal("Spiritual", row.Category);
    }
}
