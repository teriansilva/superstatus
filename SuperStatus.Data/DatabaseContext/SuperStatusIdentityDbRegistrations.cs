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

                // Pin a STABLE issuer so the authorization-issued code and the
                // back-channel token exchange agree on it — otherwise OpenIddict
                // rejects the code redemption with ID2088 ("the issuer associated to
                // the specified token is not valid"). Three shapes:
                //   • IDP_PUBLIC_HTTP set (pinned/cloud)          → that public URL.
                //   • #358 dynamic self-host (both env unset)     → the INTERNAL host
                //     (IDP_HTTP). The browser still reaches the published identity
                //     port via Web's OnRedirectToIdentityProvider; only the issuer
                //     STRING is stabilized to the internal host, so the front-channel
                //     authorize and the back-channel token endpoint stamp the same
                //     issuer. Without this, authorize (browser host) and token
                //     (internal host) derive different issuers → ID2088 at login.
                //   • reverse-proxy (WEBAPP_HTTP set, IDP_PUBLIC_HTTP unset) → leave
                //     unset: OpenIddict derives it from the single forwarded Host.
                var idpPublic = Environment.GetEnvironmentVariable("IDP_PUBLIC_HTTP");
                var webAppHttp = Environment.GetEnvironmentVariable("WEBAPP_HTTP");
                var idpInternal = Environment.GetEnvironmentVariable("IDP_HTTP");
                if (!string.IsNullOrWhiteSpace(idpPublic))
                {
                    serverBuilder.SetIssuer(new Uri(idpPublic, UriKind.Absolute));
                }
                else if (string.IsNullOrWhiteSpace(webAppHttp) && !string.IsNullOrWhiteSpace(idpInternal))
                {
                    // dynamic self-host (both public + webapp unset) → pin internal.
                    serverBuilder.SetIssuer(new Uri(idpInternal, UriKind.Absolute));
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