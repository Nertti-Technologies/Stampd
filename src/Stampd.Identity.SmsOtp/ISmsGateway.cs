namespace Stampd.Identity.SmsOtp;

/// <summary>
/// Sends SMS messages on behalf of <see cref="SmsOtpProvider"/>. Implementations wrap
/// Twilio, AWS SNS, MessageBird, Sinch, etc. The interface stays narrow so adopters can
/// ship their own gateway against any provider with five minutes of glue code.
/// </summary>
public interface ISmsGateway
{
    /// <summary>Short identifying name recorded into the audit trail.</summary>
    string Name { get; }

    /// <summary>
    /// Sends <paramref name="message"/> to <paramref name="phoneNumber"/>. Implementations
    /// should throw on transport failure; the OTP provider catches and surfaces failures
    /// to the caller.
    /// </summary>
    Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Discards SMS messages instead of dispatching them. Suitable for dev, tests and demos,
/// never for production: outside the Development environment the host refuses to start
/// with it unless <see cref="SmsOtpOptions.AllowMockGateway"/> is set. It writes only a
/// masked phone number to stdout, never the message or the code; tests read messages
/// through the <c>onSend</c> callback.
/// </summary>
public sealed class MockSmsGateway : ISmsGateway
{
    private readonly Action<string, string>? _onSend;

    public MockSmsGateway(Action<string, string>? onSend = null)
    {
        _onSend = onSend;
    }

    /// <inheritdoc />
    public string Name => "Mock";

    /// <inheritdoc />
    public Task SendAsync(string phoneNumber, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _onSend?.Invoke(phoneNumber, message);
        Console.WriteLine($"[MockSmsGateway] message to {SmsOtpProvider.MaskPhone(phoneNumber)} discarded.");
        return Task.CompletedTask;
    }
}
