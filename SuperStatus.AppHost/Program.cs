var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.SuperStatus_ApiService>("apiservice");

var webFrontend = builder.AddProject<Projects.SuperStatus_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService);

var identityProvider = builder.AddProject<Projects.SuperStatus_Identity>("superstatus-identity")
    .WithExternalHttpEndpoints();

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
