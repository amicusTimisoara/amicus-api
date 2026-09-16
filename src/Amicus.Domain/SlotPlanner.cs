using Amicus.Domain.Entities;

namespace Amicus.Domain;

/// <summary>One expanded slot, as UTC instants ready to persist.</summary>
public readonly record struct PlannedSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// Expands admin-authored <see cref="SlotPattern"/>s into concrete
/// <see cref="PlannedSlot"/>s.
///
/// Deliberately pure — no database, no clock. All the fiddly correctness lives
/// here (wall-clock to UTC across a DST boundary, trailing partial slots) where
/// it can be tested without a Postgres.
/// </summary>
public static class SlotPlanner
{
    /// <summary>
    /// Expands one pattern across every matching day inside the event's range.
    /// </summary>
    /// <remarks>
    /// Pattern times are wall-clock in the event's zone, so they are converted per
    /// day rather than by adding a fixed offset: on a DST changeover day the same
    /// "14:00" is a different instant than the day before.
    ///
    /// A start time that does not exist (the spring-forward gap) is SKIPPED rather
    /// than shifted. Silently moving an appointment to a different hour than the
    /// admin wrote is worse than not offering it.
    ///
    /// The END is derived by adding the duration to the UTC start, not by
    /// converting the local end time. A 30-minute appointment is 30 minutes of
    /// real time. Converting the local end would reject a perfectly valid
    /// 02:30–03:00 slot on a spring-forward day, because local 03:00 is inside the
    /// gap — even though that instant exists and is simply relabelled 04:00.
    /// </remarks>
    public static IReadOnlyList<PlannedSlot> Expand(Event @event, SlotPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.SlotDurationMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pattern),
                pattern.SlotDurationMinutes,
                "Slot duration must be positive.");
        }

        if (pattern.BreakMinutes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pattern), pattern.BreakMinutes, "Break cannot be negative.");
        }

        if (pattern.EndTime <= pattern.StartTime)
        {
            throw new ArgumentException(
                "Pattern end time must be after its start time.", nameof(pattern));
        }

        if (@event.EndsOn < @event.StartsOn)
        {
            throw new ArgumentException(
                "Event end date must not precede its start date.", nameof(@event));
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(@event.TimeZoneId);
        var duration = TimeSpan.FromMinutes(pattern.SlotDurationMinutes);
        var step = TimeSpan.FromMinutes(pattern.SlotDurationMinutes + pattern.BreakMinutes);

        var planned = new List<PlannedSlot>();

        for (var day = @event.StartsOn; day <= @event.EndsOn; day = day.AddDays(1))
        {
            if (day.DayOfWeek != pattern.DayOfWeek)
            {
                continue;
            }

            for (var localStart = day.ToDateTime(pattern.StartTime);
                 localStart.TimeOfDay + duration <= pattern.EndTime.ToTimeSpan();
                 localStart = localStart.Add(step))
            {
                // A start on the wall clock that never happens is a fiction.
                if (zone.IsInvalidTime(localStart))
                {
                    continue;
                }

                var startsAt = ToUtc(localStart, zone);
                planned.Add(new PlannedSlot(startsAt, startsAt.Add(duration)));
            }
        }

        return planned;
    }

    /// <summary>
    /// Expands every pattern for a specialist and drops slots that would overlap
    /// one another, keeping the earlier slot. Two patterns on the same day are an
    /// admin mistake, but a double-booked specialist is a student's problem, so it
    /// is resolved here rather than trusted not to happen.
    /// </summary>
    public static IReadOnlyList<PlannedSlot> ExpandAll(
        Event @event, IEnumerable<SlotPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var ordered = patterns
            .SelectMany(p => Expand(@event, p))
            .OrderBy(s => s.StartsAt)
            .ThenBy(s => s.EndsAt)
            .ToList();

        var kept = new List<PlannedSlot>(ordered.Count);

        foreach (var slot in ordered)
        {
            if (kept.Count > 0 && slot.StartsAt < kept[^1].EndsAt)
            {
                continue;
            }

            kept.Add(slot);
        }

        return kept;
    }

    /// <remarks>
    /// An ambiguous local time (the autumn fall-back hour happens twice) resolves
    /// to the FIRST occurrence — the daylight offset — which is what someone
    /// reading a printed timetable would turn up for.
    /// </remarks>
    /// <summary>
    /// Public because a „carte” publishing a single interval needs exactly the
    /// same wall-clock-to-instant rule as pattern expansion. Two implementations
    /// of this would drift, and the one that drifted would put a student and a
    /// „carte” in a room an hour apart.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The local time does not exist — the spring-forward hour is skipped. The
    /// caller decides what to tell the person; there is no sensible instant.
    /// </exception>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsAmbiguousTime(unspecified))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            var daylight = offsets.Max();
            return new DateTimeOffset(unspecified, daylight).ToUniversalTime();
        }

        return new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }
}
