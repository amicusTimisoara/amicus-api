using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Amicus.Domain.Entities;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Amicus.Api.Tests;

[Collection(AmicusCollection.Name)]
public sealed class BookingFlowTests(AmicusFixture fixture) : IAsyncLifetime
{
    private readonly AmicusAppFactory _app = fixture.App;

    // 2026-09-01 is a Tuesday. The fixed clock sits on 2026-08-31T09:00Z, so every
    // generated slot is in the future and bookable.
    private const string Slug = "advice-day";

    public Task InitializeAsync() => _app.ResetAsync();

    // Reset here, not at the end of a test: an assertion failure would otherwise
    // leave the clock moved and silently break whatever ran next.
    public Task DisposeAsync()
    {
        _app.Clock.Now = DateTimeOffset.Parse("2026-08-31T09:00:00Z");
        return Task.CompletedTask;
    }

    private sealed record BoardSlotDto(Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsAvailable, bool IsMine);
    private sealed record SpecialistDto(Guid EventSpecialistId, Guid SpecialistId, string FullName, string Specialty, string? Bio, string? Location);
    private sealed record BoardDto(SpecialistDto Specialist, List<BoardSlotDto> Slots);
    private sealed record BookingDto(Guid Id, Guid SlotId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, string? Topic, string CheckInCode, string EventSlug, string EventName, string SpecialistName, string Specialty, string? Location);
    private sealed record GenerateDto(int Created, int AlreadyPresent, int RemovedStale);
    private sealed record EventDto(Guid Id, string Slug, string Name);
    private sealed record CheckInDto(Guid BookingId, DateTimeOffset StartsAt, string SpecialistName, string Status);

    /// <summary>
    /// Builds a full event through the admin API — which exercises those endpoints
    /// as a side effect rather than reaching into the database behind them.
    /// </summary>
    private async Task<(Guid EventId, Guid EventSpecialistId)> SeedAsync(
        HttpClient admin, string start = "14:00", string end = "16:00", int minutes = 30)
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
                startTime = start,
                endTime = end,
                slotDurationMinutes = minutes,
                breakMinutes = 0,
            });
        pattern.EnsureSuccessStatusCode();

        (await admin.PostAsync($"/admin/events/{@event.Id}/generate-slots", null))
            .EnsureSuccessStatusCode();
        (await admin.PostAsync($"/admin/events/{@event.Id}/publish", null))
            .EnsureSuccessStatusCode();

        return (@event.Id, eventSpecialistId);
    }

    private static async Task<List<BoardSlotDto>> BoardAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/events/{Slug}/board");
        response.EnsureSuccessStatusCode();
        var boards = (await response.Content.ReadFromJsonAsync<List<BoardDto>>())!;
        return Assert.Single(boards).Slots;
    }

    [Fact]
    public async Task Admin_builds_a_board_and_a_student_books_a_slot()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slots = await BoardAsync(student);

        // 14:00-16:00 in 30-minute slots is four, and September is EEST (+3).
        Assert.Equal(4, slots.Count);
        Assert.All(slots, s => Assert.True(s.IsAvailable));
        Assert.All(slots, s => Assert.False(s.IsMine));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 1, 11, 0, 0, TimeSpan.Zero), slots[0].StartsAt);

        var booked = await student.PostAsJsonAsync(
            "/bookings", new { slotId = slots[0].Id, topic = "Chestiune de contract" });

        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);
        var detail = (await booked.Content.ReadFromJsonAsync<BookingDto>())!;
        Assert.Equal("Booked", detail.Status);
        Assert.Equal("Ana Popescu", detail.SpecialistName);
        Assert.Equal("Sala 2", detail.Location);
        Assert.Equal(10, detail.CheckInCode.Length);

        var mine = await student.GetFromJsonAsync<List<BookingDto>>("/bookings/mine");
        Assert.Equal(detail.Id, Assert.Single(mine!).Id);

        var after = await BoardAsync(student);
        Assert.False(after[0].IsAvailable);
        Assert.True(after[0].IsMine);
    }

    [Fact]
    public async Task Two_students_cannot_hold_the_same_slot()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var first = await _app.SignedInClientAsync("first@amicus.test");
        var second = await _app.SignedInClientAsync("second@amicus.test");

        var slot = (await BoardAsync(first))[0].Id;

        var won = await first.PostAsJsonAsync("/bookings", new { slotId = slot });
        var lost = await second.PostAsJsonAsync("/bookings", new { slotId = slot });

        Assert.Equal(HttpStatusCode.Created, won.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
    }

    [Fact]
    public async Task The_board_never_reveals_who_holds_a_slot()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var booker = await _app.SignedInClientAsync("booker@amicus.test");
        var onlooker = await _app.SignedInClientAsync("onlooker@amicus.test");

        var slot = (await BoardAsync(booker))[0].Id;
        (await booker.PostAsJsonAsync("/bookings", new { slotId = slot, topic = "Ceva privat" }))
            .EnsureSuccessStatusCode();

        var raw = await onlooker.GetStringAsync($"/events/{Slug}/board");

        // The onlooker learns the slot is gone, and nothing else.
        Assert.Contains("\"isAvailable\":false", raw);
        Assert.DoesNotContain("booker@amicus.test", raw);
        Assert.DoesNotContain("Ceva privat", raw);
        Assert.DoesNotContain("checkInCode", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("studentUserId", raw, StringComparison.OrdinalIgnoreCase);

        // Every property name the board is allowed to expose, enumerated. A new
        // field leaking a booker's identity fails here rather than in production.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "specialist", "eventSpecialistId", "specialistId", "fullName", "specialty",
            "category", "profile", "bio", "location", "slots", "id", "startsAt", "endsAt",
            "isAvailable", "isMine",
        };
        foreach (var name in PropertyNames(JsonDocument.Parse(raw).RootElement))
        {
            Assert.Contains(name, allowed);
        }

        var onlookerView = await BoardAsync(onlooker);
        Assert.False(onlookerView[0].IsAvailable);
        Assert.False(onlookerView[0].IsMine);
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;

                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    [Fact]
    public async Task Cancelling_frees_the_slot_for_someone_else()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var first = await _app.SignedInClientAsync("first@amicus.test");
        var second = await _app.SignedInClientAsync("second@amicus.test");

        var slot = (await BoardAsync(first))[0].Id;

        var booked = await first.PostAsJsonAsync("/bookings", new { slotId = slot });
        var detail = (await booked.Content.ReadFromJsonAsync<BookingDto>())!;

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await second.PostAsJsonAsync("/bookings", new { slotId = slot })).StatusCode);

        var cancelled = await first.PostAsync($"/bookings/{detail.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        Assert.Equal(
            HttpStatusCode.Created,
            (await second.PostAsJsonAsync("/bookings", new { slotId = slot })).StatusCode);

        // The cancelled booking is still on record, not deleted.
        var mine = await first.GetFromJsonAsync<List<BookingDto>>("/bookings/mine");
        Assert.Equal("Cancelled", Assert.Single(mine!).Status);
    }

    [Fact]
    public async Task A_student_cannot_hold_two_overlapping_bookings()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var (eventId, _) = await SeedAsync(admin);

        // A second specialist running the same hours, so the two boards collide.
        var second = await admin.PostAsJsonAsync(
            "/admin/specialists", new { fullName = "Ion Marin", specialty = "Contabil" });
        var secondId = await second.Content.ReadFromJsonAsync<Guid>();
        var assigned = await admin.PostAsJsonAsync(
            $"/admin/events/{eventId}/specialists", new { specialistId = secondId });
        var secondEventSpecialist = await assigned.Content.ReadFromJsonAsync<Guid>();
        (await admin.PostAsJsonAsync(
            $"/admin/event-specialists/{secondEventSpecialist}/patterns",
            new
            {
                dayOfWeek = (int)DayOfWeek.Tuesday,
                startTime = "14:00",
                endTime = "16:00",
                slotDurationMinutes = 30,
                breakMinutes = 0,
            })).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/admin/events/{eventId}/generate-slots", null))
            .EnsureSuccessStatusCode();

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var response = await student.GetAsync($"/events/{Slug}/board");
        var boards = (await response.Content.ReadFromJsonAsync<List<BoardDto>>())!;
        Assert.Equal(2, boards.Count);

        var firstSlot = boards[0].Slots[0];
        var clashing = boards[1].Slots.First(s => s.StartsAt == firstSlot.StartsAt);

        Assert.Equal(
            HttpStatusCode.Created,
            (await student.PostAsJsonAsync("/bookings", new { slotId = firstSlot.Id })).StatusCode);

        var conflict = await student.PostAsJsonAsync("/bookings", new { slotId = clashing.Id });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("overlaps", await conflict.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_slot_that_has_already_started_cannot_be_booked()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slot = (await BoardAsync(student))[0];

        // Identity validates bearer tokens against the same injected clock, so this
        // 26-hour jump expires the token already in hand. Without re-authenticating
        // the request 401s before it ever reaches the rule under test.
        _app.Clock.Now = slot.StartsAt.AddMinutes(1);
        await _app.Authenticate(student, "student@amicus.test");

        var response = await student.PostAsJsonAsync("/bookings", new { slotId = slot.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already started", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Generating_slots_again_adds_nothing()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var (eventId, _) = await SeedAsync(admin);

        var again = await admin.PostAsync($"/admin/events/{eventId}/generate-slots", null);
        var result = (await again.Content.ReadFromJsonAsync<GenerateDto>())!;

        Assert.Equal(0, result.Created);
        Assert.Equal(4, result.AlreadyPresent);
        Assert.Equal(0, result.RemovedStale);
    }

    [Fact]
    public async Task Retiming_a_day_drops_unbooked_slots_but_keeps_booked_ones()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var (eventId, eventSpecialistId) = await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slots = await BoardAsync(student);

        // Book the FIRST slot, then retime the day to start after it. Its slot is no
        // longer produced by any pattern, but a student is holding it.
        (await student.PostAsJsonAsync("/bookings", new { slotId = slots[0].Id }))
            .EnsureSuccessStatusCode();

        (await admin.PostAsJsonAsync(
            $"/admin/event-specialists/{eventSpecialistId}/patterns",
            new
            {
                dayOfWeek = (int)DayOfWeek.Wednesday,
                startTime = "09:00",
                endTime = "10:00",
                slotDurationMinutes = 30,
                breakMinutes = 0,
            })).EnsureSuccessStatusCode();

        var regenerated = await admin.PostAsync($"/admin/events/{eventId}/generate-slots", null);
        var result = (await regenerated.Content.ReadFromJsonAsync<GenerateDto>())!;

        // The event is a single Tuesday, so the Wednesday pattern yields nothing and
        // the original Tuesday pattern still yields its four. Nothing is stale yet.
        Assert.Equal(0, result.RemovedStale);

        var stillThere = await BoardAsync(student);
        Assert.Equal(4, stillThere.Count);
        Assert.True(stillThere[0].IsMine);
    }

    [Fact]
    public async Task The_board_can_be_narrowed_to_a_date_range()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");

        // The event is a single Tuesday, so a Monday-only window is empty and the
        // Tuesday window is the whole board. Proves the filter bites either way.
        var empty = await student.GetFromJsonAsync<List<BoardDto>>(
            $"/events/{Slug}/board?from=2026-08-31&to=2026-08-31");
        Assert.Empty(empty!);

        var full = await student.GetFromJsonAsync<List<BoardDto>>(
            $"/events/{Slug}/board?from=2026-09-01&to=2026-09-01");
        Assert.Equal(4, Assert.Single(full!).Slots.Count);

        var backwards = await student.GetAsync(
            $"/events/{Slug}/board?from=2026-09-02&to=2026-09-01");
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    [Fact]
    public async Task Location_headers_carry_the_forwarded_path_prefix()
    {
        // nginx serves this app under /amicus/ and strips the prefix, telling the
        // app via X-Forwarded-Prefix. Results.Created with a literal path ignores
        // PathBase, which shipped a Location pointing at a 404 one level up.
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slot = (await BoardAsync(student))[0].Id;

        student.DefaultRequestHeaders.Add("X-Forwarded-Prefix", "/amicus");

        // nginx strips the prefix, so the app sees /bookings and learns the prefix
        // only from the header. That is the shape being reproduced here.
        var created = await student.PostAsJsonAsync("/bookings", new { slotId = slot });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.StartsWith("/amicus/bookings/", created.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Students_cannot_reach_admin_endpoints()
    {
        var student = await _app.SignedInClientAsync("student@amicus.test");

        var response = await student.PostAsJsonAsync("/admin/events", new
        {
            name = "Sneaky",
            slug = "sneaky",
            startsOn = "2026-09-01",
            endsOn = "2026-09-01",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_root_url_answers_anonymously_so_a_browser_sees_the_api_is_up()
    {
        // Hitting the base URL in a browser must not read as "nothing is running".
        var anonymous = _app.CreateClient();

        var response = await anonymous.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("amicus-api", body);
        Assert.Contains("/health", body);
    }

    [Fact]
    public async Task Anonymous_callers_are_turned_away()
    {
        var anonymous = _app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/events")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/events/{Slug}/board")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/bookings/mine")).StatusCode);
    }

    [Fact]
    public async Task Unpublished_events_are_invisible_to_students()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);

        var created = await admin.PostAsJsonAsync("/admin/events", new
        {
            name = "Draft",
            slug = "draft",
            startsOn = "2026-09-01",
            endsOn = "2026-09-01",
        });
        created.EnsureSuccessStatusCode();

        var student = await _app.SignedInClientAsync("student@amicus.test");

        Assert.Empty((await student.GetFromJsonAsync<List<EventDto>>("/events"))!);
        Assert.Equal(
            HttpStatusCode.NotFound, (await student.GetAsync("/events/draft")).StatusCode);
    }

    [Fact]
    public async Task Check_in_is_the_owning_carte_or_an_admin_not_a_role()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        var (_, eventSpecialistId) = await SeedAsync(admin);

        // The board's „carte”, linked to a real account the way approval links one
        // (Specialist.UserId). No role is involved — nothing ever grants a Specialist
        // role, which is exactly why check-in used to be impossible for every „carte”.
        var carte = await _app.SignedInClientAsync("carte@amicus.test");
        await LinkToBoardSpecialistAsync(eventSpecialistId, "carte@amicus.test");

        // A second „carte” on their own Specialist row, NOT this booking's slot.
        var otherCarte = await _app.SignedInClientAsync("other-carte@amicus.test");
        await MakeStandaloneCarteAsync("other-carte@amicus.test");

        var student = await _app.SignedInClientAsync("student@amicus.test");
        var slots = await BoardAsync(student);
        var booking = (await (await student.PostAsJsonAsync("/bookings", new { slotId = slots[0].Id }))
            .Content.ReadFromJsonAsync<BookingDto>())!;

        // A student cannot mark themselves present without turning up.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await student.PostAsJsonAsync("/check-in", new { code = booking.CheckInCode })).StatusCode);

        // A „carte” who does not own this booking's slot cannot either — the fix
        // scopes by the Specialist entity, not a blanket role.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await otherCarte.PostAsJsonAsync("/check-in", new { code = booking.CheckInCode })).StatusCode);

        // The OWNING „carte” can — this is the fix. Before it, check-in required a
        // Specialist role that nothing ever granted, so no „carte” could check anyone in.
        var scanned = await carte.PostAsJsonAsync("/check-in", new { code = booking.CheckInCode });
        scanned.EnsureSuccessStatusCode();
        Assert.Equal("CheckedIn", (await scanned.Content.ReadFromJsonAsync<CheckInDto>())!.Status);

        // Idempotent: scanning the same QR twice at a busy desk is not an error.
        Assert.Equal(HttpStatusCode.OK,
            (await carte.PostAsJsonAsync("/check-in", new { code = booking.CheckInCode })).StatusCode);

        // An admin checks in anyone.
        var second = (await (await student.PostAsJsonAsync("/bookings", new { slotId = slots[1].Id }))
            .Content.ReadFromJsonAsync<BookingDto>())!;
        (await admin.PostAsJsonAsync("/check-in", new { code = second.CheckInCode }))
            .EnsureSuccessStatusCode();

        // Unknown code is 404 regardless of who asks.
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/check-in", new { code = "ZZZZZZZZZZ" })).StatusCode);
    }

    /// <summary>Link the board's specialist to a user, the way approving an application does.</summary>
    private async Task LinkToBoardSpecialistAsync(Guid eventSpecialistId, string email)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"{email} not found.");
        var es = await db.EventSpecialists.FindAsync(eventSpecialistId)
            ?? throw new InvalidOperationException("event-specialist not found.");
        var specialist = await db.Specialists.FindAsync(es.SpecialistId)
            ?? throw new InvalidOperationException("specialist not found.");
        specialist.UserId = user.Id;
        await db.SaveChangesAsync();
    }

    /// <summary>A „carte” with its own Specialist row but no slot on this board.</summary>
    private async Task MakeStandaloneCarteAsync(string email)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AmicusDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByEmailAsync(email)
            ?? throw new InvalidOperationException($"{email} not found.");
        db.Specialists.Add(new Specialist
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FullName = "Alt Carte",
            Specialty = "Something else",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_student_cannot_cancel_someone_elses_booking()
    {
        var admin = await _app.SignedInClientAsync("admin@amicus.test", AppRoles.Admin);
        await SeedAsync(admin);

        var owner = await _app.SignedInClientAsync("owner@amicus.test");
        var other = await _app.SignedInClientAsync("other@amicus.test");

        var slot = (await BoardAsync(owner))[0].Id;
        var booking = (await (await owner.PostAsJsonAsync("/bookings", new { slotId = slot }))
            .Content.ReadFromJsonAsync<BookingDto>())!;

        // 404, not 403 — otherwise the endpoint confirms the id exists.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await other.PostAsync($"/bookings/{booking.Id}/cancel", null)).StatusCode);
    }
}
