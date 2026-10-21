using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RequestsManagement.Api.Tests.Infrastructure;

public static class HttpResponseExtensions
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T> ReadAsAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions))!;
}
