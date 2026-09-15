namespace Amicus.Domain;

/// <summary>
/// What kind of advice a specialist gives. First-class so the clients don't have
/// to guess it from the free-text <see cref="Entities.Specialist.Specialty"/>.
///
/// The names match the web client's category set exactly (spiritual, mentorat,
/// medical, juridic, cariera, social); <see cref="Social"/> is the default and the
/// catch-all, the same fallback the client used before this field existed.
/// </summary>
public enum SpecialistCategory
{
    Social = 0,
    Spiritual = 1,
    Mentorat = 2,
    Medical = 3,
    Juridic = 4,
    Cariera = 5,
}
