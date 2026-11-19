using Stampd.Core.Entities;

namespace Stampd.WebApi.Services;

/// <summary>
/// Builds the plain-text and HTML bodies of the recipient invitation email sent by
/// <see cref="SigningWorkflowService"/>.
/// </summary>
internal static class InvitationEmailComposer
{
    internal static string BuildInvitationPlainTextBody(
        Recipient recipient,
        SigningRequest request,
        string url,
        string productName)
    {
        var lines = new List<string>
        {
            $"Hi {recipient.Name},",
            string.Empty,
            $"You've been asked to sign \"{request.Subject}\" via {productName}.",
        };

        if (!string.IsNullOrWhiteSpace(request.Message))
        {
            lines.Add(string.Empty);
            lines.Add(request.Message);
        }

        lines.Add(string.Empty);
        lines.Add($"Review and sign your document: {url}");
        lines.Add(string.Empty);
        lines.Add($"— {productName}");
        lines.Add("This is an automated message. Do not reply.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Executive-grade HTML body for recipient invitations. Matches the OTP and sender-
    /// completion templates (deep-navy → blue gradient header, white card body, dark
    /// footer, tables-only layout) so signers see consistent brand styling end-to-end.
    /// </summary>
    internal static string BuildInvitationHtmlBody(
        Recipient recipient,
        SigningRequest request,
        string url,
        string productName)
    {
        var safeName = System.Net.WebUtility.HtmlEncode(recipient.Name);
        var safeProduct = System.Net.WebUtility.HtmlEncode(productName);
        var safeSubject = System.Net.WebUtility.HtmlEncode(request.Subject);
        var safeUrl = System.Net.WebUtility.HtmlEncode(url);
        var safeRoleName = System.Net.WebUtility.HtmlEncode(recipient.Role?.Name ?? "Signer");

        // Optional message block — only render the panel when the sender included a note,
        // otherwise the card looks padded with empty whitespace.
        var messageBlock = string.IsNullOrWhiteSpace(request.Message)
            ? string.Empty
            : $@"<table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:0 0 24px 0;"">
                <tr>
                  <td style=""background:#f8fafc;border:1px solid #e2e8f0;border-left:3px solid #2b7fce;border-radius:8px;padding:16px 20px;font-size:14px;line-height:1.55;color:#334155;"">
                    {System.Net.WebUtility.HtmlEncode(request.Message)}
                  </td>
                </tr>
              </table>";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width,initial-scale=1""/>
<title>{safeProduct}: action required</title>
</head>
<body style=""margin:0;padding:0;background:#f1f5f9;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;color:#0f172a;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""background:#f1f5f9;padding:32px 16px;"">
    <tr>
      <td align=""center"">
        <table role=""presentation"" width=""560"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""max-width:560px;width:100%;background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 4px 14px rgba(11,60,110,0.08);"">
          <!-- Header -->
          <tr>
            <td style=""background:linear-gradient(135deg,#0b3c6e 0%,#1a5698 50%,#2b7fce 100%);padding:28px 36px;color:#ffffff;"">
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"">
                <tr>
                  <td style=""font-size:14px;font-weight:600;letter-spacing:0.12em;text-transform:uppercase;opacity:0.85;"">{safeProduct}</td>
                  <td align=""right"" style=""font-size:12px;letter-spacing:0.08em;text-transform:uppercase;opacity:0.7;"">Action required</td>
                </tr>
              </table>
              <div style=""font-size:24px;font-weight:700;letter-spacing:-0.01em;margin-top:14px;"">Your signature is requested</div>
            </td>
          </tr>

          <!-- Body -->
          <tr>
            <td style=""padding:36px;"">
              <p style=""margin:0 0 18px 0;font-size:15px;line-height:1.55;color:#334155;"">
                Hi {safeName},
              </p>
              <p style=""margin:0 0 24px 0;font-size:15px;line-height:1.55;color:#334155;"">
                You've been asked to sign a document as <strong style=""color:#0f172a;"">{safeRoleName}</strong> via {safeProduct}.
              </p>

              <!-- Document summary card -->
              <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:0 0 24px 0;"">
                <tr>
                  <td style=""background:#f8fafc;border:1px solid #e2e8f0;border-radius:10px;padding:20px 22px;"">
                    <div style=""font-size:11px;letter-spacing:0.12em;text-transform:uppercase;color:#64748b;margin-bottom:6px;"">Document</div>
                    <div style=""font-size:16px;font-weight:600;color:#0f172a;"">{safeSubject}</div>
                  </td>
                </tr>
              </table>

              {messageBlock}

              <!-- CTA -->
              <table role=""presentation"" cellpadding=""0"" cellspacing=""0"" border=""0"" style=""margin:8px 0 8px 0;"">
                <tr>
                  <td align=""center"" style=""background:#0b3c6e;border-radius:8px;"">
                    <a href=""{safeUrl}"" style=""display:inline-block;padding:13px 28px;color:#ffffff;text-decoration:none;font-weight:600;font-size:14px;letter-spacing:0.02em;"">Review and sign your document</a>
                  </td>
                </tr>
              </table>

              <p style=""margin:18px 0 0 0;font-size:13px;line-height:1.55;color:#64748b;"">
                The link above is unique to you. Don't share it — it's how {safeProduct} keeps your signing session secure and audit-ready.
              </p>
            </td>
          </tr>

          <!-- Footer -->
          <tr>
            <td style=""background:#0f172a;padding:18px 36px;color:#94a3b8;font-size:11px;line-height:1.6;text-align:center;"">
              Sent automatically by {safeProduct}. Please do not reply to this message.
            </td>
          </tr>
        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }
}
