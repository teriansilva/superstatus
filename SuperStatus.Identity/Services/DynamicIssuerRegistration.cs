using Microsoft.AspNetCore; // OpenIddictServerAspNetCoreHelpers.GetHttpRequest extension
using OpenIddict.Server;
using SuperStatus.ServiceDefaults;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;
using static OpenIddict.Server.OpenIddictServerHandlers;

namespace SuperStatus.Identity.Services;

/// <summary>
/// Issue #358 — wires the self-host dynamic-issuer hardening. A no-op unless
/// dynamic mode is active (both <c>IDP_PUBLIC_HTTP</c> and <c>WEBAPP_HTTP</c>
/// unset), so the pinned (IDP_PUBLIC_HTTP) and reverse-proxy (WEBAPP_HTTP) paths
/// are completely untouched.
///
/// In dynamic mode the OIDC issuer is pinned to the internal authority (IDP_HTTP,
/// in SuperStatusIdentityDbRegistrations) so the authorize + token endpoints agree
/// on it; the browser reaches the published identity port via Web's redirect rewrite.
/// What IS dynamic here is redirect_uri validation: there is no fixed <c>WEBAPP_HTTP</c>
/// to seed, so the static <c>redirect_uri</c>-against-registered-client check no longer
/// fits. We swap it for a <b>same-origin</b> check (the callback must return to the host
/// the login arrived on, on the web port) and add a <b>host gate</b> (once the operator
/// pins an allowlist, only those hosts may drive login). The same-origin rule is what
/// keeps this from being an open redirector even in the relaxed first-run state.
/// </summary>
public static class DynamicIssuerRegistration
{
    public static IServiceCollection AddDynamicIssuerHardening(this IServiceCollection services)
    {
        // Only the no-proxy two-port self-host stack gets the dynamic handlers.
        // Pinned (IDP_PUBLIC_HTTP set) and reverse-proxy (WEBAPP_HTTP set) are both
        // left as-is — see IdpAuthority.IsDynamicSelfHost.
        if (!IdpAuthority.IsDynamicSelfHost(
                Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP"),
                Environment.GetEnvironmentVariable("WEBAPP_HTTP")))
            return services;

        var apiBase = Environment.GetEnvironmentVariable("API_INTERNAL_HTTP");
        if (string.IsNullOrWhiteSpace(apiBase)) apiBase = "http://api:8080";

        services.AddHttpClient(CachedAuthHostPolicy.HttpClientName, client =>
        {
            client.BaseAddress = new Uri(apiBase!, UriKind.Absolute);
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddSingleton<IAuthHostPolicy>(sp =>
            new CachedAuthHostPolicy(sp.GetRequiredService<IHttpClientFactory>()));

        // Add the dynamic-mode handlers to the (already-configured) OpenIddict server.
        // AddServer is additive, so this composes with the base config in
        // SuperStatusIdentityDbRegistrations without duplicating it.
        services.AddOpenIddict().AddServer(builder =>
        {
            // Replace the built-in "redirect_uri / post_logout_redirect_uri must match
            // a registered client URI" checks — in dynamic mode there is no fixed
            // registered URI. Both are re-validated same-origin below.
            builder.RemoveEventHandler(Authentication.ValidateClientRedirectUri.Descriptor);
            builder.RemoveEventHandler(Session.ValidateClientPostLogoutRedirectUri.Descriptor);

            // Authorization request: host gate + same-origin redirect_uri.
            builder.AddEventHandler<ValidateAuthorizationRequestContext>(handler =>
                handler.UseInlineHandler(async context =>
                {
                    var request = context.Transaction.GetHttpRequest();
                    if (request is null) return; // non-HTTP transport — nothing to gate.

                    var policy = request.HttpContext.RequestServices.GetRequiredService<IAuthHostPolicy>();
                    var allowlist = await policy.GetAllowlistAsync(request.HttpContext.RequestAborted);

                    // Host gate: hardened (allowlist non-empty) ⇒ the browser's host
                    // must be on the list. Relaxed (empty) ⇒ accept any host.
                    if (allowlist.Count > 0 && !AuthHostAllowlist.Allows(allowlist, request.Host.Host, request.Host.Port))
                    {
                        context.Reject(
                            error: Errors.InvalidRequest,
                            description: "This host is not an allowed sign-in host for this server.");
                        return;
                    }

                    // Same-origin redirect (enforced in BOTH relaxed and hardened): the
                    // callback must return to the login host, on the web port, at
                    // /signin-oidc. This is the open-redirector guard.
                    if (!string.IsNullOrEmpty(context.RedirectUri)
                        && !AuthHostAllowlist.IsSameOriginCallback(
                            context.RedirectUri, request.Host.Host, policy.WebPort, "/signin-oidc", request.Scheme))
                    {
                        context.Reject(
                            error: Errors.InvalidRequest,
                            description: "The redirect_uri must return to the host you signed in from.");
                    }
                })
                .SetOrder(Authentication.ValidateClientRedirectUri.Descriptor.Order)
                .SetType(OpenIddictServerHandlerType.Custom));

            // End-session (logout) request: same host gate + same-origin
            // post_logout_redirect_uri (absent is allowed — a logout with no return).
            builder.AddEventHandler<ValidateEndSessionRequestContext>(handler =>
                handler.UseInlineHandler(async context =>
                {
                    var request = context.Transaction.GetHttpRequest();
                    if (request is null) return;

                    var policy = request.HttpContext.RequestServices.GetRequiredService<IAuthHostPolicy>();
                    var allowlist = await policy.GetAllowlistAsync(request.HttpContext.RequestAborted);

                    if (allowlist.Count > 0 && !AuthHostAllowlist.Allows(allowlist, request.Host.Host, request.Host.Port))
                    {
                        context.Reject(
                            error: Errors.InvalidRequest,
                            description: "This host is not an allowed sign-in host for this server.");
                        return;
                    }

                    if (!string.IsNullOrEmpty(context.PostLogoutRedirectUri)
                        && !AuthHostAllowlist.IsSameOriginCallback(
                            context.PostLogoutRedirectUri, request.Host.Host, policy.WebPort, "/signout-callback-oidc", request.Scheme))
                    {
                        context.Reject(
                            error: Errors.InvalidRequest,
                            description: "The post_logout_redirect_uri must return to the host you signed in from.");
                    }
                })
                .SetOrder(Session.ValidateClientPostLogoutRedirectUri.Descriptor.Order)
                .SetType(OpenIddictServerHandlerType.Custom));
        });

        return services;
    }
}
