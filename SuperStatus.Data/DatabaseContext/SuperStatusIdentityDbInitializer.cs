using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using SuperStatus.Data.Entities.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SuperStatus.Data.DatabaseContext;

public static class SuperStatusIdentityDbInitializer
{
    // Canonical name of the single role created for the first (and, in v1,
    // only) admin user. Referenced from the UI-driven first-run setup page;
    // kept on the initializer because it represents identity-DB-level state
    // that lives next to migrations + OIDC client seeding.
    public const string AdministratorRole = "Administrator";

    /// <summary>Issue #377: the public demo instance's well-known operator email.</summary>
    public const string DemoAdminEmail = "admin@superstatus.io";

    /// <summary>
    /// Issue #377: the public demo instance's well-known operator password. Five
    /// characters — deliberately below the 12-character
    /// <see cref="SuperStatusIdentityDbRegistrations"/> policy. See
    /// <see cref="SeedDemoAdministrator"/> for why that is safe and how it is done.
    /// </summary>
    public const string DemoAdminPassword = "admin";

    public static async Task Seed(
        IServiceProvider serviceProvider,
        bool applyMigrations,
        bool seedDemoAdmin = false)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SuperStatusIdentityDb>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(SuperStatusIdentityDbInitializer).FullName!);

        if (applyMigrations)
        {
            logger.LogInformation("Applying migrations on SuperStatusIdentityDb");
            await dbContext.Database.MigrateAsync();
        }

        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        await SeedAuthorizationClients(manager, scopeManager, logger, CancellationToken.None);

        if (seedDemoAdmin)
        {
            await SeedDemoAdministrator(scope.ServiceProvider, logger);
        }
    }

    /// <summary>
    /// Issue #377 — creates the public demo instance's well-known administrator, but
    /// only when the identity DB has no users at all (so an ordinary container restart
    /// re-runs this as a no-op, and it can never take over a real instance's user table).
    ///
    /// <para><b>Why the password is set by hash rather than by
    /// <c>CreateAsync(user, password)</c>.</b> The registered password policy requires
    /// 12 characters, and the demo credential is "admin". Relaxing the policy would
    /// weaken every SuperStatus deployment, so instead we use the no-password
    /// <c>CreateAsync</c> overload — which runs no <c>IPasswordValidator</c> — and set
    /// <c>PasswordHash</c> directly through the same <see cref="IPasswordHasher{TUser}"/>
    /// that Identity uses. Sign-in is unaffected: <c>PasswordSignInAsync</c> →
    /// <c>CheckPasswordAsync</c> only verifies the hash, it never re-runs the policy.
    /// And the policy still applies to <c>Manage/ChangePassword</c>, so even the demo
    /// operator cannot *set* a sub-policy password through the UI.</para>
    ///
    /// <para>Caller is responsible for only passing <c>seedDemoAdmin: true</c> when
    /// <c>PUBLIC_DEMO=true</c>.</para>
    /// </summary>
    private static async Task SeedDemoAdministrator(IServiceProvider services, ILogger logger)
    {
        var userManager = services.GetRequiredService<UserManager<SuperStatusIdentityUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var hasher = services.GetRequiredService<IPasswordHasher<SuperStatusIdentityUser>>();

        if (await userManager.Users.AnyAsync())
        {
            logger.LogInformation(
                "PUBLIC_DEMO is on but the identity DB already has users — demo administrator seed skipped.");
            return;
        }

        logger.LogWarning(
            "PUBLIC_DEMO is ENABLED. Seeding the well-known demo administrator '{Email}' with a " +
            "publicly documented password. This instance is intended to be wiped hourly and must " +
            "NEVER be used as a real deployment.",
            DemoAdminEmail);

        if (!await roleManager.RoleExistsAsync(AdministratorRole))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(AdministratorRole));
            if (!roleResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Failed to create the '{AdministratorRole}' role for the demo seed: "
                    + string.Join("; ", roleResult.Errors.Select(e => e.Description)));
            }
        }

        var user = new SuperStatusIdentityUser
        {
            UserName = DemoAdminEmail,
            Email = DemoAdminEmail,
            EmailConfirmed = true
        };

        // No-password overload: creates the user without running any IPasswordValidator.
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to create the demo administrator: "
                + string.Join("; ", createResult.Errors.Select(e => e.Description)));
        }

        user.PasswordHash = hasher.HashPassword(user, DemoAdminPassword);
        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            throw new InvalidOperationException(
                "Failed to set the demo administrator's password hash: "
                + string.Join("; ", updateResult.Errors.Select(e => e.Description)));
        }

        var roleAssign = await userManager.AddToRoleAsync(user, AdministratorRole);
        if (!roleAssign.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to add the demo administrator to '{AdministratorRole}': "
                + string.Join("; ", roleAssign.Errors.Select(e => e.Description)));
        }

        logger.LogInformation("Seeded demo administrator {Email}.", DemoAdminEmail);
    }

    private static async Task SeedAuthorizationClients(
        IOpenIddictApplicationManager manager,
        IOpenIddictScopeManager scopeManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var idpPublic = Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP");
        var webAppBaseUrl = Environment.GetEnvironmentVariable("WEBAPP_HTTP");

        // #358: "dynamic self-host" means BOTH IDP_PUBLIC_HTTP and WEBAPP_HTTP are
        // unset — the SAME discriminator as ServiceDefaults' IdpAuthority.IsDynamicSelfHost
        // (inlined here because SuperStatus.Data can't reference ServiceDefaults). In
        // that mode the client is seeded WITHOUT static redirect URIs; the redirect is
        // validated per-request (same-origin) by the dynamic OpenIddict handler. A
        // reverse-proxy / pinned deploy sets WEBAPP_HTTP and seeds redirects from it.
        // Only the odd "pinned issuer but no web URL" case (IDP_PUBLIC_HTTP set,
        // WEBAPP_HTTP unset) can't seed redirects — warn and skip there.
        var dynamicSelfHost = string.IsNullOrWhiteSpace(idpPublic) && string.IsNullOrWhiteSpace(webAppBaseUrl);
        if (string.IsNullOrWhiteSpace(webAppBaseUrl) && !dynamicSelfHost)
        {
            logger.LogWarning(
                "IDP_PUBLIC_HTTP is set but WEBAPP_HTTP is not — cannot seed the OIDC client's " +
                "redirect URIs for a pinned/reverse-proxy deployment. Set WEBAPP_HTTP to your public " +
                "web URL. Skipping 'aspNetCoreAuth' seed until then.");
            return;
        }

        // OIDC_WEB_CLIENT_SECRET must match the value Web/Program.cs uses
        // for both the OIDC code flow and client-credentials clients. The
        // "some_secret" fallback only kicks in when the env var is missing
        // (dev / Aspire) so the existing developer experience is untouched.
        var oidcWebClientSecret =
            Environment.GetEnvironmentVariable("OIDC_WEB_CLIENT_SECRET")
            ?? "some_secret";

        var trimmed = string.IsNullOrWhiteSpace(webAppBaseUrl) ? null : webAppBaseUrl.TrimEnd('/');
        var redirectBase = trimmed ?? "dynamic (per-request same-origin)";

        var descriptor = BuildAspNetCoreAuthDescriptor(trimmed, oidcWebClientSecret);

        // Reconcile on every boot, not just first run: a self-hoster moving from
        // the local trial to a real deployment changes WEBAPP_HTTP /
        // OIDC_WEB_CLIENT_SECRET, and the stored client must follow or Web hits
        // invalid_client / redirect-uri failures against a stale descriptor.
        var existing = await manager.FindByClientIdAsync("aspNetCoreAuth", cancellationToken);
        if (existing is not null)
        {
            await manager.UpdateAsync(existing, descriptor, cancellationToken);
            logger.LogInformation(
                "Reconciled OpenIddict client 'aspNetCoreAuth' (redirect base {WebApp}).",
                redirectBase);
            return;
        }

        await manager.CreateAsync(descriptor, cancellationToken);

        logger.LogInformation(
            "Seeded OpenIddict client 'aspNetCoreAuth' (redirect base {WebApp}).",
            redirectBase);
    }

    public static OpenIddictApplicationDescriptor BuildAspNetCoreAuthDescriptor(
        string? trimmedWebAppBaseUrl, string clientSecret)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = "aspNetCoreAuth",
            ClientSecret = clientSecret,
            // Set explicitly: CreateAsync infers Confidential from the presence of
            // a secret, but the descriptor-based UpdateAsync used on reconcile
            // validates ClientType and rejects a null value.
            ClientType = ClientTypes.Confidential,
            // Implicit consent: this is a first-party single-tenant client
            // (Web frontend) talking to its own Identity service, so showing
            // a "Do you authorize this app?" screen on every login is
            // operator-confusing noise rather than a meaningful prompt.
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Blazor WebAssembly client application",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Scopes.Roles,

                // Machine-to-machine (client credentials) + custom scope
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + "api"
            },
            Requirements =
            {
                Requirements.Features.ProofKeyForCodeExchange
            }
        };

        // #358: static redirect URIs are seeded only in pinned mode (a base URL was
        // provided). In dynamic self-host mode the base is null — the redirect_uri is
        // validated per-request (same-origin) by the OpenIddict handler, so leaving
        // the registered set empty is intentional, not a misconfiguration.
        if (!string.IsNullOrWhiteSpace(trimmedWebAppBaseUrl))
        {
            descriptor.RedirectUris.Add(new Uri(trimmedWebAppBaseUrl + "/signin-oidc"));
            descriptor.PostLogoutRedirectUris.Add(new Uri(trimmedWebAppBaseUrl + "/signout-callback-oidc"));
        }

        return descriptor;
    }

}
