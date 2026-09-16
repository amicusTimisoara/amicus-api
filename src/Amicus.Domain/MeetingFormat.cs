namespace Amicus.Domain;

/// <summary>
/// How a „carte” is willing to meet. Straight from the project brief: "fiecare
/// alege formatul (1-la-1 sau grup mic; fizic, online sau ambele)".
///
/// Note what is NOT here: how long a meeting lasts. That belongs to the slot,
/// not to the person — a „carte” picks the start time and duration when they
/// publish each interval, so it can differ from one week to the next. See
/// <see cref="Entities.SlotPattern.SlotDurationMinutes"/>.
/// </summary>
public enum MeetingFormat
{
    Fizic = 1,
    Online = 2,
    Ambele = 3,
}
