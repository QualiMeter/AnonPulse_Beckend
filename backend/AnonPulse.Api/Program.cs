using Scalar.AspNetCore;
using AnonPulse.Api.Bootstrap;
using AnonPulse.Api.Data;
using AnonPulse.Api.Development;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Admin;
using AnonPulse.Api.Services.Decisions;
using AnonPulse.Api.Services.Authentication;
using AnonPulse.Api.Services.Polls;
using AnonPulse.Api.Services.PollResults;
using AnonPulse.Api.Services.Voting;
using AnonPulse.Api.Services.FeedbackAnalysis;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

const string BootstrapInitialTeacherArgument = "--bootstrap-initial-teacher";
const string BootstrapAdminArgument = "--bootstrap-admin";
const string CreateDevelopmentTeacherArgument = "--create-development-teacher";
const string BrowserCorsPolicy = "BrowserApiClients";

bool bootstrapInitialTeacher = args.Contains(
    BootstrapInitialTeacherArgument,
    StringComparer.Ordinal);
bool bootstrapAdmin = args.Contains(
    BootstrapAdminArgument,
    StringComparer.Ordinal);
bool createDevelopmentTeacher = args.Contains(
    CreateDevelopmentTeacherArgument,
    StringComparer.Ordinal);
string[] applicationArguments = args
    .Where(argument =>
        !string.Equals(
            argument,
            BootstrapInitialTeacherArgument,
            StringComparison.Ordinal) &&
        !string.Equals(
            argument,
            BootstrapAdminArgument,
            StringComparison.Ordinal) &&
        !string.Equals(
            argument,
            CreateDevelopmentTeacherArgument,
            StringComparison.Ordinal))
    .ToArray();

var builder = WebApplication.CreateBuilder(applicationArguments);

// Регистрируем сервисы приложения
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-AnonPulse.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
// CORS is needed only for browser clients hosted on a different origin.
// The Scalar UI served at /scalar/ is same-origin and does not need CORS.
// Add other trusted frontend origins via Cors__AllowedOrigins__0, __1, etc.
// Never use AllowAnyOrigin() together with AllowCredentials().
string[] additionalCorsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];
string[] browserOrigins = [
    "https://client.scalar.com",
    .. additionalCorsOrigins
        .Select(origin => origin.Trim().TrimEnd('/'))
        .Where(origin => origin.Length > 0)
];

builder.Services.AddCors(options =>
{
    options.AddPolicy(BrowserCorsPolicy, policy =>
        policy.WithOrigins(browserOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<AuthenticationSessionOptions>()
    .Bind(builder.Configuration.GetSection(AuthenticationSessionOptions.SectionName))
    .Validate(options => options.SessionLifetime > TimeSpan.Zero,
        "Authentication session lifetime must be greater than zero.")
    .Validate(options => options.PersistentLifetime > TimeSpan.Zero,
        "Persistent authentication lifetime must be greater than zero.")
    .Validate(options =>
            options.PersistentLifetime <=
            AuthenticationSessionOptions.MaximumPersistentLifetime,
        "Persistent authentication lifetime must not exceed 30 days.")
    .ValidateOnStart();
builder.Services.AddScoped<IDecisionService, DecisionService>();
builder.Services.AddScoped<IAdminAccessChecker, AdminAccessChecker>();
builder.Services.AddScoped<IAdminDashboardService, AdminDashboardService>();
builder.Services.AddScoped<IAdminUserService, AdminUserService>();
builder.Services.AddScoped<IAdminPollService, AdminPollService>();
builder.Services.AddScoped<IPollDeletionService, PollDeletionService>();
builder.Services.AddScoped<IAdminAuditService, AdminAuditService>();
builder.Services.AddScoped<IPollService, PollService>();
builder.Services.AddScoped<IPollResultsService, PollResultsService>();
builder.Services.AddScoped<IPublicVotingService, PublicVotingService>();
builder.Services.AddScoped<IPollTextAnswerService, PollTextAnswerService>();
builder.Services.AddScoped<IPollSubmissionService, PollSubmissionService>();
builder.Services.AddScoped<IFeedbackAnalysisService, FeedbackAnalysisService>();
builder.Services.AddScoped<IFeedbackAnalysisJobProcessor, FeedbackAnalysisJobProcessor>();
builder.Services.AddScoped<IFeedbackAnalysisJobQueue, FeedbackAnalysisJobQueue>();
builder.Services.AddScoped<IFeedbackAnalysisProvider, GroqFeedbackAnalysisProvider>();
builder.Services.AddSingleton<IPersonalDataSanitizer, PersonalDataSanitizer>();
builder.Services.AddHostedService<FeedbackAnalysisWorker>();
builder.Services.AddOptions<AiOptions>()
    .Bind(builder.Configuration.GetSection(AiOptions.SectionName))
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 600,
        "AI timeout must be between 1 and 600 seconds.")
    .Validate(options => options.MaxRetries is >= 0 and <= 5,
        "AI maximum retries must be between 0 and 5.")
    .Validate(options => options.MaxConcurrentJobs is >= 1 and <= 10,
        "AI concurrent job limit must be between 1 and 10.")
    .Validate(options => options.MaxAnswers > 0 && options.MaxTotalCharacters > 0 &&
                         options.MaxInputTokens > 0 && options.MaxOutputTokens > 0,
        "AI analysis limits must be positive.")
    .Validate(options => options.WorkerPollSeconds is >= 1 and <= 60,
        "AI worker poll interval must be between 1 and 60 seconds.")
    .Validate(options => Uri.TryCreate(options.Groq.BaseUrl, UriKind.Absolute, out _),
        "Groq base URL must be an absolute URL.")
    .ValidateOnStart();
builder.Services.AddHttpClient("groq-feedback-analysis", (serviceProvider, client) =>
{
    AiOptions options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
    client.BaseAddress = new Uri(options.Groq.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
});
builder.Services.AddOptions<TextAnswerOptions>()
    .Bind(builder.Configuration.GetSection(TextAnswerOptions.SectionName))
    .Validate(options => options.MaximumLength is > 0 and <= TextAnswerOptions.StorageMaximumLength,
        "Text answer maximum length must be between 1 and 4000.")
    .Validate(options => options.MaximumRequestBodySize is >= 1024 and <= 10_485_760,
        "Text answer request body limit must be between 1 KiB and 10 MiB.")
    .Validate(options => options.MaximumAnswersPerRequest is > 0 and <= 1000,
        "Maximum answers per request must be between 1 and 1000.")
    .ValidateOnStart();
builder.Services.AddSingleton<IAnonymousVoterTokenService, AnonymousVoterTokenService>();
builder.Services.AddSingleton<IAnonymousVotingSessionRateLimiter,
    AnonymousVotingSessionRateLimiter>();
builder.Services.AddOptions<AnonymousVoterOptions>()
    .Bind(builder.Configuration.GetSection(AnonymousVoterOptions.SectionName))
    .Validate(options => options.CookieName.StartsWith("__Host-", StringComparison.Ordinal),
        "Anonymous voting cookie name must use the __Host- prefix.")
    .Validate(options => options.CookieLifetime > TimeSpan.Zero,
        "Anonymous voting cookie lifetime must be greater than zero.")
    .Validate(options => options.TimestampPrecision > TimeSpan.Zero,
        "Anonymous voting timestamp precision must be greater than zero.")
    .Validate(options => options.RateLimitWindow > TimeSpan.Zero,
        "Anonymous voting rate-limit window must be greater than zero.")
    .Validate(options => options.SessionRequestsPerWindow > 0,
        "Anonymous session rate limit must be greater than zero.")
    .Validate(options => options.GlobalRequestsPerWindow >=
                         options.SessionRequestsPerWindow,
        "Global voting rate limit must not be lower than the session limit.")
    .Validate(options => options.MaxTrackedSessions > 0,
        "Maximum tracked anonymous sessions must be greater than zero.")
    .Validate(options => options.SessionTrackingRetention >= options.RateLimitWindow,
        "Session tracking retention must not be shorter than the rate-limit window.")
    .ValidateOnStart();
builder.Services.AddRateLimiter(rateLimiterOptions =>
{
    int permitLimit = builder.Configuration.GetValue<int?>(
        $"{AnonymousVoterOptions.SectionName}:GlobalRequestsPerWindow") ?? 600;
    TimeSpan window = builder.Configuration.GetValue<TimeSpan?>(
        $"{AnonymousVoterOptions.SectionName}:RateLimitWindow") ??
        TimeSpan.FromMinutes(1);
    rateLimiterOptions.AddPolicy("public-voting", _ =>
        RateLimitPartition.GetFixedWindowLimiter(
            "public-voting-global",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    rateLimiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
builder.Services
    .AddDataProtection()
    .SetApplicationName("AnonPulse")
    .PersistKeysToDbContext<ApplicationDbContext>();
builder.Services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
{
    string connectionString = serviceProvider
                                  .GetRequiredService<IConfiguration>()
                                  .GetConnectionString("DefaultConnection")
                              ?? throw new InvalidOperationException(
                                  "Строка подключения к PostgreSQL не найдена.");

    options.UseNpgsql(connectionString);
});
builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddSignInManager<ApplicationSignInManager>();
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.Zero);
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
        options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
    })
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "__Host-AnonPulse.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.ExpireTimeSpan = builder.Configuration.GetValue<TimeSpan?>(
            $"{AuthenticationSessionOptions.SectionName}:SessionLifetime") ??
            TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal =
            AdministrativeDeactivationCookieValidator.ValidateAsync;
        options.Events.OnRedirectToLogin = context =>
            WriteAuthenticationProblemAsync(
                context.HttpContext,
                StatusCodes.Status401Unauthorized,
                "Authentication is required.");
        options.Events.OnRedirectToAccessDenied = context =>
            WriteAuthenticationProblemAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "Access is forbidden.");
    })
    .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
    })
    .AddCookie(IdentityConstants.TwoFactorUserIdScheme, options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
    });
// Создаём приложение
var app = builder.Build();

if (bootstrapInitialTeacher)
{
    try
    {
        await InitialTeacherBootstrapper.CreateAsync(app.Services, app.Configuration);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Initial teacher bootstrap failed: {exception.Message}");
        Environment.ExitCode = 1;
    }

    return;
}

if (bootstrapAdmin)
{
    try
    {
        await AdminBootstrapper.CreateAsync(app.Services, app.Configuration);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Administrator bootstrap failed: {exception.Message}");
        Environment.ExitCode = 1;
    }

    return;
}

// Команда создания тестового преподавателя
if (createDevelopmentTeacher)
{
    await DevelopmentTeacherCreator.CreateAsync(app);
    return;
}

// Документация API: доступна в Development
// или при включении через переменную окружения
bool enableApiDocs =
    app.Environment.IsDevelopment() ||
    app.Configuration.GetValue<bool>("ApiDocs:Enabled");

if (enableApiDocs)
{
    app.MapOpenApi();

    app.MapScalarApiReference(options =>
    {
        options.AddServer(
            "https://anonpulsebeckend-production.up.railway.app",
            "Production");
    });
}

// Проверка БД доступна только в Development
if (app.Environment.IsDevelopment())
{
    app.MapGet("/dev/db-check", async (ApplicationDbContext db) =>
    {
        bool connected = await db.Database.CanConnectAsync();
        return Results.Ok(new { connected });
    }).ExcludeFromDescription();
}


// Настраиваем HTTP pipeline
// Route matching must run before CORS and endpoint-specific rate limiting.
// Handle CORS preflight (OPTIONS) before possible HTTPS redirection: browsers
// cannot follow an HTTP-to-HTTPS redirect during preflight.
app.UseRouting();
app.UseCors(BrowserCorsPolicy);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Подключаем маршруты контроллеров
app.MapControllers();

// Запускаем веб-сервер
app.Run();

static Task WriteAuthenticationProblemAsync(
    HttpContext context,
    int statusCode,
    string title)
{
    context.Response.StatusCode = statusCode;

    return context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = statusCode,
        Title = title
    },
    options: null,
    contentType: "application/problem+json",
    cancellationToken: context.RequestAborted);
}

public partial class Program;
