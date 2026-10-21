using RequestsManagement.Api.Domain;
using RequestsManagement.Api.Infrastructure.Seeding;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class RequestDataGeneratorTests
{
    private const int Count = 2_000;

    [Fact]
    public void Generate_WithSameSeed_ProducesIdenticalContent()
    {
        var first = new RequestDataGenerator(seed: 7).Generate(Count).Select(Describe).ToList();
        var second = new RequestDataGenerator(seed: 7).Generate(Count).Select(Describe).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Generate_ProducesDocumentsThatSatisfyConsistencyRules()
    {
        foreach (var request in new RequestDataGenerator(seed: 7).Generate(Count))
        {
            var history = request.StatusHistory;

            Assert.Equal(1 + history.Count, request.Version);
            Assert.Equal(request.Status == RequestStatus.New, history.Count == 0);
            Assert.Equal(DateTimeKind.Utc, request.CreatedAt.Kind);

            var previousStatus = RequestStatus.New;
            var previousTime = request.CreatedAt;
            foreach (var entry in history)
            {
                Assert.Equal(previousStatus, entry.PreviousStatus);
                Assert.True(StatusTransitions.IsAllowed(entry.PreviousStatus, entry.NewStatus));
                Assert.True(entry.ChangedAt >= previousTime);
                previousStatus = entry.NewStatus;
                previousTime = entry.ChangedAt;
            }

            Assert.Equal(previousStatus, request.Status);
            Assert.Equal(previousTime, request.UpdatedAt);
        }
    }

    private static string Describe(RequestDocument request) =>
        $"{request.Title}|{request.OrganizationName}|{request.Status}|{request.Priority}|{request.AssignedTo}|" +
        $"{request.CreatedAt.Ticks}|{request.UpdatedAt.Ticks}|{request.Version}";
}
