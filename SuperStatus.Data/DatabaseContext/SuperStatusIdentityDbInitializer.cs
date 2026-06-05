using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SuperStatus.Data.DatabaseContext;

public static class SuperStatusIdentityDbInitializer
{
    // Canonical name of the single role created for the first (and, in v1,
    // only) admin user. Referenced from the UI-driven first-run setup page;
    // kept on the initializer because it represents identity-DB-level state
    // that lives next to migrations + OIDC client seeding.
    public const string AdministratorRole = "Administrator";

    public static async Task Seed(
        IServiceProvider serviceProvider,
        bool applyMigrations)
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
    }

    private static async Task SeedAuthorizationClients(
        IOpenIddictApplicationManager manager,
        IOpenIddictScopeManager scopeManager,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var webAppBaseUrl = Environment.GetEnvironmentVariable("WEBAPP_HTTP");
        if (string.IsNullOrWhiteSpace(webAppBaseUrl))
        {
            logger.LogWarning(
                "WEBAPP_HTTP is not set — skipping OIDC client seed for 'aspNetCoreAuth'. " +
                "The client will be created on a subsequent boot once WEBAPP_HTTP is configured.");
            return;
        }

        // OIDC_WEB_CLIENT_SECRET must match the value Web/Program.cs uses
        // for both the OIDC code flow and client-credentials clients. The
        // "some_secret" fallback only kicks in when the env var is missing
        // (dev / Aspire) so the existing developer experience is untouched.
        var oidcWebClientSecret =
            Environment.GetEnvironmentVariable("OIDC_WEB_CLIENT_SECRET")
            ?? "some_secret";

        var trimmed = webAppBaseUrl.TrimEnd('/');

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
                trimmed);
            return;
        }

        await manager.CreateAsync(descriptor, cancellationToken);

        logger.LogInformation(
            "Seeded OpenIddict client 'aspNetCoreAuth' (redirect base {WebApp}).",
            trimmed);
    }

    public static OpenIddictApplicationDescriptor BuildAspNetCoreAuthDescriptor(
        string trimmedWebAppBaseUrl, string clientSecret)
    {
        return new OpenIddictApplicationDescriptor
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
            RedirectUris =
            {
                new Uri(trimmedWebAppBaseUrl + "/signin-oidc")
            },
            PostLogoutRedirectUris =
            {
                new Uri(trimmedWebAppBaseUrl + "/signout-callback-oidc")
            },
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
    }

}
