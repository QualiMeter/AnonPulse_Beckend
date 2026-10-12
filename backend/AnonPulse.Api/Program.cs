using Scalar.AspNetCore;
using AnonPulse.Api.Bootstrap;
using AnonPulse.Api.Data;
using AnonPulse.Api.Development;
using AnonPulse.Api.Models;
using AnonPulse.Api.Services.Polls;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

const string BootstrapInitialTeacherArgument = "--bootstrap-initial-teacher";
const string BootstrapAdminArgument = "--bootstrap-admin";
const string CreateDevelopmentTeacherArgument = "--create-development-teacher";

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
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPollService, PollService>();
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
    .AddSignInManager();
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
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
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
    app.MapScalarApiReference();
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
app.UseHttpsRedirection();
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
