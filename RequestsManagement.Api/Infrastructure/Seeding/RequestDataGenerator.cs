using RequestsManagement.Api.Domain;

namespace RequestsManagement.Api.Infrastructure.Seeding;

// Generates deterministic test requests in memory. It performs no I/O, so it is also safe to use in tests.
// The same seed always produces the same content; only the ObjectId values differ between runs,
// because they are assigned by the driver at insert time.
public sealed class RequestDataGenerator
{
    public const int DefaultSeed = 20261001;

    // A fixed base date (instead of DateTime.UtcNow) keeps generated data identical between runs.
    private static readonly DateTime BaseDate = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long CreatedAtSpanMilliseconds = (long)TimeSpan.FromDays(730).TotalMilliseconds;
    private static readonly long MinChangeGapMilliseconds = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
    private static readonly long MaxChangeGapMilliseconds = (long)TimeSpan.FromDays(10).TotalMilliseconds;

    private static readonly string[] OrganizationPrefixes =
    [
        "Acme", "Globex", "Initech", "Umbrella", "Stark", "Wayne", "Hooli", "Vandelay", "Soylent", "Cyberdyne",
        "Tyrell", "Wonka", "Gringotts", "Oscorp", "Aperture", "Massive", "Pied Piper", "Dunder", "Monarch", "Nakatomi"
    ];

    private static readonly string[] OrganizationSuffixes =
    [
        "Industries", "Logistics", "Healthcare", "Systems", "Foods", "Consulting", "Labs", "Group", "Partners", "Holdings"
    ];

    private static readonly string[] AssigneeFirstNames = ["dana", "yossi", "noa", "avi", "maya", "eli"];

    private static readonly string[] AssigneeLastNames = ["cohen", "levi", "mizrahi", "peretz", "biton"];

    private static readonly string[] TitlePrefixes =
    [
        "Request for", "Issue with", "Question about", "Update to", "Problem with", "Inquiry regarding",
        "Change in", "Approval for"
    ];

    private static readonly string[] TitleSubjects =
    [
        "payroll report", "employee onboarding", "invoice", "contract renewal", "access permissions",
        "tax documents", "benefits enrollment", "pension contribution", "insurance claim", "work permit",
        "training budget", "overtime approval", "vacation balance", "salary slip", "health declaration"
    ];

    // Weighted by repetition: Low 40%, Medium 40%, High 20%.
    private static readonly RequestPriority[] PriorityWeights =
    [
        RequestPriority.Low, RequestPriority.Low, RequestPriority.Medium, RequestPriority.Medium, RequestPriority.High
    ];

    // Number of status changes, weighted by repetition, so that the status distribution is not uniform.
    private static readonly int[] StatusChangeCountWeights = [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 5, 5];

    private static readonly string[] Organizations =
        OrganizationPrefixes.SelectMany(prefix => OrganizationSuffixes.Select(suffix => $"{prefix} {suffix}")).ToArray();

    private static readonly string[] Assignees =
        AssigneeFirstNames.SelectMany(first => AssigneeLastNames.Select(last => $"{first}.{last}")).ToArray();

    private readonly Random _random;

    public RequestDataGenerator(int seed = DefaultSeed)
    {
        _random = new Random(seed);
    }

    public IEnumerable<RequestDocument> Generate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (var i = 0; i < count; i++)
        {
            yield return CreateRequest();
        }
    }

    private RequestDocument CreateRequest()
    {
        var createdAt = BaseDate.AddTicks(-NextLong(0, CreatedAtSpanMilliseconds) * TimeSpan.TicksPerMillisecond);

        var request = new RequestDocument
        {
            Title = $"{Pick(TitlePrefixes)} {Pick(TitleSubjects)}",
            OrganizationName = Pick(Organizations),
            Priority = Pick(PriorityWeights),
            AssignedTo = Pick(Assignees),
            Status = RequestStatus.New,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Version = 1
        };

        AddStatusHistory(request, Pick(StatusChangeCountWeights));

        return request;
    }

    // Walks the allowed transitions from New, so every generated history follows the status policy.
    private void AddStatusHistory(RequestDocument request, int changeCount)
    {
        var changedAt = request.CreatedAt;

        for (var i = 0; i < changeCount; i++)
        {
            var targets = StatusTransitions.GetAllowedTargets(request.Status);
            if (targets.Count == 0)
            {
                break;
            }

            var gap = NextLong(MinChangeGapMilliseconds, MaxChangeGapMilliseconds);
            changedAt = Min(changedAt.AddTicks(gap * TimeSpan.TicksPerMillisecond), BaseDate);

            var newStatus = targets[_random.Next(targets.Count)];
            request.StatusHistory.Add(new StatusChangeEntry
            {
                PreviousStatus = request.Status,
                NewStatus = newStatus,
                ChangedAt = changedAt,
                ChangedBy = Pick(Assignees)
            });

            request.Status = newStatus;
            request.UpdatedAt = changedAt;
            request.Version++;
        }
    }

    private T Pick<T>(IReadOnlyList<T> values) => values[_random.Next(values.Count)];

    private long NextLong(long minInclusive, long maxExclusive) => _random.NextInt64(minInclusive, maxExclusive);

    private static DateTime Min(DateTime first, DateTime second) => first <= second ? first : second;
}
