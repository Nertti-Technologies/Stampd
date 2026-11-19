namespace Stampd.Identity.Kba;

/// <summary>Options for <see cref="KbaServiceCollectionExtensions.AddKbaIdentityVerification(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{KbaIdentityVerificationOptions})"/>.</summary>
public sealed class KbaIdentityVerificationOptions
{
    /// <summary>
    /// Lets the host start outside the Development environment while
    /// <see cref="MockKbaProvider"/> is the registered <see cref="IKbaProvider"/>. Leave
    /// false in production: the mock passes almost any answer.
    /// </summary>
    public bool AllowMockProvider { get; set; }
}
