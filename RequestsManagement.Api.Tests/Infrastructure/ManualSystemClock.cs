using Microsoft.Extensions.Internal;

namespace RequestsManagement.Api.Tests.Infrastructure;

// A clock the test moves explicitly, so MemoryCache expiration is deterministic without waiting.
public sealed class ManualSystemClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
}
