using Microsoft.AspNetCore.DataProtection;

namespace Backend.Auth;

/// <summary>
/// Stops ASP.NET Core Data Protection from minting a key ring that nothing asked for.
///
/// <c>AddAuthentication</c> (called by <see cref="EntraAuthentication.AddEntraAuthentication"/>)
/// registers Data Protection as a side effect, and the framework's own hosted service then builds
/// a key ring at startup whether or not anything ever protects a payload. In a container that means
/// a <c>key-{guid}.xml</c> written unencrypted under the user profile and two warnings on every
/// start ("Storing keys in a directory ... that may not be persisted outside of the container" and
/// "No XML encryptor configured"). This backend has no Data Protection consumer: JwtBearer
/// validation is stateless and <see cref="SessionTokenService"/> signs with <c>APP_SESSION_SECRET</c>.
///
/// Both halves of <see cref="Configure"/> are required -- each fails alone (reproduced on the
/// net11.0 SDK in a Linux container, where the startup behaviour matches production):
/// <list type="bullet">
/// <item>Replacing <c>IDataProtectionProvider</c> with the in-memory provider does NOT silence
/// startup. The hosted service is registered independently of that provider and still asks the
/// key-ring provider for a ring, so the key file and both warnings appear anyway.</item>
/// <item>Removing the hosted service alone is not enough either: the first consumer that resolves
/// <c>IDataProtectionProvider</c> would then lazily create the file-system key ring.</item>
/// </list>
///
/// The hosted service is internal to the framework, so it is matched by its exact full name.
/// Anchoring on an assembly instead matches nothing (the type does not live in the assembly that
/// defines <c>IDataProtectionProvider</c>) and silently removes zero descriptors; the unit tests
/// check the name against the real registration so a framework rename cannot turn this into a
/// no-op unnoticed.
///
/// Ordering: call this AFTER every <c>AddAuthentication</c>/<c>AddDataProtection</c> registration.
/// They register the hosted service with <c>TryAddEnumerable</c>, so an earlier removal is simply
/// undone by a later registration. Program.cs places the call immediately before
/// <c>builder.Build()</c>, and the conformance suite watches the real process's startup output.
///
/// Trade-off, stated plainly: keys exist only in this process's memory, so a restart (or a second
/// replica) cannot read a payload another instance protected. That costs nothing while nothing
/// consumes Data Protection. Adding a consumer (cookie authentication, antiforgery, an
/// <c>IDataProtector</c>) needs real persistence instead -- keys in shared storage, protected at
/// rest -- and this call must then be removed. The framework flags the situation itself with an
/// Information line when the provider is first resolved ("Using ephemeral data protection
/// provider. Payloads will be undecipherable upon application shutdown."), but appsettings.json
/// caps <c>Microsoft.AspNetCore</c> at Warning, so that line stays hidden until the
/// <c>Microsoft.AspNetCore.DataProtection</c> log category is lowered.
/// </summary>
internal static class EphemeralDataProtection
{
    /// <summary>Full name of the framework's internal hosted service that builds the key ring at startup.</summary>
    internal const string KeyRingHostedServiceTypeName = "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService";

    /// <summary>
    /// Switches Data Protection to the in-memory provider and removes the startup key-ring hosted
    /// service. Must run after all authentication/Data Protection registrations (see the type's
    /// doc comment).
    /// </summary>
    public static void Configure(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDataProtection().UseEphemeralDataProtectionProvider();

        for (int i = services.Count - 1; i >= 0; i--)
        {
            ServiceDescriptor descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType?.FullName == KeyRingHostedServiceTypeName)
            {
                services.RemoveAt(i);
            }
        }
    }
}
