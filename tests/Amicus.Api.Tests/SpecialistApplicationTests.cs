using System.Net;
using System.Net.Http.Json;
using Amicus.Infrastructure.Identity;

namespace Amicus.Api.Tests;

/// <summary>
/// Becoming a „carte”. The route is: ordinary account → application → a human on
/// the committee decides. These assert the parts that would quietly let someone
/// promote themselves, and the parts that would strand an applicant.
/// </summary>
[Collection(AmicusCollection.Name)]
public sealed class SpecialistApplicationTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record ApplicationDto(
        Guid Id, string FullName, string Phone, string Specialty, string Category,
        string? Profile, string Story, string Format, bool SpeaksEnglish,
        bool AcceptsSmallGroups, string Status, string? ReviewNote,
        DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt, Guid? SpecialistId);

    private sealed record AccountDto(
        string Email, string? DisplayName, string? PhotoUrl, bool IsEmailConfirmed, bool IsCarte);

    private static object Application(string name = "Carolina Ilie") => new
    {
        fullName = name,
        phone = "0712 345 678",
        specialty = "Medic de familie",
        category = 3,  // Medical
        profile = 7,   // Tragedie
        story = "Am pierdut totul la 40 de ani și am luat-o de la capăt într-un oraș nou.",
        format = 3,    // Ambele
        speaksEnglish = true,
        acceptsSmallGroups = true,
    };

    private static async Task<HttpResponseMessage> ApplyAsync(HttpClient c, string name = "Carolina Ilie") =>
        await c.PostAsJsonAsync("/account/specialist-application", Application(name));

    [Fact]
    public async Task An_approved_application_turns_the_account_into_a_carte()
    {
        var student = await _app.SignedInClientAsync("carolina@amicus.test");

        // Before applying, the account is an ordinary student — no ring.
        var before = await student.GetFromJsonAsync<AccountDto>("/account/me");
        Assert.False(before!.IsCarte);

        var submitted = await ApplyAsync(student);
        submitted.EnsureSuccessStatusCode();
        var application = await submitted.Content.ReadFromJsonAsync<ApplicationDto>();
        Assert.Equal("Pending", application!.Status);
        Assert.Null(application.SpecialistId);

        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var queue = await admin.GetFromJsonAsync<List<ApplicationDto>>("/admin/specialist-applications?status=Pending");
        Assert.Single(queue!);

        var approved = await admin.PostAsync(
            $"/admin/specialist-applications/{application.Id}/approve", null);
        approved.EnsureSuccessStatusCode();
        var result = await approved.Content.ReadFromJsonAsync<ApplicationDto>();
        Assert.Equal("Approved", result!.Status);
        Assert.NotNull(result.SpecialistId);

        // The whole point: the account is now a carte, which is what the clients
        // read to mark the avatar.
        var after = await student.GetFromJsonAsync<AccountDto>("/account/me");
        Assert.True(after!.IsCarte);

        // And the carte exists on the board, carrying both tag axes.
        var specialists = await admin.GetFromJsonAsync<List<Dictionary<string, object>>>("/admin/specialists");
        var row = Assert.Single(specialists!);
        Assert.Equal("Medical", row["category"].ToString());
        Assert.Equal("Tragedie", row["profile"].ToString());
    }

    [Fact]
    public async Task A_student_cannot_promote_themselves()
    {
        var student = await _app.SignedInClientAsync("student@amicus.test");
        var submitted = await ApplyAsync(student);
        var application = await submitted.Content.ReadFromJsonAsync<ApplicationDto>();

        // No role, no queue, no approving your own application.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.GetAsync("/admin/specialist-applications")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.PostAsync($"/admin/specialist-applications/{application!.Id}/approve", null)).StatusCode);

        var me = await student.GetFromJsonAsync<AccountDto>("/account/me");
        Assert.False(me!.IsCarte);
    }

    [Fact]
    public async Task Only_one_application_can_be_open_at_a_time()
    {
        var student = await _app.SignedInClientAsync("dublu@amicus.test");
        (await ApplyAsync(student)).EnsureSuccessStatusCode();

        var second = await ApplyAsync(student);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_rejection_carries_its_reason_back_and_lets_them_try_again()
    {
        var student = await _app.SignedInClientAsync("respins@amicus.test");
        var first = await (await ApplyAsync(student)).Content.ReadFromJsonAsync<ApplicationDto>();

        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var rejected = await admin.PostAsJsonAsync(
            $"/admin/specialist-applications/{first!.Id}/reject",
            new { note = "Ne-ar trebui mai multe detalii despre poveste." });
        rejected.EnsureSuccessStatusCode();

        // The applicant sees why — a refusal with no reason just invites a resend.
        var mine = await student.GetFromJsonAsync<ApplicationDto>("/account/specialist-application");
        Assert.Equal("Rejected", mine!.Status);
        Assert.Equal("Ne-ar trebui mai multe detalii despre poveste.", mine.ReviewNote);

        // Rejected does not block a better second attempt; the unique index is
        // filtered to Pending precisely so this works.
        var retry = await ApplyAsync(student);
        retry.EnsureSuccessStatusCode();

        // And "mine" is now the new attempt, not the old refusal.
        var latest = await student.GetFromJsonAsync<ApplicationDto>("/account/specialist-application");
        Assert.Equal("Pending", latest!.Status);
    }

    [Fact]
    public async Task An_application_can_only_be_decided_once()
    {
        var student = await _app.SignedInClientAsync("odata@amicus.test");
        var application = await (await ApplyAsync(student)).Content.ReadFromJsonAsync<ApplicationDto>();

        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        (await admin.PostAsync($"/admin/specialist-applications/{application!.Id}/approve", null))
            .EnsureSuccessStatusCode();

        // Approving twice would insert a second Specialist for the same account.
        var again = await admin.PostAsync($"/admin/specialist-applications/{application.Id}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var flipped = await admin.PostAsJsonAsync(
            $"/admin/specialist-applications/{application.Id}/reject", new { note = "nu" });
        Assert.Equal(HttpStatusCode.Conflict, flipped.StatusCode);
    }

    [Fact]
    public async Task An_existing_carte_is_not_asked_to_apply_again()
    {
        var student = await _app.SignedInClientAsync("deja@amicus.test");
        var application = await (await ApplyAsync(student)).Content.ReadFromJsonAsync<ApplicationDto>();

        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        (await admin.PostAsync($"/admin/specialist-applications/{application!.Id}/approve", null))
            .EnsureSuccessStatusCode();

        var again = await ApplyAsync(student);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task An_application_without_a_story_is_refused()
    {
        var student = await _app.SignedInClientAsync("gol@amicus.test");

        // The story is what a human actually reads; without it there is nothing
        // to decide on.
        var response = await student.PostAsJsonAsync("/account/specialist-application", new
        {
            fullName = "Fără Poveste",
            phone = "0700 000 000",
            specialty = "Contabil",
            category = 5,
            story = "   ",
            format = 2,
            speaksEnglish = false,
            acceptsSmallGroups = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Enum_fields_are_accepted_by_name_as_well_as_by_number()
    {
        var student = await _app.SignedInClientAsync("nume@amicus.test");

        // Every response SENDS these as names — Category.ToString() — so a client
        // that reads "Medical" and posts "Medical" back must not get a 400 for
        // using our own vocabulary. This failed in a real browser before
        // JsonStringEnumConverter was configured.
        var response = await student.PostAsJsonAsync("/account/specialist-application", new
        {
            fullName = "Carolina Ilie",
            phone = "0712 345 678",
            specialty = "Medic de familie",
            category = "Medical",
            profile = "Tragedie",
            story = "Am luat-o de la capăt.",
            format = "Online",
            speaksEnglish = true,
            acceptsSmallGroups = false,
        });

        response.EnsureSuccessStatusCode();
        var application = await response.Content.ReadFromJsonAsync<ApplicationDto>();
        Assert.Equal("Medical", application!.Category);
        Assert.Equal("Tragedie", application.Profile);
        Assert.Equal("Online", application.Format);
    }

    [Fact]
    public async Task Never_applying_is_not_an_error()
    {
        var student = await _app.SignedInClientAsync("nimic@amicus.test");
        var response = await student.GetAsync("/account/specialist-application");

        // 204, not 404: the account is fine, there is simply nothing to show.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
