using RequestsManagement.Api.Infrastructure.Mongo;
using RequestsManagement.Api.Infrastructure.Seeding;

const string SeedArgument = "--seed";

// The seed flag is removed before configuration is built so it is not parsed as a configuration key.
var seedRequested = args.Contains(SeedArgument);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != SeedArgument).ToArray());

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMongoDb(builder.Configuration);

var app = builder.Build();

if (seedRequested)
{
    // Seed path: recreate the test data and its indexes, then exit without starting the HTTP server.
    await RequestDataSeeder.RunAsync(app.Services, CancellationToken.None);
    return;
}

await app.Services.GetRequiredService<RequestIndexes>().EnsureIndexesAsync(CancellationToken.None);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
