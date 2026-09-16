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
/// Becoming a „carte”: a student applies from their own settings, and someone on
/// the committee reads it and decides.
///
/// There is no code to redeem and no self-service promotion. That was the
/// alternative design, and it was dropped: a single shared secret cannot be
/// revoked for one person, and one leak would have opened „carte” status to
/// anyone who heard it.
/// </summary>
public static class SpecialistApplicationEndpoints
{
    private static SpecialistApplicationDetail ToDetail(SpecialistApplication a) => new(
        a.Id, a.FullName, a.Phone, a.Specialty, a.Category.ToString(),
        a.Profile?.ToString(), a.Story, a.Format.ToString(), a.SpeaksEnglish,
        a.AcceptsSmallGroups, a.Status.ToString(), a.ReviewNote,
        a.CreatedAt, a.ReviewedAt, a.SpecialistId);

    public static IEndpointRouteBuilder MapSpecialistApplications(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/account").RequireAuthorization();

        mine.MapPost("/specialist-application", async (
            [FromBody] SubmitSpecialistApplicationRequest request,
            UserManager<AppUser> users, AmicusDbContext db, TimeProvider clock,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
            {
                return Results.NotFound();
            }

            var fullName = request.FullName?.Trim() ?? "";
            var phone = request.Phone?.Trim() ?? "";
            var specialty = request.Specialty?.Trim() ?? "";
            var story = request.Story?.Trim() ?? "";

            // The story is the part a human actually reads, so an empty one makes
            // the application undecidable rather than merely incomplete.
            if (fullName.Length == 0 || phone.Length == 0 || specialty.Length == 0 || story.Length == 0)
            {
                return Results.BadRequest(new
                {
                    error = "fullName, phone, specialty and story are all required.",
                });
            }

            if (await db.Specialists.AnyAsync(s => s.UserId == user.Id, ct))
            {
                return Results.Conflict(new { error = "This account is already a carte." });
            }

            if (await db.SpecialistApplications.AnyAsync(
                    a => a.UserId == user.Id && a.Status == SpecialistApplicationStatus.Pending, ct))
            {
                return Results.Conflict(new { error = "You already have an application under review." });
            }

            var application = new SpecialistApplication
            {
                Id = Guid.CreateVersion7(),
                UserId = user.Id,
                FullName = fullName,
                Phone = phone,
                Specialty = specialty,
                Category = request.Category,
                Profile = request.Profile,
                Story = story,
                Format = request.Format,
                SpeaksEnglish = request.SpeaksEnglish,
                AcceptsSmallGroups = request.AcceptsSmallGroups,
                Status = SpecialistApplicationStatus.Pending,
                CreatedAt = clock.GetUtcNow(),
            };

            db.SpecialistApplications.Add(application);
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToDetail(application));
        })
            .WithSummary("Apply to become a „carte”. One open application per account.");

        mine.MapGet("/specialist-application", async (
            UserManager<AppUser> users, AmicusDbContext db,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
            {
                return Results.NotFound();
            }

            // The latest one, so a rejected applicant who reapplies sees the new
            // attempt rather than the old refusal.
            //
            // Id breaks the tie. Two applications can share a CreatedAt — under a
            // fixed clock they always do — and without a tiebreaker "latest" is
            // whatever the database happens to return, which surfaced as a
            // resubmitting applicant still being shown their old rejection.
            // Guid.CreateVersion7 is time-ordered, so the larger id is the newer row.
            var application = await db.SpecialistApplications
                .Where(a => a.UserId == user.Id)
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .FirstOrDefaultAsync(ct);

            return application is null
                ? Results.NoContent()
                : Results.Ok(ToDetail(application));
        })
            .WithSummary("Your own application and where it has got to. 204 if you never applied.");

        var admin = app.MapGroup("/admin")
            .RequireAuthorization(policy => policy.RequireRole(AppRoles.Admin));

        admin.MapGet("/specialist-applications", async (
            [FromQuery] string? status, AmicusDbContext db, CancellationToken ct) =>
        {
            var query = db.SpecialistApplications.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<SpecialistApplicationStatus>(status, true, out var parsed))
                {
                    return Results.BadRequest(new { error = $"Unknown status '{status}'." });
                }
                query = query.Where(a => a.Status == parsed);
            }

            // Oldest first: the queue is a queue, and the person who has been
            // waiting longest should be read first.
            //
            // Materialised BEFORE mapping — ToDetail is a plain C# method and EF
            // cannot translate it into SQL, so projecting with it inside the
            // query throws at runtime rather than at build time.
            var rows = await query.OrderBy(a => a.CreatedAt).ToListAsync(ct);

            return Results.Ok(rows.Select(ToDetail).ToList());
        })
            .WithSummary("The application queue, optionally filtered by status.");

        admin.MapPost("/specialist-applications/{id:guid}/approve", async (
            Guid id, UserManager<AppUser> users, AmicusDbContext db, TimeProvider clock,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var application = await db.SpecialistApplications
                .FirstOrDefaultAsync(a => a.Id == id, ct);

            if (application is null)
            {
                return Results.NotFound();
            }

            if (application.Status != SpecialistApplicationStatus.Pending)
            {
                return Results.Conflict(new
                {
                    error = $"Already {application.Status.ToString().ToLowerInvariant()}.",
                });
            }

            // If this person somehow already has a Specialist row, link to it
            // rather than inserting a second — Specialist.UserId is uniquely
            // indexed, so a duplicate would fail at the database anyway.
            var specialist = await db.Specialists
                .FirstOrDefaultAsync(s => s.UserId == application.UserId, ct);

            if (specialist is null)
            {
                specialist = new Specialist
                {
                    Id = Guid.CreateVersion7(),
                    UserId = application.UserId,
                    FullName = application.FullName,
                    Specialty = application.Specialty,
                    Category = application.Category,
                    Profile = application.Profile,
                    // What they wrote about themselves becomes their public bio.
                    Bio = application.Story,
                    IsActive = true,
                    CreatedAt = clock.GetUtcNow(),
                };
                db.Specialists.Add(specialist);
            }

            var reviewer = await users.GetUserAsync(principal);

            application.Status = SpecialistApplicationStatus.Approved;
            application.ReviewedAt = clock.GetUtcNow();
            application.ReviewedByUserId = reviewer?.Id;
            application.SpecialistId = specialist.Id;

            await db.SaveChangesAsync(ct);

            return Results.Ok(ToDetail(application));
        })
            .WithSummary("Approve an application, creating the carte and linking it to the account.");

        admin.MapPost("/specialist-applications/{id:guid}/reject", async (
            Guid id, [FromBody] RejectSpecialistApplicationRequest request,
            UserManager<AppUser> users, AmicusDbContext db, TimeProvider clock,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var application = await db.SpecialistApplications
                .FirstOrDefaultAsync(a => a.Id == id, ct);

            if (application is null)
            {
                return Results.NotFound();
            }

            if (application.Status != SpecialistApplicationStatus.Pending)
            {
                return Results.Conflict(new
                {
                    error = $"Already {application.Status.ToString().ToLowerInvariant()}.",
                });
            }

            var reviewer = await users.GetUserAsync(principal);

            application.Status = SpecialistApplicationStatus.Rejected;
            // Shown back to the applicant: a refusal with no reason just invites
            // them to send the same thing again.
            application.ReviewNote = string.IsNullOrWhiteSpace(request?.Note)
                ? null
                : request.Note.Trim();
            application.ReviewedAt = clock.GetUtcNow();
            application.ReviewedByUserId = reviewer?.Id;

            await db.SaveChangesAsync(ct);

            return Results.Ok(ToDetail(application));
        })
            .WithSummary("Reject an application, with a reason the applicant can read.");

        return app;
    }
}
