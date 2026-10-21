using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Tests.Unit;

public sealed class StatusTransitionsTests
{
    [Theory]
    [InlineData(RequestStatus.New, RequestStatus.InProgress)]
    [InlineData(RequestStatus.InProgress, RequestStatus.Waiting)]
    [InlineData(RequestStatus.InProgress, RequestStatus.Completed)]
    [InlineData(RequestStatus.Waiting, RequestStatus.InProgress)]
    public void IsAllowed_ForPolicyTransition_ReturnsTrue(RequestStatus from, RequestStatus to)
    {
        Assert.True(StatusTransitions.IsAllowed(from, to));
    }

    [Theory]
    [InlineData(RequestStatus.New, RequestStatus.New)]
    [InlineData(RequestStatus.New, RequestStatus.Waiting)]
    [InlineData(RequestStatus.New, RequestStatus.Completed)]
    [InlineData(RequestStatus.InProgress, RequestStatus.New)]
    [InlineData(RequestStatus.Waiting, RequestStatus.Completed)]
    [InlineData(RequestStatus.Completed, RequestStatus.InProgress)]
    [InlineData(RequestStatus.Completed, RequestStatus.New)]
    public void IsAllowed_ForTransitionOutsidePolicy_ReturnsFalse(RequestStatus from, RequestStatus to)
    {
        Assert.False(StatusTransitions.IsAllowed(from, to));
    }

    [Fact]
    public void Completed_IsFinal()
    {
        Assert.Empty(StatusTransitions.GetAllowedTargets(RequestStatus.Completed));
    }
}
