var builder = DistributedApplication.CreateBuilder(args);

var apiService = builder.AddProject<Projects.SuperStatus_ApiService>("apiservice");

builder.AddProject<Projects.SuperStatus_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService);

builder.Build().Run();
