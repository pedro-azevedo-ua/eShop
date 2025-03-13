

using NLog;
using NLog.Web;
using OpenTelemetry.Resources;

using OpenTelemetry.Trace;
using eShop.WebApp;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Npgsql;


var logger = LogManager.GetCurrentClassLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);


    builder.Logging.ClearProviders();
    builder.Host.UseNLog();

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: Instrumentation.ActivitySourceName,
            serviceVersion: Instrumentation.ActivitySourceVersion))
        .WithTracing(tracing => tracing
            .AddSource(Instrumentation.ActivitySourceName)
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddNpgsql()
            .AddConsoleExporter()
            .AddOtlpExporter(opts =>
            {
                opts.Endpoint = new Uri("http://localhost:4317");
            }))
       .WithMetrics(metrics => metrics
            .AddMeter(Instrumentation.ActivitySourceName)
            .AddMeter("eShop.WebApp.Basket")
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddConsoleExporter()
            .AddPrometheusExporter());

    builder.AddBasicServiceDefaults();
    builder.AddApplicationServices();


    builder.Services.AddSingleton<Instrumentation>();


    builder.Services.AddGrpc();

    var app = builder.Build();

    app.MapDefaultEndpoints();


    app.MapGrpcService<BasketService>();

    app.Run();

    app.UseOpenTelemetryPrometheusScrapingEndpoint();

}
catch (Exception exception)
{
    // NLog: catch setup errors
    logger.Error(exception, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    NLog.LogManager.Shutdown();
}

