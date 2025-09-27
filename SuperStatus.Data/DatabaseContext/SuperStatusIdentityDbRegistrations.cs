using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SuperStatus.Data.Entities.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SuperStatus.Data.DatabaseContext;

public static class SuperStatusIdentityDbRegistrations
{
    public static void AddSuperStatusIdentityDb(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<SuperStatusIdentityDb>(options =>
            options.UseNpgsql(configuration.GetConnectionString("SuperStatusIdentityDb"))
                   .UseOpenIddict());
    }

    public static void AddSuperStatusIdentity(this IServiceCollection services)
    {
        services.AddIdentity<SuperStatusIdentityUser, IdentityRole>(options =>
        {
            // For dev, consider disabling confirmation to simplify sign-in:
            options.SignIn.RequireConfirmedAccount = false;
        })
        .AddEntityFrameworkStores<SuperStatusIdentityDb>()
        .AddDefaultTokenProviders()
        .AddDefaultUI();

        services.AddOpenIddict()
            .AddCore(coreBuilder =>
            {
                coreBuilder.UseEntityFrameworkCore()
                           .UseDbContext<SuperStatusIdentityDb>();
            })
            .AddServer(serverBuilder =>
            {
                serverBuilder.DisableAccessTokenEncryption();

                serverBuilder.SetAuthorizationEndpointUris("connect/authorize")
                             .SetEndSessionEndpointUris("connect/logout")
                             .SetTokenEndpointUris("connect/token")
                             .SetUserInfoEndpointUris("connect/userinfo");

                serverBuilder.RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.Roles, "api");

                serverBuilder.AllowAuthorizationCodeFlow()
                             .RequireProofKeyForCodeExchange()
                             .AllowRefreshTokenFlow()
                             .AllowClientCredentialsFlow();


                serverBuilder.AddDevelopmentEncryptionCertificate()
                             .AddDevelopmentSigningCertificate();

                serverBuilder.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough()
                    .EnableStatusCodePagesIntegration()
                    .DisableTransportSecurityRequirement(); // dev only
            })
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
            });
    }
}