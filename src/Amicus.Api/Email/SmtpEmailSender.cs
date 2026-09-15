using Amicus.Infrastructure.Identity;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Amicus.Api.Email;

/// <summary>
/// Sends Identity's account emails over SMTP (Gmail or any provider). Registering
/// this is what makes the <c>/auth/forgotPassword</c> and <c>/auth/resetPassword</c>
/// endpoints actually deliver — without an <see cref="IEmailSender{TUser}"/> they
/// succeed silently and nothing is sent.
///
/// When SMTP is not configured the sender logs the fact and returns without
/// throwing, so the app runs in dev and tests without credentials. Identity always
/// answers <c>/forgotPassword</c> with 200 regardless (so it can't be used to probe
/// which emails exist), which means an unconfigured sender would otherwise fail
/// completely silently — hence the warning.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SmtpEmailSender> logger) : IEmailSender<AppUser>
{
    private readonly EmailOptions _options = options.Value;

    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink) =>
        SendAsync(email, "Confirmă-ți adresa de email",
            $"Salut,<br><br>Confirmă-ți adresa apăsând " +
            $"<a href=\"{confirmationLink}\">aici</a>.<br><br>AMiCUS Timișoara");

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink) =>
        SendAsync(email, "Resetare parolă",
            $"Salut,<br><br>Resetează-ți parola apăsând " +
            $"<a href=\"{resetLink}\">aici</a>.<br><br>Dacă nu ai cerut tu asta, " +
            $"ignoră acest email.<br><br>AMiCUS Timișoara");

    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode)
    {
        // MapIdentityApi's /forgotPassword uses the CODE flow, not the link flow.
        // If the web client's reset page is configured, wrap the code in a link to
        // it so the student clicks rather than copies; otherwise send the code.
        var body = string.IsNullOrWhiteSpace(_options.WebResetUrl)
            ? $"Salut,<br><br>Codul tău de resetare a parolei este:<br><br>" +
              $"<b style=\"font-size:18px\">{resetCode}</b><br><br>" +
              $"Introdu-l în aplicație împreună cu noua parolă.<br><br>AMiCUS Timișoara"
            : $"Salut,<br><br>Resetează-ți parola apăsând " +
              $"<a href=\"{BuildResetLink(email, resetCode)}\">aici</a>.<br><br>" +
              $"Dacă nu ai cerut tu asta, ignoră acest email.<br><br>AMiCUS Timișoara";

        return SendAsync(email, "Resetare parolă", body);
    }

    private string BuildResetLink(string email, string code) =>
        $"{_options.WebResetUrl}?email={Uri.EscapeDataString(email)}&code={Uri.EscapeDataString(code)}";

    private async Task SendAsync(string to, string subject, string htmlBody)
    {
        if (!_options.IsConfigured)
        {
            logger.LogWarning(
                "Email not sent to {To} ('{Subject}') — SMTP is not configured. " +
                "Set Email:Host / Email:User / Email:Password to enable delivery.",
                to, subject);
            return;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            _options.FromName,
            string.IsNullOrWhiteSpace(_options.From) ? _options.User : _options.From));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody }.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            // STARTTLS on 587 (Gmail's submission port); the alternative is 465 with
            // implicit TLS. MailKit picks the handshake from the option.
            var socketOption = _options.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_options.Host, _options.Port, socketOption);
            await client.AuthenticateAsync(_options.User, _options.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(quit: true);

            logger.LogInformation("Sent '{Subject}' to {To}.", subject, to);
        }
        catch (Exception ex)
        {
            // Logged, not rethrown: a failed reset email must not turn /forgotPassword
            // into a 500 that tells a caller the address exists. The admin-reset path
            // is the fallback when delivery is broken.
            logger.LogError(ex, "Failed to send '{Subject}' to {To}.", subject, to);
        }
    }
}
