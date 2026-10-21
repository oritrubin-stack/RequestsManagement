namespace RequestsManagement.Api.Domain;

// Business policy for allowed status transitions (an assumption, not defined by the assignment):
// New -> InProgress; InProgress -> Waiting | Completed; Waiting -> InProgress; Completed is final.
public static class StatusTransitions
{
    private static readonly IReadOnlyDictionary<RequestStatus, RequestStatus[]> AllowedTargets =
        new Dictionary<RequestStatus, RequestStatus[]>
        {
            [RequestStatus.New] = [RequestStatus.InProgress],
            [RequestStatus.InProgress] = [RequestStatus.Waiting, RequestStatus.Completed],
            [RequestStatus.Waiting] = [RequestStatus.InProgress],
            [RequestStatus.Completed] = []
        };

    public static IReadOnlyList<RequestStatus> GetAllowedTargets(RequestStatus from) =>
        AllowedTargets.TryGetValue(from, out var targets) ? targets : [];

    public static bool IsAllowed(RequestStatus from, RequestStatus to) =>
        GetAllowedTargets(from).Contains(to);
}
