using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SuperStatus.ApiService.Configuration;
using SuperStatus.Configuration;
using SuperStatus.Data.Repositories;
using SuperStatus.Services;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme =
        JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme =
        JwtBearerDefaults.AuthenticationScheme;

}).AddJwtBearer(options =>
{
    options.Authority = Environment.GetEnvironmentVariable(
        "IDP_HTTP");
    options.RequireHttpsMetadata = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateAudience = false
    };
});

builder.Services.AddAuthorization();
builder.Services.AddApplicationServices(builder);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

// crete db
Directory.CreateDirectory("Database");
using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<SuperStatusContext>();
    await dbContext.Database.MigrateAsync();
    // Seed the database
    var seeder = scope.ServiceProvider.GetRequiredService<SuperStatusSeeder>();
    await seeder.SeedAsync();
}

app.MapGet("/statuscheck", async (IStatusCheckService statusCheckService) =>
{
    var statusCheck = await statusCheckService.GetStatusCheckViewModelSet();
    if(statusCheck.Count == 0)
    {
        return Results.NotFound("No status checks found.");
    }
    return Results.Ok(statusCheck);
});

app.MapGet("/historicalStatusData/{id}", async (int id, IStatusCheckService statusCheckService) =>
{
    return await statusCheckService.GetHistoricalStatusDataOverviewForRecentTimeRange(id, SuperStatusConfig.StatusCheckGraphViewMaxDays);
});

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.Run();
