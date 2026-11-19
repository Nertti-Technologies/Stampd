using Stampd.Email.Smtp;
using Stampd.Identity.EmailOtp;
using Stampd.Infrastructure.Identity;

namespace Stampd.WebApi.Hosting;

internal static class EmailRegistration
{
    /// <summary>
    /// Registers the SMTP email transport and the Email OTP identity verification.
    /// </summary>
    public static void AddStampdEmail(this WebApplicationBuilder builder)
    {
        // ---- Email transport ----
        // SMTP sender. Defaults to localhost:2525 (Hermex default) with no auth so the
        // in-process dev SMTP server hosted by Stampd.UI captures everything for inspection
        // at http://localhost:5170/hermex. Override Stampd:Email:Smtp:* for production.
        builder.Services.AddSmtpEmailSender(opts =>
        {
            opts.Host = builder.Configuration["Stampd:Email:Smtp:Host"] ?? "localhost";
            opts.Port = builder.Configuration.GetValue("Stampd:Email:Smtp:Port", 2525);
            opts.Username = builder.Configuration["Stampd:Email:Smtp:Username"];
            opts.Password = builder.Configuration["Stampd:Email:Smtp:Password"];
            var sec = builder.Configuration["Stampd:Email:Smtp:Security"];
            if (!string.IsNullOrWhiteSpace(sec)
                && Enum.TryParse<MailKit.Security.SecureSocketOptions>(sec, ignoreCase: true, out var parsedSec))
            {
                opts.Security = parsedSec;
            }
            else
            {
                // Dev default: no TLS so Hermex on :2525 just works.
                opts.Security = MailKit.Security.SecureSocketOptions.None;
            }
        });

        // ---- Identity verification (Email OTP) ----
        // Persistent OTP store FIRST so the in-memory default doesn't win the TryAddSingleton race.
        builder.Services.AddDbOtpChallengeStore();
        builder.Services.AddEmailOtpIdentityVerification(opts =>
        {
            opts.FromAddress = builder.Configuration["Stampd:Identity:EmailOtp:FromAddress"]
                ?? builder.Configuration["Stampd:Workflow:Email:FromAddress"]
                ?? "noreply@stampd.local";
            opts.FromDisplayName = builder.Configuration["Stampd:Identity:EmailOtp:FromDisplayName"]
                ?? builder.Configuration["Stampd:Workflow:Email:FromDisplayName"]
                ?? "Stampd";
            opts.ProductName = builder.Configuration["Stampd:Identity:EmailOtp:ProductName"]
                ?? builder.Configuration["Stampd:Workflow:Email:ProductName"]
                ?? "Stampd";
            var lifetimeMinutes = builder.Configuration.GetValue("Stampd:Identity:EmailOtp:ChallengeLifetimeMinutes", 10);
            opts.ChallengeLifetime = TimeSpan.FromMinutes(lifetimeMinutes);
        });
    }
}
