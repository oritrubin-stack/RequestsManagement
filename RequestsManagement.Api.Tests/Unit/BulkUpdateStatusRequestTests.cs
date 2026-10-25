using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;
using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Features.Requests.Dtos;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class BulkUpdateStatusRequestTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(BulkUpdateStatusRequest.MaxItems)]
    public void ItemCountWithinLimit_IsValid(int count)
    {
        var (isValid, _) = Validate(CreateRequest(count));

        Assert.True(isValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BulkUpdateStatusRequest.MaxItems + 1)]
    public void ItemCountOutsideLimit_IsInvalid(int count)
    {
        var (isValid, errors) = Validate(CreateRequest(count));

        Assert.False(isValid);
        Assert.Contains(errors, error => error.MemberNames.Contains(nameof(BulkUpdateStatusRequest.Items)));
    }

    [Fact]
    public void MissingItems_IsInvalid()
    {
        var (isValid, _) = Validate(new BulkUpdateStatusRequest());

        Assert.False(isValid);
    }

    [Fact]
    public void InvalidId_IsReportedOnTheItem()
    {
        var items = CreateRequest(3).Items!;
        items[1] = Item("not-an-id");

        var (isValid, errors) = Validate(new BulkUpdateStatusRequest { Items = items });

        Assert.False(isValid);
        Assert.Contains(errors, error => error.MemberNames.Contains("Items[1].Id"));
    }

    [Fact]
    public void DuplicateIds_AreRejected_RegardlessOfLetterCase()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var request = new BulkUpdateStatusRequest
        {
            Items = [Item(id.ToLowerInvariant()), Item(ObjectId.GenerateNewId().ToString()), Item(id.ToUpperInvariant())]
        };

        var (isValid, errors) = Validate(request);

        Assert.False(isValid);
        var duplicate = Assert.Single(errors, error => error.MemberNames.Contains(nameof(BulkUpdateStatusRequest.Items)));
        Assert.Contains(id, duplicate.ErrorMessage);
    }

    private static (bool IsValid, List<ValidationResult> Errors) Validate(BulkUpdateStatusRequest request)
    {
        var errors = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(request, new ValidationContext(request), errors, validateAllProperties: true);
        return (isValid, errors);
    }

    private static BulkUpdateStatusRequest CreateRequest(int count) =>
        new() { Items = Enumerable.Range(0, count).Select(_ => Item(ObjectId.GenerateNewId().ToString())).ToList() };

    private static BulkUpdateStatusItem Item(string id) =>
        new() { Id = id, Status = RequestStatus.InProgress, ExpectedVersion = 1 };
}
