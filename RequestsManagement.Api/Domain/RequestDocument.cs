using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RequestsManagement.Api.Domain;

// Consistency rules maintained for every request:
// - A new request has Status = New, an empty StatusHistory, Version = 1 and UpdatedAt = CreatedAt.
// - Version = 1 + StatusHistory.Count (status is currently the only mutable field).
// - When history exists, Status equals the last entry's NewStatus, the first entry starts from New,
//   and each entry's PreviousStatus equals the preceding entry's NewStatus via an allowed transition.
// - ChangedAt values never decrease, start at or after CreatedAt, and UpdatedAt equals the last ChangedAt.
// - All timestamps are UTC with millisecond precision (the precision of a BSON date).
public sealed class RequestDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string OrganizationName { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public RequestStatus Status { get; set; }

    public RequestPriority Priority { get; set; }

    public string AssignedTo { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Optimistic concurrency token: every successful update must match the current value and increment it.
    public int Version { get; set; }

    public List<StatusChangeEntry> StatusHistory { get; set; } = [];
}
