using Scalar.AspNetCore;
using AnonPulse.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Регистрируем сервисы приложения
builder.Services.AddControllers();
builder.Services.AddOpenApi();
var connectionString = builder.Configuration
                           .GetConnectionString("DefaultConnection")
                       ?? throw new InvalidOperationException(
                           "Строка подключения к PostgreSQL не найдена.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
// Создаём приложение
var app = builder.Build();

// Документация API только для разработки
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
    
    app.MapGet("/dev/db-check", async (ApplicationDbContext db) =>
    {
        bool connected = await db.Database.CanConnectAsync();

        return Results.Ok(new { connected });
    }).ExcludeFromDescription();
}

// Настраиваем HTTP pipeline
app.UseHttpsRedirection();
app.UseAuthorization();

// Подключаем маршруты контроллеров
app.MapControllers();

// Запускаем веб-сервер
app.Run();