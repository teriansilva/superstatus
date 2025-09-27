using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SuperStatus.Data.DatabaseContext;

public static class SuperStatusIdentityDbInitializer
{
    public static async Task Seed(IServiceProvider serviceProvider, bool isDevEnvironment)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SuperStatusIdentityDb>();
        if (isDevEnvironment)
        {
            await dbContext.Database.MigrateAsync();
        }
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        await SeedAuthorizationClients(dbContext, manager, scopeManager, new CancellationToken());
    }

    private static async Task SeedAuthorizationClients(SuperStatusIdentityDb context, IOpenIddictApplicationManager manager, IOpenIddictScopeManager scopeManager, CancellationToken cancellationToken)
    {


        var existing = await manager.FindByClientIdAsync("aspNetCoreAuth", cancellationToken);

        if (existing == null)
        {
            await manager.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "aspNetCoreAuth",
                ClientSecret = "some_secret",
                ConsentType = ConsentTypes.Explicit,
                DisplayName = "Blazor WebAssembly client application",
                RedirectUris =
                {
                    new Uri(Environment.GetEnvironmentVariable("WEBAPP_HTTP") + "/signin-oidc")
                },
                PostLogoutRedirectUris =
                {
                    new Uri(Environment.GetEnvironmentVariable("WEBAPP_HTTP") + "/signout-callback-oidc")
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
            }, cancellationToken);
        }
    }

}