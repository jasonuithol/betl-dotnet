using Betl.Core;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Betl.Components.Tasks;

/// <summary>
/// Plain-text SMTP send via MailKit. Supports <c>smtp://</c> (cleartext +
/// opportunistic STARTTLS) and <c>smtps://</c> (implicit TLS), with optional
/// AUTH. Attachments / multipart / HTML are out of scope for Phase 3, matching
/// the upstream v0.x limitation.
/// </summary>
public sealed class SmtpSendTask(
    string id, string url, string? username, string? password,
    string from, IReadOnlyList<string> to, IReadOnlyList<string>? cc,
    string subject, string body) : IControlTask
{
    public string Id { get; } = id;

    public void Execute(Action<string>? log)
    {
        var uri = new Uri(url);
        var secureSocketOptions = uri.Scheme switch
        {
            "smtps" => SecureSocketOptions.SslOnConnect,
            "smtp" => SecureSocketOptions.StartTlsWhenAvailable,
            _ => throw new BetlException($"smtp.send '{Id}': unsupported URL scheme '{uri.Scheme}'. Use smtp:// or smtps://."),
        };
        var port = uri.Port == -1
            ? (secureSocketOptions == SecureSocketOptions.SslOnConnect ? 465 : 587)
            : uri.Port;

        var msg = new MimeMessage
        {
            Subject = subject,
            Body = new TextPart("plain") { Text = body },
        };
        msg.From.Add(MailboxAddress.Parse(from));
        foreach (var t in to) msg.To.Add(MailboxAddress.Parse(t));
        if (cc is not null) foreach (var c in cc) msg.Cc.Add(MailboxAddress.Parse(c));

        using var client = new SmtpClient();
        client.Connect(uri.Host, port, secureSocketOptions);
        if (!string.IsNullOrEmpty(username))
            client.Authenticate(username, password ?? "");
        client.Send(msg);
        client.Disconnect(quit: true);

        log?.Invoke($"   sent message to {string.Join(", ", to)} via {uri.Host}:{port}");
    }
}
