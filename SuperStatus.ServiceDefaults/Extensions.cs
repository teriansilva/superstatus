using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common .NET Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/dotnet/aspire/service-defaults
public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Issue #326: persist DataProtection keys so antiforgery tokens and auth
    /// cookies survive a container restart. Self-host runs Identity and Web as
    /// throwaway containers (Watchtower auto-update, <c>docker compose pull</c>),
    /// and the default keyring lives on the ephemeral container filesystem — so
    /// every recreate rotates it and invalidates every cookie the browser still
    /// holds, and the next form POST (e.g. first-run /Identity/Account/Setup)
    /// fails antiforgery with HTTP 400.
    ///
    /// Opt-in: persists only when <c>DATAPROTECTION_KEYS_DIR</c> is set (the
    /// compose deploys point it at a mounted volume). Absent — local Aspire dev —
    /// keeps the default ephemeral behaviour, so dev is unchanged.
    /// <paramref name="applicationName"/> isolates each app's keyring so two apps
    /// sharing a directory can't cross-decrypt each other's payloads.
    /// </summary>
    public static IHostApplicationBuilder AddSuperStatusDataProtection(
        this IHostApplicationBuilder builder, string applicationName)
    {
        var keysDir = builder.Configuration["DATAPROTECTION_KEYS_DIR"];
        if (string.IsNullOrWhiteSpace(keysDir))
        {
            return builder; // unconfigured (dev): keep the ephemeral default
        }

        Directory.CreateDirectory(keysDir);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
            .SetApplicationName(applicationName);

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    // Issue #86: custom status-check instruments. Registered by
                    // name (the meter itself lives in SuperStatus.Services, which
                    // this low-level project must not reference). Keep in sync
                    // with SuperStatus.Services.Telemetry.StatusCheckMetrics.MeterName.
                    .AddMeter("SuperStatus.Services.StatusCheck");
            })
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation()
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // We need /health and /alive in every environment so docker compose
        // healthchecks and the deploy workflow can probe readiness. The
        // Aspire scaffold gates these to Development by default for
        // information-disclosure reasons; at the reverse-proxy layer we
        // restrict /health and /alive from public traffic instead.
        // See https://aka.ms/dotnet/aspire/healthchecks for the rationale.
        app.MapHealthChecks("/health");

        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }
}
