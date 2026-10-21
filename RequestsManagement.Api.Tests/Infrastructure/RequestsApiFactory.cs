using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace RequestsManagement.Api.Tests.Infrastructure;

// Hosts the real API in memory with test-specific MongoDB settings.
public sealed class RequestsApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public RequestsApiFactory(IReadOnlyDictionary<string, string?> settings)
    {
        _settings = settings;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" does not load appsettings.Development.json, so every MongoDB setting comes from the test.
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
    }
}
