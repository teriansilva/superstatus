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
            options.SignIn.RequireConfirmedAccount = false;

            // Length-only password policy: 12-char minimum, no character-class
            // requirements. Closer to current NIST guidance than the framework
            // defaults (which require digit + uppercase + non-alphanumeric).
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredUniqueChars = 1;
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

                // When IDP_PUBLIC_HTTP is set, pin it as the issuer so discovery,
                // endpoints, and issued tokens advertise the public browser URL
                // regardless of which host (public front-channel or internal
                // back-channel) a request arrives on. This keeps the issuer
                // consistent across both channels in the single-host self-host
                // stack. Left unset in the reverse-proxy deployment, where the
                // issuer is derived from the forwarded public Host header.
                var idpPublic = Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP");
                if (!string.IsNullOrWhiteSpace(idpPublic))
                {
                    serverBuilder.SetIssuer(new Uri(idpPublic, UriKind.Absolute));
                }

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