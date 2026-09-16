using System.Security.Claims;
using Amicus.Api.Contracts;
using Amicus.Domain;
using Amicus.Domain.Entities;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Amicus.Api.Endpoints;

/// <summary>
/// A „carte” publishing their own availability — "fiecare «carte» își publică în
/// aplicație zilele și intervalele în care este disponibilă".
///
/// These create <see cref="Slot"/> rows directly, with a null
/// <see cref="Slot.SlotPatternId"/>. That link has always been nullable for
/// exactly this ("or added ad hoc"), so admin-authored <see cref="SlotPattern"/>s
/// keep their meaning — bulk setup for a whole event — while a „carte” adds the
/// individual intervals they can actually make.
///
/// Duration is chosen per interval rather than once on the application, so the
/// same person can offer twenty minutes one week and an hour the next.
/// </summary>
public static class CarteSlotEndpoints
{
    /// <summary>Anything longer than this is a workshop, not a conversation.</summary>
    private const int MaxDurationMinutes = 240;

    public static IEndpointRouteBuilder MapCarteSlots(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account/carte").RequireAuthorization();

        group.MapGet("/slots", async (
            [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
            UserManager<AppUser> users, AmicusDbContext db,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.NotFound();

            var specialist = await db.Specialists
                .FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
            if (specialist is null) return Results.Forbid();

            var fromInstant = from is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(from.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var toInstant = to is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            var rows = await db.Slots
                .AsNoTracking()
                .Where(s => s.EventSpecialist!.SpecialistId == specialist.Id)
                .Where(s => fromInstant == null || s.StartsAt >= fromInstant)
                .Where(s => toInstant == null || s.StartsAt < toInstant)
                .OrderBy(s => s.StartsAt)
                .Select(s => new CarteSlot(
                    s.Id,
                    s.StartsAt,
                    s.EndsAt,
                    s.Bookings.Any(b => b.Status != BookingStatus.Cancelled),
                    s.IsBlocked,
                    s.EventSpecialist!.Event!.Slug,
                    s.EventSpecialist.Event.Name))
                .ToListAsync(ct);

            return Results.Ok(rows);
        })
            .WithSummary("The intervals you have published, optionally narrowed to a date range.");

        group.MapPost("/slots", async (
            [FromBody] PublishSlotRequest request,
            UserManager<AppUser> users, AmicusDbContext db, TimeProvider clock,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.NotFound();

            var specialist = await db.Specialists
                .FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
            if (specialist is null) return Results.Forbid();

            if (request.DurationMinutes <= 0 || request.DurationMinutes > MaxDurationMinutes)
            {
                return Results.BadRequest(new
                {
                    error = $"durationMinutes must be between 1 and {MaxDurationMinutes}.",
                });
            }

            var startsAt = request.StartsAt.ToUniversalTime();
            if (startsAt <= clock.GetUtcNow())
            {
                return Results.BadRequest(new { error = "Cannot publish an interval in the past." });
            }

            var endsAt = startsAt.AddMinutes(request.DurationMinutes);

            // Which event does this fall in? The project runs a month at a time,
            // so a „carte” thinks "the 14th", not "which event is that". Resolve
            // it from the date rather than making the client guess.
            var day = DateOnly.FromDateTime(startsAt.UtcDateTime);
            var assignments = await db.EventSpecialists
                .Include(es => es.Event)
                .Where(es => es.SpecialistId == specialist.Id)
                .Where(es => es.Event!.StartsOn <= day && day <= es.Event.EndsOn)
                .ToListAsync(ct);

            if (assignments.Count == 0)
            {
                return Results.Conflict(new
                {
                    error = "You are not on the roster for any event covering that date.",
                });
            }

            if (assignments.Count > 1)
            {
                // Better to ask than to guess: publishing into the wrong event
                // would put the slot on a board the student never looks at.
                return Results.Conflict(new
                {
                    error = "That date falls in more than one event; an admin needs to resolve it.",
                });
            }

            var assignment = assignments[0];

            // Overlap, not just exact duplicates. The unique index on
            // (EventSpecialistId, StartsAt) would catch an identical start, but a
            // 30-minute slot starting inside an existing 60-minute one would slip
            // past it and double-book a real person.
            var clashes = await db.Slots
                .Where(s => s.EventSpecialist!.SpecialistId == specialist.Id)
                .Where(s => s.StartsAt < endsAt && startsAt < s.EndsAt)
                .AnyAsync(ct);

            if (clashes)
            {
                return Results.Conflict(new { error = "That interval overlaps one you already published." });
            }

            var slot = new Slot
            {
                Id = Guid.CreateVersion7(),
                EventSpecialistId = assignment.Id,
                SlotPatternId = null,
                StartsAt = startsAt,
                EndsAt = endsAt,
                IsBlocked = false,
            };

            db.Slots.Add(slot);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new CarteSlot(
                slot.Id, slot.StartsAt, slot.EndsAt, false, false,
                assignment.Event!.Slug, assignment.Event.Name));
        })
            .WithSummary("Publish one interval. You choose its start and its length.");

        group.MapDelete("/slots/{id:guid}", async (
            Guid id, UserManager<AppUser> users, AmicusDbContext db,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Results.NotFound();

            var specialist = await db.Specialists
                .FirstOrDefaultAsync(s => s.UserId == user.Id, ct);
            if (specialist is null) return Results.Forbid();

            var slot = await db.Slots
                .Include(s => s.Bookings)
                .Include(s => s.EventSpecialist)
                .FirstOrDefaultAsync(s => s.Id == id, ct);

            // 404 rather than 403 for someone else's slot: whether a given id
            // exists is not a „carte”'s business.
            if (slot is null || slot.EventSpecialist!.SpecialistId != specialist.Id)
            {
                return Results.NotFound();
            }

            if (slot.Bookings.Any(b => b.Status != BookingStatus.Cancelled))
            {
                // A student is expecting this meeting. Withdrawing it has to go
                // through a person, not a delete button.
                return Results.Conflict(new
                {
                    error = "A student has booked this interval. Contact the team to cancel it.",
                });
            }

            db.Slots.Remove(slot);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
            .WithSummary("Withdraw an interval nobody has booked yet.");

        return app;
    }
}
