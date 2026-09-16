using System.Security.Claims;
using Amicus.Api.Contracts;
using Amicus.Domain;
using Amicus.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Amicus.Api.Endpoints;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEvents(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/events").RequireAuthorization();

        group.MapGet("/", async (AmicusDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Events
                .Where(e => e.IsPublished)
                .OrderBy(e => e.StartsOn)
                .Select(e => new EventSummary(
                    e.Id, e.Slug, e.Name, e.StartsOn, e.EndsOn, e.TimeZoneId))
                .ToListAsync(ct)))
            .WithName("ListEvents")
            .WithSummary("Published events, soonest first.");

        group.MapGet("/{slug}", async (
            string slug, AmicusDbContext db, CancellationToken ct) =>
        {
            var detail = await db.Events
                .Where(e => e.Slug == slug && e.IsPublished)
                .Select(e => new EventDetail(
                    new EventSummary(e.Id, e.Slug, e.Name, e.StartsOn, e.EndsOn, e.TimeZoneId),
                    e.Specialists
                        .Where(es => es.Specialist!.IsActive)
                        .OrderBy(es => es.Specialist!.FullName)
                        .Select(es => new SpecialistSummary(
                            es.Id,
                            es.SpecialistId,
                            es.Specialist!.FullName,
                            es.Specialist.Specialty,
                            es.Specialist.Category.ToString(),
                            es.Specialist.Profile == null ? null : es.Specialist.Profile.ToString(),
                            es.Specialist.Bio,
                            es.Location))
                        .ToList()))
                .FirstOrDefaultAsync(ct);

            return detail is null ? Results.NotFound() : Results.Ok(detail);
        })
            .WithName("GetEvent")
            .WithSummary("One published event and the specialists attending it.");

        group.MapGet("/{slug}/board", async (
            string slug, AmicusDbContext db, ClaimsPrincipal user,
            DateOnly? from, DateOnly? to, CancellationToken ct) =>
        {
            var userId = user.Id();

            if (from is not null && to is not null && to < from)
            {
                return Results.BadRequest(new { error = "'to' precedes 'from'." });
            }

            var @event = await db.Events
                .Where(e => e.Slug == slug && e.IsPublished)
                .Select(e => new { e.Id })
                .FirstOrDefaultAsync(ct);

            if (@event is null)
            {
                return Results.NotFound();
            }

            // Projected straight to the wire shape. There is no path here that can
            // load who holds a slot: only whether SOMEONE does, and whether it is
            // the caller. That is the privacy guarantee, enforced by the query.
            // A whole multi-week event is a genuinely large response: 2016 slots
            // measured at 100 rps and a 305 MB peak on a Pi, against 263 rps and
            // 252 MB for a single week. Clients showing one day should say so.
            var fromInstant = from is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(from.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var toInstant = to is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(
                    to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            var rows = await db.Slots
                .Where(s => s.EventSpecialist!.EventId == @event.Id)
                .Where(s => fromInstant == null || s.StartsAt >= fromInstant)
                .Where(s => toInstant == null || s.StartsAt < toInstant)
                .OrderBy(s => s.StartsAt)
                .Select(s => new
                {
                    s.EventSpecialistId,
                    Specialist = new SpecialistSummary(
                        s.EventSpecialist!.Id,
                        s.EventSpecialist.SpecialistId,
                        s.EventSpecialist.Specialist!.FullName,
                        s.EventSpecialist.Specialist.Specialty,
                        s.EventSpecialist.Specialist.Category.ToString(),
                        s.EventSpecialist.Specialist.Profile == null
                            ? null
                            : s.EventSpecialist.Specialist.Profile.ToString(),
                        s.EventSpecialist.Specialist.Bio,
                        s.EventSpecialist.Location),
                    Slot = new BoardSlot(
                        s.Id,
                        s.StartsAt,
                        s.EndsAt,
                        !s.IsBlocked
                            && !s.Bookings.Any(b => b.Status != BookingStatus.Cancelled),
                        s.Bookings.Any(b =>
                            b.Status != BookingStatus.Cancelled && b.StudentUserId == userId)),
                })
                .ToListAsync(ct);

            var boards = rows
                .GroupBy(r => r.EventSpecialistId)
                .Select(g => new SpecialistBoard(
                    g.First().Specialist,
                    g.Select(r => r.Slot).ToList()))
                .OrderBy(b => b.Specialist.FullName)
                .ToList();

            return Results.Ok(boards);
        })
            .WithName("GetEventBoard")
            .WithSummary("The shared slot board: what is taken and when, never by whom.");

        return app;
    }
}
