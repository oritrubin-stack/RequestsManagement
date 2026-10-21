using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RequestsManagement.Api.Domain;

// A status change audit entry, embedded in its request document so that the status change
// and its audit entry are written in a single atomic update.
public sealed class StatusChangeEntry
{
    [BsonRepresentation(BsonType.String)]
    public RequestStatus PreviousStatus { get; set; }

    [BsonRepresentation(BsonType.String)]
    public RequestStatus NewStatus { get; set; }

    public DateTime ChangedAt { get; set; }

    public string ChangedBy { get; set; } = string.Empty;
}
