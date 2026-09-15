namespace Amicus.Api.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>SMTP host, e.g. smtp.gmail.com. Empty disables sending.</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    /// <summary>SMTP username — for Gmail, the full address.</summary>
    public string User { get; set; } = "";

    /// <summary>SMTP password — for Gmail, an app password (not the account password).</summary>
    public string Password { get; set; } = "";

    /// <summary>The From address. Defaults to <see cref="User"/> when unset.</summary>
    public string From { get; set; } = "";

    public string FromName { get; set; } = "AMiCUS Timișoara";

    /// <summary>
    /// Base URL of the web client's reset page. When set, a password-reset email
    /// links to <c>{WebResetUrl}?email=…&amp;code=…</c>; when empty, the email
    /// carries the bare code and the student pastes it into the app.
    /// </summary>
    public string WebResetUrl { get; set; } = "";

    /// <summary>
    /// Base URL of the web client's confirm page. When set, the confirmation
    /// email links to <c>{WebConfirmUrl}?userId=…&amp;code=…</c> (a nicer page than
    /// the API's raw confirm endpoint); when empty, it links straight to the API.
    /// </summary>
    public string WebConfirmUrl { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(User)
        && !string.IsNullOrWhiteSpace(Password);
}
