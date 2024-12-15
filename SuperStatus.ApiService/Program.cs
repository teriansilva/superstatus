using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using SuperStatus.ApiService.Configuration;
using SuperStatus.Configuration;
using SuperStatus.Data.Repositories;
using SuperStatus.Services;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

builder.Services.AddApplicationServices(builder);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

Directory.CreateDirectory("Database");
using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<SuperStatusContext>();
    await dbContext.Database.MigrateAsync();
}

app.MapGet("/statuscheck", async (IStatusCheckService statusCheckService) =>
{
    return await statusCheckService.GetStatusCheckViewModelSet();
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

app.Run();
