using System.Security.Claims;
using Amicus.Api.Contracts;
using Amicus.Domain;
using Amicus.Domain.Entities;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Amicus.Api.Endpoints;

public static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookings(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bookings").RequireAuthorization();

        group.MapPost("/", async (
            [FromBody] CreateBookingRequest request,
            AmicusDbContext db,
            ClaimsPrincipal user,
            TimeProvider clock,
            HttpContext http,
            CancellationToken ct) =>
        {
            var userId = user.Id();
            var now = clock.GetUtcNow();

            var slot = await db.Slots
                .Include(s => s.EventSpecialist!).ThenInclude(es => es.Event)
                .FirstOrDefaultAsync(s => s.Id == request.SlotId, ct);

            if (slot is null || slot.EventSpecialist?.Event is null
                || !slot.EventSpecialist.Event.IsPublished)
            {
                return Results.NotFound(new { error = "No such slot." });
            }

            if (slot.IsBlocked)
            {
                return Results.Conflict(new { error = "That slot is not open for booking." });
            }

            if (slot.StartsAt <= now)
            {
                return Results.BadRequest(new { error = "That slot has already started." });
            }

            // A student cannot be in two places at once. Checked before the insert
            // because, unlike the slot itself, this is not something the database
            // can express as a constraint.
            var clashes = await db.Bookings
                .Where(b => b.StudentUserId == userId && b.Status != BookingStatus.Cancelled)
                .AnyAsync(b => b.Slot!.StartsAt < slot.EndsAt && slot.StartsAt < b.Slot.EndsAt, ct);

            if (clashes)
            {
                return Results.Conflict(
                    new { error = "You already have a booking that overlaps this time." });
            }

            var booking = new Booking
            {
                Id = Guid.CreateVersion7(),
                SlotId = slot.Id,
                StudentUserId = userId,
                Status = BookingStatus.Booked,
                Topic = string.IsNullOrWhiteSpace(request.Topic) ? null : request.Topic.Trim(),
                CheckInCode = CheckInCode.New(),
                CreatedAt = now,
            };

            db.Bookings.Add(booking);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (
                UniqueViolation.On(ex, UniqueViolation.LiveBookingPerSlot))
            {
                // Someone committed first, in the gap between our availability read
                // and this insert. The database is the arbiter; we just report it.
                return Results.Conflict(
                    new { error = "Someone just took that slot. Pick another." });
            }

            var detail = await LoadDetailAsync(db, booking.Id, ct);

            return CreatedAt.Path(http, $"/bookings/{booking.Id}", detail);
        })
            .WithName("CreateBooking")
            .WithSummary("Take a free slot.");

        group.MapGet("/mine", async (
            AmicusDbContext db, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await db.Bookings
                .Where(b => b.StudentUserId == user.Id())
                .OrderBy(b => b.Slot!.StartsAt)
                .ToDetail()
                .ToListAsync(ct)))
            .WithName("ListMyBookings")
            .WithSummary("The caller's own bookings, including their check-in codes.");

        group.MapPost("/{id:guid}/cancel", async (
            Guid id, AmicusDbContext db, ClaimsPrincipal user, TimeProvider clock,
            CancellationToken ct) =>
        {
            var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == id, ct);

            // Same answer for "does not exist" and "belongs to someone else", so the
            // endpoint cannot be used to discover other people's booking ids.
            if (booking is null || booking.StudentUserId != user.Id())
            {
                return Results.NotFound();
            }

            if (booking.Status != BookingStatus.Booked)
            {
                return Results.Conflict(
                    new { error = $"Booking is {booking.Status} and cannot be cancelled." });
            }

            booking.Status = BookingStatus.Cancelled;
            booking.CancelledAt = clock.GetUtcNow();

            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        })
            .WithName("CancelBooking")
            .WithSummary("Cancel your own booking, freeing the slot for someone else.");

        // Check-in is the specialist's or an admin's action, not the student's:
        // otherwise a student could mark themselves present without turning up.
        app.MapPost("/check-in", async (
            [FromBody] CheckInRequest request,
            AmicusDbContext db,
            ClaimsPrincipal user,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var code = request.Code?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(code))
            {
                return Results.BadRequest(new { error = "code is required." });
            }

            var booking = await db.Bookings
                .Include(b => b.Slot!).ThenInclude(s => s.EventSpecialist!)
                    .ThenInclude(es => es.Specialist)
                .FirstOrDefaultAsync(b => b.CheckInCode == code, ct);

            if (booking is null)
            {
                return Results.NotFound(new { error = "Unknown check-in code." });
            }

            // Admins check in anyone; a „carte” checks in only bookings on their own
            // slots. Deliberately an entity check (Specialist.UserId), not a role: a
            // just-approved „carte” gets a Specialist row but no new token, and a
            // stateless bearer token would not carry a Specialist role claim until the
            // next sign-in. There is no such role anyway — nothing ever grants it.
            if (!user.IsInRole(AppRoles.Admin)
                && booking.Slot?.EventSpecialist?.Specialist?.UserId != user.Id())
            {
                return Results.Forbid();
            }

            if (booking.Status == BookingStatus.CheckedIn)
            {
                // Idempotent: scanning the same QR twice is a normal accident at a
                // busy desk, not an error worth refusing.
                return Results.Ok(Result(booking));
            }

            if (booking.Status != BookingStatus.Booked)
            {
                return Results.Conflict(
                    new { error = $"Booking is {booking.Status}." });
            }

            booking.Status = BookingStatus.CheckedIn;
            booking.CheckedInAt = clock.GetUtcNow();

            await db.SaveChangesAsync(ct);

            return Results.Ok(Result(booking));

            static CheckInResult Result(Booking b) => new(
                b.Id,
                b.Slot!.StartsAt,
                b.Slot.EventSpecialist!.Specialist!.FullName,
                b.Status.ToString());
        })
            .RequireAuthorization()
            .WithName("CheckIn")
            .WithSummary("Mark a booking as attended, from its QR code.");

        return app;
    }

    /// <summary>Projects to the wire shape. Filter BEFORE calling this.</summary>
    private static IQueryable<BookingDetail> ToDetail(this IQueryable<Booking> bookings) =>
        bookings.Select(b => new BookingDetail(
            b.Id,
            b.SlotId,
            b.Slot!.StartsAt,
            b.Slot.EndsAt,
            b.Status.ToString(),
            b.Topic,
            b.CheckInCode,
            b.Slot.EventSpecialist!.Event!.Slug,
            b.Slot.EventSpecialist.Event.Name,
            b.Slot.EventSpecialist.Specialist!.FullName,
            b.Slot.EventSpecialist.Specialist.Specialty,
            b.Slot.EventSpecialist.Location));

    private static async Task<BookingDetail?> LoadDetailAsync(
        AmicusDbContext db, Guid id, CancellationToken ct) =>
        await db.Bookings.Where(b => b.Id == id).ToDetail().FirstOrDefaultAsync(ct);
}
