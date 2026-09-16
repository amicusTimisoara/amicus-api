using Amicus.Domain;

namespace Amicus.Api.Contracts;

public sealed record EventSummary(
    Guid Id, string Slug, string Name, DateOnly StartsOn, DateOnly EndsOn, string TimeZoneId);

public sealed record SpecialistSummary(
    Guid EventSpecialistId, Guid SpecialistId, string FullName, string Specialty,
    string Category, string? Profile, string? Bio, string? Location);

public sealed record EventDetail(
    EventSummary Event, IReadOnlyList<SpecialistSummary> Specialists);

/// <summary>
/// One cell of the shared board.
///
/// Carries availability and nothing else. <c>IsMine</c> is the caller's own
/// booking, which they are entitled to know; there is deliberately no field for
/// who holds a slot, because the board is visible to every student and some of
/// these specialists are physicians and lawyers.
/// </summary>
public sealed record BoardSlot(
    Guid Id, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsAvailable, bool IsMine);

public sealed record SpecialistBoard(
    SpecialistSummary Specialist, IReadOnlyList<BoardSlot> Slots);

public sealed record CreateBookingRequest(Guid SlotId, string? Topic);

public sealed record BookingDetail(
    Guid Id, Guid SlotId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status,
    string? Topic, string CheckInCode, string EventSlug, string EventName,
    string SpecialistName, string Specialty, string? Location);

public sealed record CreateEventRequest(
    string Name, string Slug, DateOnly StartsOn, DateOnly EndsOn, string? TimeZoneId);

public sealed record CreateSpecialistRequest(
    string FullName, string Specialty, string? Bio, SpecialistCategory? Category,
    StoryProfile? Profile);

/// <summary>
/// Every field is optional — only the ones present are changed. Lets an admin
/// set a category on a specialist created before the field existed, or fix any
/// other detail, without a full replace.
/// </summary>
public sealed record UpdateSpecialistRequest(
    string? FullName, string? Specialty, string? Bio,
    SpecialistCategory? Category, StoryProfile? Profile, bool? IsActive);

public sealed record AssignSpecialistRequest(Guid SpecialistId, string? Location);

public sealed record CreateSlotPatternRequest(
    DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime,
    int SlotDurationMinutes, int BreakMinutes);

public sealed record GenerateSlotsResult(int Created, int AlreadyPresent, int RemovedStale);

/// <summary>The signed-in user's own profile. `photoUrl` is null for a password
/// account; `displayName` is null until set (or filled by Google).</summary>
public sealed record AccountInfo(
    string Email, string? DisplayName, string? PhotoUrl, bool IsEmailConfirmed);

/// <summary>Only fields present are changed. `displayName` empty/whitespace clears it.</summary>
public sealed record UpdateAccountRequest(string? DisplayName);

public sealed record AdminResetPasswordRequest(string Email, string NewPassword);

public sealed record CheckInRequest(string Code);

public sealed record CheckInResult(
    Guid BookingId, DateTimeOffset StartsAt, string SpecialistName, string Status);

/// <summary>
/// An event as an admin sees it — unpublished ones included, with the counts a
/// console needs to show a roster at a glance without a request per row.
/// </summary>
public sealed record AdminEventSummary(
    Guid Id, string Slug, string Name, DateOnly StartsOn, DateOnly EndsOn,
    string TimeZoneId, bool IsPublished, int SpecialistCount, int SlotCount);

public sealed record AdminSpecialistSummary(
    Guid Id, string FullName, string Specialty, string Category, string? Profile,
    string? Bio, bool IsActive);

public sealed record AdminRosterEntry(
    Guid EventSpecialistId, Guid SpecialistId, string FullName, string Specialty,
    string? Location, int PatternCount, int SlotCount, int BookedCount);

/// <summary>
/// A slot on the admin's view of the board.
///
/// Carries whether someone holds it, never who: an admin is entitled to know that
/// — see <see cref="Amicus.Domain.Entities.Booking"/> — but a list endpoint is the
/// wrong place to hand it out wholesale, and nothing an admin console does with
/// this list needs a name.
/// </summary>
public sealed record AdminSlot(
    Guid Id, Guid EventSpecialistId, string SpecialistName,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool IsBlocked, bool HasLiveBooking);
