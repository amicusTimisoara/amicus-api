using Microsoft.AspNetCore.Identity;
using Amicus.Api.Contracts;
using Amicus.Domain;
using Amicus.Domain.Entities;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Amicus.Api.Endpoints;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization(policy => policy.RequireRole(AppRoles.Admin));

        group.MapPost("/events", async (
            [FromBody] CreateEventRequest request, AmicusDbContext db,
            TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            if (request.EndsOn < request.StartsOn)
            {
                return Results.BadRequest(new { error = "endsOn precedes startsOn." });
            }

            var timeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId)
                ? "Europe/Bucharest"
                : request.TimeZoneId;

            // Rejected here rather than at slot-generation time, which is when a bad
            // zone would otherwise surface — long after the admin left this screen.
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
            {
                return Results.BadRequest(new { error = $"Unknown time zone '{timeZoneId}'." });
            }

            var @event = new Event
            {
                Id = Guid.CreateVersion7(),
                Name = request.Name.Trim(),
                Slug = request.Slug.Trim().ToLowerInvariant(),
                StartsOn = request.StartsOn,
                EndsOn = request.EndsOn,
                TimeZoneId = timeZoneId,
                IsPublished = false,
                CreatedAt = clock.GetUtcNow(),
            };

            db.Events.Add(@event);
            await db.SaveChangesAsync(ct);

            return CreatedAt.Path(http, $"/events/{@event.Slug}", new EventSummary(
                @event.Id, @event.Slug, @event.Name,
                @event.StartsOn, @event.EndsOn, @event.TimeZoneId));
        })
            .WithSummary("Create an event. Unpublished, so students cannot see it yet.");

        group.MapPost("/events/{eventId:guid}/publish", async (
            Guid eventId, AmicusDbContext db, CancellationToken ct) =>
        {
            var @event = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (@event is null)
            {
                return Results.NotFound();
            }

            @event.IsPublished = true;
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
            .WithSummary("Make an event visible to students.");

        group.MapPost("/specialists", async (
            [FromBody] CreateSpecialistRequest request, AmicusDbContext db,
            TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            var specialist = new Specialist
            {
                Id = Guid.CreateVersion7(),
                FullName = request.FullName.Trim(),
                Specialty = request.Specialty.Trim(),
                Category = request.Category ?? SpecialistCategory.Social,
                Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim(),
                IsActive = true,
                CreatedAt = clock.GetUtcNow(),
            };

            db.Specialists.Add(specialist);
            await db.SaveChangesAsync(ct);

            return CreatedAt.Path(http, $"/admin/specialists/{specialist.Id}", specialist.Id);
        })
            .WithSummary("Add a specialist. No account is created — they do not need one.");

        group.MapPatch("/specialists/{specialistId:guid}", async (
            Guid specialistId, [FromBody] UpdateSpecialistRequest request,
            AmicusDbContext db, CancellationToken ct) =>
        {
            var specialist = await db.Specialists
                .FirstOrDefaultAsync(s => s.Id == specialistId, ct);

            if (specialist is null)
            {
                return Results.NotFound();
            }

            // Only the fields the caller sent are touched — a null means "leave it".
            if (request.FullName is not null)
            {
                specialist.FullName = request.FullName.Trim();
            }

            if (request.Specialty is not null)
            {
                specialist.Specialty = request.Specialty.Trim();
            }

            if (request.Bio is not null)
            {
                specialist.Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim();
            }

            if (request.Category is not null)
            {
                specialist.Category = request.Category.Value;
            }

            if (request.IsActive is not null)
            {
                specialist.IsActive = request.IsActive.Value;
            }

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
            .WithSummary(
                "Edit a specialist. Only the fields present in the body change — the "
                + "way to set a category on one created before the field existed.");

        group.MapPost("/events/{eventId:guid}/specialists", async (
            Guid eventId, [FromBody] AssignSpecialistRequest request,
            AmicusDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var exists = await db.Events.AnyAsync(e => e.Id == eventId, ct)
                && await db.Specialists.AnyAsync(s => s.Id == request.SpecialistId, ct);

            if (!exists)
            {
                return Results.NotFound(new { error = "No such event or specialist." });
            }

            if (await db.EventSpecialists.AnyAsync(
                    es => es.EventId == eventId && es.SpecialistId == request.SpecialistId, ct))
            {
                return Results.Conflict(
                    new { error = "That specialist is already assigned to this event." });
            }

            var assignment = new EventSpecialist
            {
                Id = Guid.CreateVersion7(),
                EventId = eventId,
                SpecialistId = request.SpecialistId,
                Location = string.IsNullOrWhiteSpace(request.Location)
                    ? null
                    : request.Location.Trim(),
            };

            db.EventSpecialists.Add(assignment);
            await db.SaveChangesAsync(ct);

            return CreatedAt.Path(
                http, $"/admin/event-specialists/{assignment.Id}", assignment.Id);
        })
            .WithSummary("Put a specialist on an event's roster.");

        group.MapPost("/event-specialists/{eventSpecialistId:guid}/patterns", async (
            Guid eventSpecialistId, [FromBody] CreateSlotPatternRequest request,
            AmicusDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var assignment = await db.EventSpecialists
                .Include(es => es.Event)
                .FirstOrDefaultAsync(es => es.Id == eventSpecialistId, ct);

            if (assignment?.Event is null)
            {
                return Results.NotFound();
            }

            var pattern = new SlotPattern
            {
                Id = Guid.CreateVersion7(),
                EventSpecialistId = eventSpecialistId,
                DayOfWeek = request.DayOfWeek,
                StartTime = request.StartTime,
                EndTime = request.EndTime,
                SlotDurationMinutes = request.SlotDurationMinutes,
                BreakMinutes = request.BreakMinutes,
            };

            // Validated by expanding it: the planner already owns every rule about
            // what a sane pattern is, so it is the single source of truth rather
            // than a second copy of the checks living here.
            try
            {
                SlotPlanner.Expand(assignment.Event, pattern);
            }
            catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            db.SlotPatterns.Add(pattern);
            await db.SaveChangesAsync(ct);

            return CreatedAt.Path(http, $"/admin/patterns/{pattern.Id}", pattern.Id);
        })
            .WithSummary("Assign a recurring availability rule to a specialist.");

        group.MapPost("/events/{eventId:guid}/generate-slots", async (
            Guid eventId, AmicusDbContext db, CancellationToken ct) =>
        {
            var @event = await db.Events
                .Include(e => e.Specialists).ThenInclude(es => es.Patterns)
                .FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (@event is null)
            {
                return Results.NotFound();
            }

            var created = 0;
            var alreadyPresent = 0;
            var removedStale = 0;

            foreach (var assignment in @event.Specialists)
            {
                var planned = SlotPlanner.ExpandAll(@event, assignment.Patterns);
                var plannedStarts = planned.Select(p => p.StartsAt).ToHashSet();

                var existing = await db.Slots
                    .Where(s => s.EventSpecialistId == assignment.Id)
                    .Select(s => new { s.Id, s.StartsAt, HasBookings = s.Bookings.Any() })
                    .ToListAsync(ct);

                var existingStarts = existing.Select(e => e.StartsAt).ToHashSet();

                foreach (var slot in planned)
                {
                    if (existingStarts.Contains(slot.StartsAt))
                    {
                        alreadyPresent++;
                        continue;
                    }

                    db.Slots.Add(new Slot
                    {
                        Id = Guid.CreateVersion7(),
                        EventSpecialistId = assignment.Id,
                        StartsAt = slot.StartsAt,
                        EndsAt = slot.EndsAt,
                    });

                    created++;
                }

                // An edited pattern leaves slots behind that it no longer produces.
                // Those are dropped ONLY if nobody ever booked them — a slot with any
                // booking history stays, so a student's record is never silently
                // deleted by an admin retiming the day.
                var stale = existing
                    .Where(e => !plannedStarts.Contains(e.StartsAt) && !e.HasBookings)
                    .Select(e => e.Id)
                    .ToList();

                if (stale.Count > 0)
                {
                    removedStale += await db.Slots
                        .Where(s => stale.Contains(s.Id))
                        .ExecuteDeleteAsync(ct);
                }
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new GenerateSlotsResult(created, alreadyPresent, removedStale));
        })
            .WithSummary(
                "Expand every pattern into bookable slots. Safe to re-run: existing "
                + "slots are left alone and booked ones are never removed.");

        // ---------------------------------------------------------------------
        // Reads. Everything above creates; without these an admin console has no
        // way to show what already exists, and no way to learn a slot's id in
        // order to block it.
        // ---------------------------------------------------------------------

        group.MapGet("/events", async (AmicusDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Events
                .OrderByDescending(e => e.StartsOn)
                .Select(e => new AdminEventSummary(
                    e.Id, e.Slug, e.Name, e.StartsOn, e.EndsOn, e.TimeZoneId, e.IsPublished,
                    e.Specialists.Count,
                    e.Specialists.SelectMany(es => es.Slots).Count()))
                .ToListAsync(ct)))
            .WithSummary(
                "Every event, drafts included. Newest first, because the one being "
                + "set up is the one being looked for.");

        group.MapGet("/specialists", async (AmicusDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Specialists
                .OrderBy(s => s.FullName)
                .Select(s => new AdminSpecialistSummary(
                    s.Id, s.FullName, s.Specialty, s.Category.ToString(), s.Bio, s.IsActive))
                .ToListAsync(ct)))
            .WithSummary("Every specialist on record, for assigning to an event.");

        group.MapGet("/events/{eventId:guid}/specialists", async (
            Guid eventId, AmicusDbContext db, CancellationToken ct) =>
        {
            if (!await db.Events.AnyAsync(e => e.Id == eventId, ct))
            {
                return Results.NotFound();
            }

            return Results.Ok(await db.EventSpecialists
                .Where(es => es.EventId == eventId)
                .OrderBy(es => es.Specialist!.FullName)
                .Select(es => new AdminRosterEntry(
                    es.Id,
                    es.SpecialistId,
                    es.Specialist!.FullName,
                    es.Specialist.Specialty,
                    es.Location,
                    es.Patterns.Count,
                    es.Slots.Count,
                    es.Slots.Count(s =>
                        s.Bookings.Any(b => b.Status != BookingStatus.Cancelled))))
                .ToListAsync(ct));
        })
            .WithSummary("Who is on this event's roster, with pattern and slot counts.");

        group.MapGet("/events/{eventId:guid}/slots", async (
            Guid eventId, AmicusDbContext db, DateOnly? from, DateOnly? to,
            CancellationToken ct) =>
        {
            if (from is not null && to is not null && to < from)
            {
                return Results.BadRequest(new { error = "'to' precedes 'from'." });
            }

            if (!await db.Events.AnyAsync(e => e.Id == eventId, ct))
            {
                return Results.NotFound();
            }

            // Same range-narrowing advice as the student board: a whole multi-week
            // event is thousands of rows, and an admin screen shows one day or week.
            var fromInstant = from is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(from.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var toInstant = to is null
                ? (DateTimeOffset?)null
                : new DateTimeOffset(
                    to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

            return Results.Ok(await db.Slots
                .Where(s => s.EventSpecialist!.EventId == eventId)
                .Where(s => fromInstant == null || s.StartsAt >= fromInstant)
                .Where(s => toInstant == null || s.StartsAt < toInstant)
                .OrderBy(s => s.StartsAt)
                .Select(s => new AdminSlot(
                    s.Id,
                    s.EventSpecialistId,
                    s.EventSpecialist!.Specialist!.FullName,
                    s.StartsAt,
                    s.EndsAt,
                    s.IsBlocked,
                    s.Bookings.Any(b => b.Status != BookingStatus.Cancelled)))
                .ToListAsync(ct));
        })
            .WithSummary("This event's slots, drafts included. Narrow with from/to.");

        // ---------------------------------------------------------------------
        // State changes that were modelled but never reachable.
        // ---------------------------------------------------------------------

        group.MapPost("/events/{eventId:guid}/unpublish", async (
            Guid eventId, AmicusDbContext db, CancellationToken ct) =>
        {
            var @event = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct);

            if (@event is null)
            {
                return Results.NotFound();
            }

            @event.IsPublished = false;
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
            .WithSummary(
                "Hide an event from students again. Existing bookings are NOT "
                + "cancelled — the students holding them still have an appointment.");

        group.MapPost("/slots/{slotId:guid}/block", async (
            Guid slotId, AmicusDbContext db, CancellationToken ct) =>
        {
            var slot = await db.Slots
                .Include(s => s.Bookings)
                .FirstOrDefaultAsync(s => s.Id == slotId, ct);

            if (slot is null)
            {
                return Results.NotFound();
            }

            // Refused rather than silently allowed. Blocking only removes a slot
            // from the board; it does not cancel the booking on it. Letting this
            // through would leave an admin believing the slot is closed while a
            // student still turns up for it — cancel first, then block.
            if (slot.Bookings.Any(b => b.Status != BookingStatus.Cancelled))
            {
                return Results.Conflict(new
                {
                    error = "That slot is booked. Cancel the booking before blocking it.",
                });
            }

            slot.IsBlocked = true;
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
            .WithSummary(
                "Take a free slot off the board without deleting it, so a cancelled "
                + "booking's audit trail survives.");

        group.MapPost("/slots/{slotId:guid}/unblock", async (
            Guid slotId, AmicusDbContext db, CancellationToken ct) =>
        {
            var slot = await db.Slots.FirstOrDefaultAsync(s => s.Id == slotId, ct);

            if (slot is null)
            {
                return Results.NotFound();
            }

            slot.IsBlocked = false;
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
            .WithSummary("Put a blocked slot back on the board.");

        group.MapPost("/users/reset-password", async (
            [FromBody] AdminResetPasswordRequest request,
            UserManager<AppUser> users,
            CancellationToken ct) =>
        {
            // The human fallback for when email delivery is unavailable or a student
            // can't receive it: an admin sets a new password directly. Goes through a
            // reset token rather than a raw hash write, so every Identity password
            // rule and the security-stamp bump still apply.
            var user = await users.FindByEmailAsync(request.Email);

            if (user is null)
            {
                return Results.NotFound(new { error = "No account with that email." });
            }

            var token = await users.GeneratePasswordResetTokenAsync(user);
            var result = await users.ResetPasswordAsync(user, token, request.NewPassword);

            if (!result.Succeeded)
            {
                return Results.ValidationProblem(result.Errors.ToDictionary(
                    e => e.Code, e => new[] { e.Description }));
            }

            return Results.NoContent();
        })
            .WithSummary(
                "Set a student's password directly — the fallback for when self-service "
                + "reset email is unavailable.");

        return app;
    }
}
