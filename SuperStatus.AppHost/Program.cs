var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin(pgAdmin => pgAdmin.WithHostPort(5050))
    .WithDataVolume("superstatus-data");

// Capture the database resources
var superStatusDb = postgres.AddDatabase("SuperStatusDb");
var superStatusIdentityDb = postgres.AddDatabase("SuperStatusIdentityDb");

// Wire the specific DB to the ApiService
var apiService = builder.AddProject<Projects.SuperStatus_ApiService>("apiservice")
    .WithExternalHttpEndpoints()
    .WithReference(superStatusDb)   // injects ConnectionStrings__SuperStatusDb
    .WaitFor(superStatusDb);

// Wire the identity DB to the Identity provider
var identityProvider = builder.AddProject<Projects.SuperStatus_Identity>("superstatus-identity")
    .WithExternalHttpEndpoints()
    .WithReference(superStatusIdentityDb)  // injects ConnectionStrings__SuperStatusIdentityDb
    .WaitFor(superStatusIdentityDb);

var webFrontend = builder.AddProject<Projects.SuperStatus_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService)
    .WithReference(identityProvider)
    .WaitFor(apiService)
    .WaitFor(identityProvider);

var webAppHttp = webFrontend.GetEndpoint("http");
var webAppHttps = webFrontend.GetEndpoint("https");

if (webAppHttps.Exists)
{
    identityProvider.WithEnvironment("WEBAPP_HTTP",
        () => $"{webAppHttps.Scheme}://{webAppHttps.Host}:{webAppHttps.Port}");
}
#if DEBUG
else //only for dev
{
    identityProvider.WithEnvironment("WEBAPP_HTTP",
        () => $"{webAppHttp.Scheme}://{webAppHttp.Host}:{webAppHttp.Port}");
}
#endif

var idpAppHttp = identityProvider.GetEndpoint("http");
var idpAppHttps = identityProvider.GetEndpoint("https");

if (idpAppHttps.Exists)
{
    webFrontend.WithEnvironment("IDP_HTTP",
        () => $"{idpAppHttps.Scheme}://{idpAppHttps.Host}:{idpAppHttps.Port}");
    apiService.WithEnvironment("IDP_HTTP",
        () => $"{idpAppHttps.Scheme}://{idpAppHttps.Host}:{idpAppHttps.Port}");
}
#if DEBUG
else //only for dev
{
    webFrontend.WithEnvironment("IDP_HTTP",
        () => $"{idpAppHttp.Scheme}://{idpAppHttp.Host}:{idpAppHttp.Port}");
    apiService.WithEnvironment("IDP_HTTP",
        () => $"{idpAppHttp.Scheme}://{idpAppHttp.Host}:{idpAppHttp.Port}");
}
#endif

builder.Build().Run();
