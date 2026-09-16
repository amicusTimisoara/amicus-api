namespace Amicus.Domain;

/// <summary>
/// The life story a "book" brings — the second axis of the catalogue, alongside
/// <see cref="SpecialistCategory"/>.
///
/// The two are deliberately different questions. <see cref="SpecialistCategory"/>
/// asks what KIND OF ADVICE someone gives (medical, juridic, carieră) and drives
/// the colour on the board. This asks WHOSE STORY it is, and is what the project
/// brief actually recruits against: "«Cărțile» reprezintă 10 oameni reali, cu
/// experiențe de viață puternice". A physician who is in the catalogue because
/// they survived a tragedy is <c>Medical</c> on one axis and <c>Tragedie</c> on
/// the other, and a student searching for either should find them.
///
/// Nullable on <see cref="Entities.Specialist"/>: a book can be listed before
/// anyone has agreed how to label its story, and an unlabelled one is better
/// than a wrong one.
///
/// The brief's fourth profile is "un refugiat sau un fost deținut" — one
/// recruiting slot covering two very different lives. It is split here because
/// this tag is shown against a NAMED PERSON, and a badge reading "refugee or
/// ex-convict" tells a student nothing while implying both.
/// </summary>
public enum StoryProfile
{
    /// <summary>„un fost dependent”</summary>
    FostDependent = 1,

    /// <summary>„un antreprenor”</summary>
    Antreprenor = 2,

    /// <summary>„un medic”</summary>
    Medic = 3,

    /// <summary>„un refugiat” — first half of the brief's fourth profile.</summary>
    Refugiat = 4,

    /// <summary>„un fost deținut” — second half of the brief's fourth profile.</summary>
    FostDetinut = 5,

    /// <summary>„un fost ateu”</summary>
    FostAteu = 6,

    /// <summary>„un om care a trecut printr-o mare tragedie”</summary>
    Tragedie = 7,

    /// <summary>„un artist”</summary>
    Artist = 8,

    /// <summary>„un psiholog”</summary>
    Psiholog = 9,

    /// <summary>„un pastor”</summary>
    Pastor = 10,

    /// <summary>„un fost student olimpic”</summary>
    FostOlimpic = 11,
}
