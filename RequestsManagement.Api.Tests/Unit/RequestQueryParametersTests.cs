using System.ComponentModel.DataAnnotations;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class RequestQueryParametersTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        var parameters = new RequestQueryParameters();

        Assert.True(IsValid(parameters));
        Assert.Equal(1, parameters.Page);
        Assert.Equal(RequestQueryParameters.DefaultPageSize, parameters.PageSize);
        Assert.Equal(RequestSortField.CreatedAt, parameters.SortBy);
        Assert.Equal(SortDirection.Desc, parameters.SortDirection);
    }

    [Fact]
    public void DateWithoutOffset_IsTreatedAsUtc()
    {
        var unspecified = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Unspecified);

        var parameters = new RequestQueryParameters { CreatedFrom = unspecified };

        Assert.Equal(DateTimeKind.Utc, parameters.CreatedFromUtc!.Value.Kind);
        Assert.Equal(unspecified.Ticks, parameters.CreatedFromUtc.Value.Ticks);
    }

    [Fact]
    public void LocalDate_IsConvertedToUtc()
    {
        var local = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Local);

        var parameters = new RequestQueryParameters { CreatedTo = local };

        Assert.Equal(local.ToUniversalTime(), parameters.CreatedToUtc);
        Assert.Equal(DateTimeKind.Utc, parameters.CreatedToUtc!.Value.Kind);
    }

    [Fact]
    public void DateRange_IsComparedAfterUtcNormalization()
    {
        // The same instant expressed as local time and as UTC must be treated as an empty (invalid) range.
        var utc = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        var parameters = new RequestQueryParameters { CreatedFrom = utc.ToLocalTime(), CreatedTo = utc };

        Assert.False(IsValid(parameters));
    }

    private static bool IsValid(RequestQueryParameters parameters) =>
        Validator.TryValidateObject(parameters, new ValidationContext(parameters), [], validateAllProperties: true);
}
