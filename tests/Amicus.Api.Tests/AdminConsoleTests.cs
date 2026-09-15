using System.Net;
using System.Net.Http.Json;
using Amicus.Infrastructure.Identity;

namespace Amicus.Api.Tests;

/// <summary>
/// The admin-side reads and state changes an operator console needs.
///
/// The interesting cases are the two refusals: blocking a slot somebody holds,
/// and reaching any of this without the Admin role. Both are the kind of thing
/// that looks fine until the day it isn't.
/// </summary>
[Collection(AmicusCollection.Name)]
public sealed class AdminConsoleTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    // 2026-09-01 is a Tuesday; the fixed clock sits the day before, so every
    // generated slot is in the future and bookable.
    private const string Slug = "advice-day";

    public Task InitializeAsync() => _app.ResetAsync();

    public Task DisposeAsync()
    {
        _app.Clock.Now = DateTimeOffset.Parse("2026-08-31T09:00:00Z");
        return Task.CompletedTask;
    }

    private sealed record EventDto(Guid Id, string Slug, string Name);
    private sealed record AdminEventDto(
        Guid Id, string Slug, string Name, DateOnly StartsOn, DateOnly EndsOn,
        string TimeZoneId, bool IsPublished, int SpecialistCount, int SlotCount);
    private sealed record RosterDto(
        Guid EventSpecialistId, Guid SpecialistId, string FullName, string Specialty,
        string? Location, int PatternCount, int SlotCount, int BookedCount);
    private sealed record AdminSlotDto(
        Guid Id, Guid EventSpecialistId, string SpecialistName,
        DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsBlocked, bool HasLiveBooking);
    private sealed record BoardSlotDto(
        Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsAvailable, bool IsMine);
    private sealed record SpecialistDto(
        Guid EventSpecialistId, Guid SpecialistId, string FullName, string Specialty,
        string? Bio, string? Location);
    private sealed record BoardDto(SpecialistDto Specialist, List<BoardSlotDto> Slots);
    private sealed record BookingDto(Guid Id, Guid SlotId, string Status);

    private async Task<Guid> SeedAsync(HttpClient admin, bool publish = true)
    {
        var created = await admin.PostAsJsonAsync("/admin/events", new
        {
            name = "Advice Day",
            slug = Slug,
            startsOn = "2026-09-01",
            endsOn = "2026-09-01",
            timeZoneId = "Europe/Bucharest",
        });
        created.EnsureSuccessStatusCode();
        var @event = (await created.Content.ReadFromJsonAsync<EventDto>())!;

        var specialist = await admin.PostAsJsonAsync("/admin/specialists", new
        {
            fullName = "Ana Popescu",
            specialty = "Avocat",
            bio = (string?)null,
        });
        specialist.EnsureSuccessStatusCode();
        var specialistId = await specialist.Content.ReadFromJsonAsync<Guid>();

        var assigned = await admin.PostAsJsonAsync(
            $"/admin/events/{@event.Id}/specialists",
            new { specialistId, location = "Sala 2" });
        assigned.EnsureSuccessStatusCode();
        var eventSpecialistId = await assigned.Content.ReadFromJsonAsync<Guid>();

        var pattern = await admin.PostAsJsonAsync(
            $"/admin/event-specialists/{eventSpecialistId}/patterns",
            new
            {
                dayOfWeek = (int)DayOfWeek.Tuesday,
                startTime = "14:00",
                endTime = "16:00",
                slotDurationMinutes = 30,
                breakMinutes = 0,
            });
        pattern.EnsureSuccessStatusCode();

        (await admin.PostAsync($"/admin/events/{@event.Id}/generate-slots", null))
            .EnsureSuccessStatusCode();

        if (publish)
        {
            (await admin.PostAsync($"/admin/events/{@event.Id}/publish", null))
                .EnsureSuccessStatusCode();
        }

        return @event.Id;
    }

    private static async Task<List<BoardSlotDto>> BoardAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/events/{Slug}/board");
        response.EnsureSuccessStatusCode();
        var boards = (await response.Content.ReadFromJsonAsync<List<BoardDto>>())!;
        return Assert.Single(boards).Slots;
    }

    [Fact]
    public async Task Admin_listing_includes_drafts_that_students_cannot_see()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin, publish: false);

        var forAdmin = await admin.GetFromJsonAsync<List<AdminEventDto>>("/admin/events");
        var draft = Assert.Single(forAdmin!);
        Assert.False(draft.IsPublished);
        Assert.Equal(1, draft.SpecialistCount);
        Assert.Equal(4, draft.SlotCount);

        // The student-facing list filters on IsPublished, so the same event is
        // simply not there.
        var student = await _app.SignedInClientAsync("student@amicus.test");
        var forStudent = await student.GetFromJsonAsync<List<EventDto>>("/events");
        Assert.Empty(forStudent!);
    }

    [Fact]
    public async Task Roster_reports_pattern_slot_and_booked_counts()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var eventId = await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slots = await BoardAsync(student);
        (await student.PostAsJsonAsync("/bookings", new { slotId = slots[0].Id }))
            .EnsureSuccessStatusCode();

        var roster = await admin.GetFromJsonAsync<List<RosterDto>>(
            $"/admin/events/{eventId}/specialists");

        var entry = Assert.Single(roster!);
        Assert.Equal("Ana Popescu", entry.FullName);
        Assert.Equal("Sala 2", entry.Location);
        Assert.Equal(1, entry.PatternCount);
        Assert.Equal(4, entry.SlotCount);
        Assert.Equal(1, entry.BookedCount);
    }

    [Fact]
    public async Task Blocking_a_free_slot_takes_it_off_the_student_board()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var eventId = await SeedAsync(admin);
        var student = await _app.SignedInClientAsync("student@amicus.test");

        var adminSlots = await admin.GetFromJsonAsync<List<AdminSlotDto>>(
            $"/admin/events/{eventId}/slots");
        var target = adminSlots![0];
        Assert.False(target.IsBlocked);
        Assert.False(target.HasLiveBooking);

        var blocked = await admin.PostAsync($"/admin/slots/{target.Id}/block", null);
        Assert.Equal(HttpStatusCode.NoContent, blocked.StatusCode);

        // Still on the board — but no longer bookable, which is the whole point of
        // blocking rather than deleting.
        var board = await BoardAsync(student);
        var onBoard = board.Single(s => s.Id == target.Id);
        Assert.False(onBoard.IsAvailable);

        var refused = await student.PostAsJsonAsync("/bookings", new { slotId = target.Id });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var unblocked = await admin.PostAsync($"/admin/slots/{target.Id}/unblock", null);
        Assert.Equal(HttpStatusCode.NoContent, unblocked.StatusCode);

        var afterUnblock = await BoardAsync(student);
        Assert.True(afterUnblock.Single(s => s.Id == target.Id).IsAvailable);
    }

    [Fact]
    public async Task Blocking_a_booked_slot_is_refused_rather_than_stranding_the_student()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var eventId = await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slots = await BoardAsync(student);
        (await student.PostAsJsonAsync("/bookings", new { slotId = slots[0].Id }))
            .EnsureSuccessStatusCode();

        var refused = await admin.PostAsync($"/admin/slots/{slots[0].Id}/block", null);

        // Blocking does not cancel the booking, so allowing it would leave the
        // admin believing the slot is closed while the student still turns up.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var adminSlots = await admin.GetFromJsonAsync<List<AdminSlotDto>>(
            $"/admin/events/{eventId}/slots");
        var row = adminSlots!.Single(s => s.Id == slots[0].Id);
        Assert.False(row.IsBlocked);
        Assert.True(row.HasLiveBooking);
    }

    [Fact]
    public async Task Unpublishing_hides_the_event_without_cancelling_bookings()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var eventId = await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slots = await BoardAsync(student);
        var booked = await student.PostAsJsonAsync("/bookings", new { slotId = slots[0].Id });
        booked.EnsureSuccessStatusCode();
        var booking = (await booked.Content.ReadFromJsonAsync<BookingDto>())!;

        var hidden = await admin.PostAsync($"/admin/events/{eventId}/unpublish", null);
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);

        Assert.Empty((await student.GetFromJsonAsync<List<EventDto>>("/events"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/events/{Slug}")).StatusCode);

        // The student still has an appointment, and can still see its check-in code.
        var mine = await student.GetFromJsonAsync<List<BookingDto>>("/bookings/mine");
        Assert.Equal(booking.Id, Assert.Single(mine!).Id);
        Assert.Equal("Booked", mine![0].Status);
    }

    [Fact]
    public async Task Admin_reads_are_closed_to_ordinary_students()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var eventId = await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");

        foreach (var path in new[]
                 {
                     "/admin/events",
                     "/admin/specialists",
                     $"/admin/events/{eventId}/specialists",
                     $"/admin/events/{eventId}/slots",
                 })
        {
            var response = await student.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var block = await student.PostAsync($"/admin/slots/{Guid.NewGuid()}/block", null);
        Assert.Equal(HttpStatusCode.Forbidden, block.StatusCode);
    }
}
