namespace RequestsManagement.Api.Domain;

// Stored as a number so that sorting by priority follows business order (Low < Medium < High)
// rather than alphabetical order.
public enum RequestPriority
{
    Low = 1,
    Medium = 2,
    High = 3
}
