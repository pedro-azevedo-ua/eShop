
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using eShop.WebApp;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using Npgsql;
using NLog.Web;
using NLog;

var logger = LogManager.GetCurrentClassLogger();

try
{

    logger.Debug("Init main");
    var builder = WebApplication.CreateBuilder(args);

    builder.AddServiceDefaults();

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



    builder.Services.AddRazorComponents().AddInteractiveServerComponents();

    builder.Services.AddSingleton<Instrumentation>();


    builder.AddApplicationServices();

    var app = builder.Build();


    app.MapDefaultEndpoints();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error");
        app.UseHsts();
    }

    app.UseAntiforgery();

    app.UseHttpsRedirection();

    app.UseStaticFiles();

    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    app.MapForwarder("/product-images/{id}", "http://catalog-api", "/api/catalog/items/{id}/pic");

    app.UseOpenTelemetryPrometheusScrapingEndpoint();


    app.Run(); 
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
