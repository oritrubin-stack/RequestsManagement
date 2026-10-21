using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RequestsManagement.Api.Features.Requests;
using RequestsManagement.Api.Infrastructure.ErrorHandling;
using RequestsManagement.Api.Infrastructure.Mongo;
using RequestsManagement.Api.Infrastructure.Seeding;

const string SeedArgument = "--seed";

// The seed flag is removed before configuration is built so it is not parsed as a configuration key.
var seedRequested = args.Contains(SeedArgument);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != SeedArgument).ToArray());

// Add services to the container.
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        // Invalid JSON input is reported as "The input was not valid." under the field path (for example "$.status")
        // instead of the serializer message, which names internal CLR types.
        options.AllowInputFormatterExceptionMessages = false;
    });

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddMongoDb(builder.Configuration);

// Stateless request-scoped services; the MongoDB client and collection they use are singletons.
builder.Services.AddScoped<IRequestRepository, RequestRepository>();
builder.Services.AddScoped<RequestService>();
builder.Services.AddSingleton(TimeProvider.System);

// The summary cache is a singleton: it owns the single-flight lock and the invalidation generation for this instance.
builder.Services.AddMemoryCache();
builder.Services.AddOptions<RequestSummaryCacheOptions>()
    .Bind(builder.Configuration.GetSection(RequestSummaryCacheOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IRequestSummaryCache, RequestSummaryCache>();

var app = builder.Build();

if (seedRequested)
{
    // Seed path: recreate the test data and its indexes, then exit without starting the HTTP server.
    await RequestDataSeeder.RunAsync(app.Services, CancellationToken.None);
    return;
}

if (app.Services.GetRequiredService<IOptions<MongoDbOptions>>().Value.EnsureIndexesOnStartup)
{
    await app.Services.GetRequiredService<RequestIndexes>().EnsureIndexesAsync(CancellationToken.None);
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapControllers();

app.Run();

// Exposes the entry point to WebApplicationFactory in the integration tests.
public partial class Program { }
