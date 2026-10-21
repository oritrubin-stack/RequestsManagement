using Microsoft.Extensions.Options;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Infrastructure.Seeding;

namespace RequestsManagement.Api.Infrastructure.Mongo;

public static class MongoServiceCollectionExtensions
{
    public const string RequestsCollectionName = "requests";

    private static int _conventionsRegistered;

    public static IServiceCollection AddMongoDb(this IServiceCollection services, IConfiguration configuration)
    {
        RegisterConventions();

        services.AddOptions<MongoDbOptions>()
            .Bind(configuration.GetSection(MongoDbOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // MongoClient is thread-safe and owns the connection pool, so a single instance is shared.
        services.AddSingleton<IMongoClient>(sp =>
            new MongoClient(sp.GetRequiredService<IOptions<MongoDbOptions>>().Value.ConnectionString));

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MongoDbOptions>>().Value;
            return sp.GetRequiredService<IMongoClient>()
                .GetDatabase(options.DatabaseName)
                .GetCollection<RequestDocument>(RequestsCollectionName);
        });

        services.AddSingleton<RequestIndexes>();
        services.AddTransient<RequestDataSeeder>();

        return services;
    }

    // Conventions are global to the BSON serializer, so they are registered once and limited to our domain types.
    private static void RegisterConventions()
    {
        if (Interlocked.Exchange(ref _conventionsRegistered, 1) == 1)
        {
            return;
        }

        var conventions = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true)
        };

        ConventionRegistry.Register(
            "RequestsManagementConventions",
            conventions,
            type => type.Namespace == typeof(RequestDocument).Namespace);
    }
}
