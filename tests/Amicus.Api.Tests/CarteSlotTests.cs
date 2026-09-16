using System.Net;
using System.Net.Http.Json;
using Amicus.Infrastructure.Identity;

namespace Amicus.Api.Tests;

/// <summary>
/// A „carte” publishing their own availability.
///
/// The rules worth guarding are the ones that would put a real person in an
/// impossible position: two students booked into the same half hour, or a
/// meeting withdrawn from under someone who is already expecting it.
/// </summary>
[Collection(AmicusCollection.Name)]
public sealed class CarteSlotTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record EventDto(Guid Id, string Slug, string Name);
    private sealed record SlotDto(
        Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsBooked,
        bool IsBlocked, string EventSlug, string EventName);
    private sealed record ApplicationDto(Guid Id, string Status, Guid? SpecialistId);

    /// <summary>The fixed test clock sits at 2026-08-31T09:00Z, so publish ahead of it.</summary>
    private static readonly DateTimeOffset Monday =
        new(2026, 9, 7, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Registers a carte, approves them, and puts them on an event roster.</summary>
    private async Task<(HttpClient Carte, HttpClient Admin, Guid EventId)> SetUpCarteAsync()
    {
        var carte = await _app.SignedInClientAsync("carte@amicus.test");
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        var submitted = await carte.PostAsJsonAsync("/account/specialist-application", new
        {
            fullName = "Carolina Ilie",
            phone = "0712 345 678",
            specialty = "Medic de familie",
            category = "Medical",
            story = "Am luat-o de la capăt.",
            format = "Ambele",
            speaksEnglish = true,
            acceptsSmallGroups = true,
        });
        submitted.EnsureSuccessStatusCode();
        var application = await submitted.Content.ReadFromJsonAsync<ApplicationDto>();

        var approved = await admin.PostAsync(
            $"/admin/specialist-applications/{application!.Id}/approve", null);
        approved.EnsureSuccessStatusCode();
        var specialistId = (await approved.Content.ReadFromJsonAsync<ApplicationDto>())!.SpecialistId;

        var ev = await admin.PostAsJsonAsync("/admin/events", new
        {
            name = "Septembrie", slug = "septembrie",
            startsOn = "2026-09-01", endsOn = "2026-09-30",
        });
        var eventId = (await ev.Content.ReadFromJsonAsync<EventDto>())!.Id;
        await admin.PostAsJsonAsync($"/admin/events/{eventId}/specialists",
            new { specialistId, location = "Sala 1" });

        return (carte, admin, eventId);
    }

    private static Task<HttpResponseMessage> PublishAsync(
        HttpClient carte, DateTimeOffset startsAt, int minutes) =>
        carte.PostAsJsonAsync("/account/carte/slots",
            new { startsAt, durationMinutes = minutes });

    [Fact]
    public async Task A_carte_publishes_an_interval_and_sees_it_on_their_calendar()
    {
        var (carte, _, _) = await SetUpCarteAsync();

        var published = await PublishAsync(carte, Monday, 30);
        published.EnsureSuccessStatusCode();
        var slot = await published.Content.ReadFromJsonAsync<SlotDto>();
        Assert.Equal(Monday, slot!.StartsAt);
        Assert.Equal(Monday.AddMinutes(30), slot.EndsAt);
        Assert.False(slot.IsBooked);
        Assert.Equal("septembrie", slot.EventSlug);

        var mine = await carte.GetFromJsonAsync<List<SlotDto>>("/account/carte/slots");
        Assert.Single(mine!);
    }

    [Fact]
    public async Task Length_is_chosen_per_interval()
    {
        var (carte, _, _) = await SetUpCarteAsync();

        // The same person, two different lengths on the same day. This is why
        // duration is not asked for once on the application.
        (await PublishAsync(carte, Monday, 20)).EnsureSuccessStatusCode();
        (await PublishAsync(carte, Monday.AddHours(2), 60)).EnsureSuccessStatusCode();

        var mine = await carte.GetFromJsonAsync<List<SlotDto>>("/account/carte/slots");
        Assert.Equal(
            new[] { 20.0, 60.0 },
            mine!.Select(s => (s.EndsAt - s.StartsAt).TotalMinutes).ToArray());
    }

    [Fact]
    public async Task Overlapping_intervals_are_refused()
    {
        var (carte, _, _) = await SetUpCarteAsync();

        (await PublishAsync(carte, Monday, 60)).EnsureSuccessStatusCode();

        // Starts INSIDE the existing hour. The unique index on (eventSpecialist,
        // startsAt) would not catch this, and two students would be booked into
        // the same half hour with one real person.
        var overlapping = await PublishAsync(carte, Monday.AddMinutes(30), 30);
        Assert.Equal(HttpStatusCode.Conflict, overlapping.StatusCode);

        // Butting up against the end is fine — that is back-to-back, not a clash.
        (await PublishAsync(carte, Monday.AddMinutes(60), 30)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_past_and_absurd_lengths_are_refused()
    {
        var (carte, _, _) = await SetUpCarteAsync();

        var past = await PublishAsync(carte, new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero), 30);
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(carte, Monday, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(carte, Monday, 600)).StatusCode);
    }

    [Fact]
    public async Task A_date_outside_every_event_is_refused_with_a_reason()
    {
        var (carte, _, _) = await SetUpCarteAsync();

        // October, when the roster only covers September.
        var outside = await PublishAsync(carte, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero), 30);
        Assert.Equal(HttpStatusCode.Conflict, outside.StatusCode);
    }

    [Fact]
    public async Task An_unbooked_interval_can_be_withdrawn_but_a_booked_one_cannot()
    {
        var (carte, admin, eventId) = await SetUpCarteAsync();

        var free = await (await PublishAsync(carte, Monday, 30)).Content.ReadFromJsonAsync<SlotDto>();
        var taken = await (await PublishAsync(carte, Monday.AddHours(1), 30)).Content.ReadFromJsonAsync<SlotDto>();

        // A slot only reaches a student's board once the event is published.
        await admin.PostAsync($"/admin/events/{eventId}/publish", null);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var booked = await student.PostAsJsonAsync("/bookings", new { slotId = taken!.Id });
        booked.EnsureSuccessStatusCode();

        // Unbooked: yours to withdraw.
        Assert.Equal(HttpStatusCode.NoContent,
            (await carte.DeleteAsync($"/account/carte/slots/{free!.Id}")).StatusCode);

        // Booked: a student is expecting this, so it goes through a person.
        Assert.Equal(HttpStatusCode.Conflict,
            (await carte.DeleteAsync($"/account/carte/slots/{taken.Id}")).StatusCode);

        var mine = await carte.GetFromJsonAsync<List<SlotDto>>("/account/carte/slots");
        var remaining = Assert.Single(mine!);
        Assert.True(remaining.IsBooked);
    }

    [Fact]
    public async Task A_student_who_is_not_a_carte_cannot_publish_anything()
    {
        var student = await _app.SignedInClientAsync("doar-student@amicus.test");

        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(student, Monday, 30)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.GetAsync("/account/carte/slots")).StatusCode);
    }

    [Fact]
    public async Task One_carte_cannot_withdraw_another_ones_interval()
    {
        var (carte, admin, _) = await SetUpCarteAsync();
        var slot = await (await PublishAsync(carte, Monday, 30)).Content.ReadFromJsonAsync<SlotDto>();

        // A second carte, on the same roster.
        var other = await _app.SignedInClientAsync("alta-carte@amicus.test");
        var submitted = await other.PostAsJsonAsync("/account/specialist-application", new
        {
            fullName = "Levis Nistor", phone = "0700 111 222", specialty = "Pastor",
            category = "Spiritual", story = "O poveste.", format = "Fizic",
            speaksEnglish = false, acceptsSmallGroups = true,
        });
        var application = await submitted.Content.ReadFromJsonAsync<ApplicationDto>();
        await admin.PostAsync($"/admin/specialist-applications/{application!.Id}/approve", null);

        // 404, not 403 — whether that id exists is not their business.
        Assert.Equal(HttpStatusCode.NotFound,
            (await other.DeleteAsync($"/account/carte/slots/{slot!.Id}")).StatusCode);
    }
}
