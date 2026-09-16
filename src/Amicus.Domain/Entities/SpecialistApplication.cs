namespace Amicus.Domain.Entities;

/// <summary>
/// Someone asking to become a „carte”.
///
/// The route is deliberately two-step: a student registers an ordinary account
/// first, then applies from their settings. That is why <see cref="UserId"/> is
/// required and not nullable — unlike <see cref="Specialist.UserId"/>, which is
/// optional because the committee can put a „carte” on the board who never signs
/// in at all. An application always belongs to someone who did.
///
/// Approving one creates the <see cref="Specialist"/> row and links it to the
/// same user, which is what makes their avatar show the „carte” ring everywhere.
/// The application is kept afterwards rather than deleted: it is the record of
/// what the person actually told us about themselves, and of who accepted it.
/// </summary>
public class SpecialistApplication
{
    public Guid Id { get; set; }

    /// <summary>The applicant's identity account. One open application each.</summary>
    public Guid UserId { get; set; }

    public required string FullName { get; set; }

    /// <summary>
    /// How to reach them while the application is being read. Kept off every
    /// student-facing contract — the board never reveals a „carte”'s phone number.
    /// </summary>
    public required string Phone { get; set; }

    /// <summary>How they describe themselves, e.g. "Medic de familie", "Pastor".</summary>
    public required string Specialty { get; set; }

    /// <summary>The advice domain — the axis that colours them on the board.</summary>
    public SpecialistCategory Category { get; set; } = SpecialistCategory.Social;

    /// <summary>
    /// The life-story axis. Optional, and optional on purpose: an applicant who
    /// recognises none of the profiles should leave it empty rather than be
    /// forced into the nearest wrong one.
    /// </summary>
    public StoryProfile? Profile { get; set; }

    /// <summary>
    /// What they wrote about themselves. This is the substance the committee
    /// reads, so it is long-form and required.
    /// </summary>
    public required string Story { get; set; }

    public MeetingFormat Format { get; set; } = MeetingFormat.Ambele;

    /// <summary>For the international students the brief calls out explicitly.</summary>
    public bool SpeaksEnglish { get; set; }

    /// <summary>"singur sau împreună cu unul sau mai mulți prieteni".</summary>
    public bool AcceptsSmallGroups { get; set; }

    public SpecialistApplicationStatus Status { get; set; } = SpecialistApplicationStatus.Pending;

    /// <summary>
    /// Why it was rejected, in the committee's own words. Shown back to the
    /// applicant — a refusal with no reason invites them to simply reapply.
    /// </summary>
    public string? ReviewNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>Which admin decided, so the decision is attributable.</summary>
    public Guid? ReviewedByUserId { get; set; }

    /// <summary>The „carte” this became, once approved.</summary>
    public Guid? SpecialistId { get; set; }

    public Specialist? Specialist { get; set; }
}
