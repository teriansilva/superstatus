using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Data.Repositories;

namespace SuperStatus.Services.Alerts;

/// <summary>What happened to an email send.</summary>
public enum EmailSendStatus
{
    /// <summary>Delivered to the relay.</summary>
    Sent,
    /// <summary>Intentionally not attempted (not configured / no recipients) — NOT a failure.</summary>
    Skipped,
    /// <summary>Attempted but the relay rejected/errored.</summary>
    Failed,
}

/// <summary>Outcome of an email send. <see cref="Skipped"/> (a guard, no attempt) is
/// distinct from <see cref="Failed"/> (an attempted delivery that errored) so the
/// audit log doesn't show an unconfigured channel as a delivery outage.</summary>
public sealed record EmailSendResult(EmailSendStatus Status, string Target, string? Detail)
{
    public bool Ok => Status == EmailSendStatus.Sent;

    public static EmailSendResult Sent(string target) => new(EmailSendStatus.Sent, target, null);
    public static EmailSendResult Skipped(string reason) => new(EmailSendStatus.Skipped, string.Empty, reason);
    public static EmailSendResult Failed(string target, string? error) => new(EmailSendStatus.Failed, target, error);
}

/// <summary>
/// Issue #241 Phase B: sends alert + test emails over the operator-configured SMTP
/// relay (MailKit). Reads the RAW <see cref="SiteSettings"/> row so it can use the
/// stored password (never the masked view-model). Error-tolerant — returns a result
/// rather than throwing, so a bad relay produces a Failed audit row, not a crashed tick.
/// </summary>
public interface IEmailNotifier
{
    /// <summary>True when the relay is configured enough to attempt a send (host + from).</summary>
    bool IsConfigured(SiteSettings settings);

    /// <summary>Send an alert email for a check + trigger. Returns Skipped (no Target)
    /// when SMTP isn't configured or there are no recipients.
    /// #291: <paramref name="recipientsOverride"/> carries the linked profile's
    /// recipients — null falls back to the legacy per-check field, empty means
    /// "use the site default recipients" (the UsesSiteDefaultRecipients profile).</summary>
    Task<EmailSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, string? recipientsOverride = null, CancellationToken cancellationToken = default);

    /// <summary>Send a test email (to <paramref name="toOverride"/> or the default
    /// recipients). On success, stamps SmtpVerifiedUtc.</summary>
    Task<EmailSendResult> SendTestAsync(string? toOverride, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class MailKitEmailNotifier(
    ISiteSettingsRepository settingsRepository,
    ILogger<MailKitEmailNotifier> logger) : IEmailNotifier
{
    /// <summary>Connect+send budget so a black-holed relay can't hang a check tick.</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    public bool IsConfigured(SiteSettings settings)
        => !string.IsNullOrWhiteSpace(settings.SmtpHost) && !string.IsNullOrWhiteSpace(settings.SmtpFromAddress);

    public async Task<EmailSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, string? recipientsOverride = null, CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.GetSingletonAsync(cancellationToken);
        if (settings is null || !IsConfigured(settings))
            return EmailSendResult.Skipped("SMTP not configured");

        // #291: profile recipients when provided; empty → site default. The
        // legacy per-check recipients column is gone (Phase D) — dispatch is
        // links-only, so the evaluator always passes the profile's recipients.
        string source = recipientsOverride ?? string.Empty;
        var recipients = ParseRecipients(
            string.IsNullOrWhiteSpace(source) ? settings.AlertDefaultRecipients : source);
        if (recipients.Count == 0)
            return EmailSendResult.Skipped("no recipients");

        var subject = $"[{Verb(trigger)}] {check.Title}";
        var body = BuildAlertBody(check, trigger);
        return await SendAsync(settings, recipients, subject, body, stampVerified: false, cancellationToken);
    }

    public async Task<EmailSendResult> SendTestAsync(string? toOverride, CancellationToken cancellationToken = default)
    {
        var settings = await settingsRepository.GetSingletonAsync(cancellationToken);
        if (settings is null || !IsConfigured(settings))
            return EmailSendResult.Skipped("SMTP not configured");

        var recipients = ParseRecipients(string.IsNullOrWhiteSpace(toOverride) ? settings.AlertDefaultRecipients : toOverride);
        if (recipients.Count == 0)
            return EmailSendResult.Skipped("no test recipient — set a default or pass an address");

        const string subject = "SuperStatus — test alert email";
        const string body = "This is a test from SuperStatus. If you received it, your SMTP alert settings work.";
        return await SendAsync(settings, recipients, subject, body, stampVerified: true, cancellationToken);
    }

    private async Task<EmailSendResult> SendAsync(
        SiteSettings settings, List<string> recipients, string subject, string body, bool stampVerified, CancellationToken cancellationToken)
    {
        var target = string.Join(", ", recipients);
        // Snapshot the transport we're about to test, as plain values — so a config
        // edit racing this send can't make us stamp the NEW (untested) relay verified.
        var (host, port, tls, user, pwd, from) =
            (settings.SmtpHost, settings.SmtpPort, settings.SmtpUseStartTls, settings.SmtpUsername, settings.SmtpPassword, settings.SmtpFromAddress);
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(
                string.IsNullOrWhiteSpace(settings.SmtpFromName) ? settings.SmtpFromAddress : settings.SmtpFromName,
                settings.SmtpFromAddress));
            foreach (var r in recipients)
                message.To.Add(MailboxAddress.Parse(r));
            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(SendTimeout);

            using var client = new SmtpClient();
            // 465 = implicit TLS; otherwise STARTTLS when requested, else plain (LAN relay).
            var socketOptions = settings.SmtpPort == 465
                ? SecureSocketOptions.SslOnConnect
                : settings.SmtpUseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

            await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, socketOptions, cts.Token);
            if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
                await client.AuthenticateAsync(settings.SmtpUsername, settings.SmtpPassword, cts.Token);
            await client.SendAsync(message, cts.Token);
            await client.DisconnectAsync(true, cts.Token);

            if (stampVerified)
            {
                // Atomic, conditional: stamps only if the row still matches the relay
                // we just tested (an edit in-flight leaves the new config unverified).
                await settingsRepository.StampSmtpVerifiedIfTransportMatchesAsync(
                    host, port, tls, user, pwd, from, DateTime.UtcNow, cancellationToken);
            }
            return EmailSendResult.Sent(target);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Log the type only (avoid leaking creds/host in logs); return a sanitized
            // message for the audit row.
            logger.LogWarning("Email send failed ({ExceptionType}).", ex.GetType().Name);
            return EmailSendResult.Failed(target, Sanitize(ex.Message));
        }
    }

    private static string Verb(AlertTrigger trigger) => trigger switch
    {
        AlertTrigger.Recovery => "RECOVERED",
        AlertTrigger.Outage => "OUTAGE",
        _ => "DOWN",
    };

    private static string BuildAlertBody(StatusCheck check, AlertTrigger trigger)
    {
        var what = trigger == AlertTrigger.Recovery ? "has recovered" : "is failing";
        var since = check.DownSinceUtc is { } d ? $"\nDown since: {d:u}" : string.Empty;
        return $"{check.Title} {what}.\nURL: {check.StatusCheckUrl}{since}\nConsecutive failures: {check.ConsecutiveFailures}\n\n— SuperStatus";
    }

    public static List<string> ParseRecipients(string? raw)
        => (raw ?? string.Empty)
            .Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Sanitize(string? message)
    {
        var m = (message ?? "send failed").Trim();
        return m.Length > AlertDeliveryLog.MaxErrorMessageLength ? m[..AlertDeliveryLog.MaxErrorMessageLength] : m;
    }
}
