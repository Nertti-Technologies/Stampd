namespace Stampd.WebApi.Auth;

/// <summary>Names of the authorization policies registered in Program.cs.</summary>
internal static class StampdPolicies
{
    /// <summary>Any authenticated caller, including ReadOnly. For read routes.</summary>
    public const string AuthenticatedAny = "AuthenticatedAny";

    /// <summary>Sender or Admin role. Every route that creates, changes or deletes data.</summary>
    public const string SenderOrAdmin = "SenderOrAdmin";
}
