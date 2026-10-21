using System.ComponentModel.DataAnnotations;

namespace RequestsManagement.Api.Features.Requests;

public sealed class RequestSummaryCacheOptions
{
    public const string SectionName = "RequestSummaryCache";

    // Absolute expiration. It bounds staleness for changes this instance does not invalidate:
    // writes made by other API instances, by the seeder, or directly in the database.
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(30);
}
