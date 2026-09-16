namespace Amicus.Domain;

/// <summary>
/// Where an application to become a „carte” has got to.
///
/// There is deliberately no self-service route to <see cref="Approved"/>: a
/// person from the committee reads every application and decides. An earlier
/// design had applicants type a shared code instead, which was dropped because
/// one leaked code would have opened „carte” status to anyone, with nothing to
/// revoke per-person.
/// </summary>
public enum SpecialistApplicationStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}
