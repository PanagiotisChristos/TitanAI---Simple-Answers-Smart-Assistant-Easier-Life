using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Project_no._1.Api;
using Project_no._1.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 10 * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 8 * 1024 * 1024;
    options.ValueLengthLimit = 16 * 1024;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("ask", limiterOptions =>
    {
        limiterOptions.PermitLimit = 30;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
    options.AddFixedWindowLimiter("upload", limiterOptions =>
    {
        limiterOptions.PermitLimit = 5;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
        limiterOptions.AutoReplenishment = true;
    });
    options.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new
        {
            title = "A little too fast",
            detail = "Please wait a minute before sending more requests.",
            status = StatusCodes.Status429TooManyRequests
        }, cancellationToken));
    };
});
builder.Services.AddHttpClient("Groq", client =>
{
    client.BaseAddress = new Uri("https://api.groq.com/openai/v1/");
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient("TavilySearch", client =>
{
    client.BaseAddress = new Uri("https://api.tavily.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IGroqService, GroqService>();
builder.Services.AddSingleton<IWebSearchService, TavilyWebSearchService>();
builder.Services.AddSingleton<IWebSearchQuotaStore, SqliteWebSearchQuotaStore>();
builder.Services.AddSingleton<IKnowledgeDocumentStore, KnowledgeDocumentStore>();
builder.Services.AddSingleton<IMissionStore, SqliteMissionStore>();

var app = builder.Build();

var missionStore = app.Services.GetRequiredService<IMissionStore>();
await missionStore.InitializeAsync(CancellationToken.None);
var webSearchQuotaStore = app.Services.GetRequiredService<IWebSearchQuotaStore>();
await webSearchQuotaStore.InitializeAsync(CancellationToken.None);

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.MapOpenApi();
app.MapMissionEndpoints();
app.MapGet("/api/status", async (IWebSearchQuotaStore webSearchQuota, CancellationToken cancellationToken) =>
    TypedResults.Ok(new StatusResponse(
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY")),
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TAVILY_API_KEY")),
        await webSearchQuota.GetMonthlyRemainingAsync(cancellationToken))))
    .WithSummary("Check whether the assistant is configured.");
app.MapAssistantEndpoints();

app.Run();

/// <summary>Assistant configuration status.</summary>
public sealed record StatusResponse(bool Configured, bool WebSearchConfigured, int WebSearchMonthlyRemaining);
