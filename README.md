# Requests Management

A full-stack system for managing requests submitted by organizations and employers.
Built as a Full Stack take-home assignment.

> **Status:** work in progress. Sections marked _TBD_ are filled in as the related tasks are completed.
> See the [work plan](docs/work-plan.md) for the task breakdown, order and estimates.

---

## Technologies and versions

| Component | Technology | Version |
|---|---|---|
| Backend | ASP.NET Core Web API | .NET 8 (SDK 8.0.x, see `global.json`) |
| Frontend | Angular | 19 (_TBD_) |
| Database | MongoDB | 4.4+ (developed against 5.0.9, standalone) |
| MongoDB driver | MongoDB.Driver | 3.12.0 |
| Tests | xUnit | _TBD_ |

MongoDB.Driver 3.10–3.12 is fully compatible with MongoDB Server 4.4 through 9.0 and with .NET 8, according to the
official [compatibility tables](https://www.mongodb.com/docs/drivers/compatibility/). MongoDB 4.2 is not supported by this driver version.

## Prerequisites

- .NET 8 SDK (any 8.0.x feature band; `global.json` rolls forward within .NET 8)
- Node.js 22 and Angular CLI 19 (for the client)
- A running MongoDB instance (standalone is sufficient; no replica set is required)

## Getting started

### Configuration

| Setting | Default | Where |
|---|---|---|
| `MongoDb:ConnectionString` | `mongodb://localhost:27017` | `appsettings.Development.json` (no credentials) |
| `MongoDb:DatabaseName` | `RequestsManagement` | `appsettings.json` |

Both can be overridden with environment variables, for example `MongoDb__ConnectionString`.
Outside the Development environment no connection string is provided, and the API fails at startup until one is configured.

### Seed test data

```bash
dotnet run --project RequestsManagement.Api -- --seed
```

- Creates 100,000 requests and exits without starting the HTTP server.
- **Destructive for the `requests` collection only:** it drops that collection, inserts the data in batches of 5,000,
  and then builds the indexes once.
- **Safety checks before any MongoDB call:** the database name must be exactly `RequestsManagement`,
  and every host in the connection string must be local (`localhost`, `127.0.0.1` or `::1`).
  SRV (`mongodb+srv`), `replicaSet` and `loadBalanced` connection strings are rejected,
  because they let the driver connect to servers other than the listed hosts.
  The seeder never drops a database.
- **Repeatable:** a fixed random seed and a fixed base date produce identical content on every run.
  Only the `_id` values differ.
- Measured on the development machine: 5.6–15.2 seconds for 100,000 requests, including index creation.
  The slower runs coincided with high memory pressure on the machine.

### Run the API

```bash
dotnet build RequestsManagement.sln
dotnet run --project RequestsManagement.Api
# Swagger UI: http://localhost:5114/swagger
```

On startup the API ensures that all indexes exist. This is idempotent.

## Solution structure

```
RequestsManagement.Api/
├── Domain/                     # Request document, status/priority enums, status transition policy
├── Infrastructure/
│   ├── Mongo/                  # Options, DI registration, index definitions
│   └── Seeding/                # Deterministic data generator and the seeder
└── Program.cs                  # Composition root; normal startup or the --seed path
```

_TBD_ — API, tests and client projects.

## Database choice and data model

_TBD_ — why MongoDB over SQL Server.

Each request is a single document in the `requests` collection. Its status history is embedded in the same document:

```js
{
  "_id": ObjectId("..."),
  "title": "Issue with payroll report",
  "organizationName": "Oscorp Industries",
  "status": "Completed",
  "priority": 2,
  "assignedTo": "noa.mizrahi",
  "createdAt": ISODate("2025-05-24T10:21:06.088Z"),
  "updatedAt": ISODate("2025-05-31T14:46:43.665Z"),
  "version": 3,
  "statusHistory": [
    { "previousStatus": "New", "newStatus": "InProgress", "changedAt": ISODate("2025-05-27T10:50:46.075Z"), "changedBy": "dana.biton" },
    { "previousStatus": "InProgress", "newStatus": "Completed", "changedAt": ISODate("2025-05-31T14:46:43.665Z"), "changedBy": "maya.peretz" }
  ]
}
```

- **Status** is stored as a string, for readability.
- **Priority** is stored as a number (`Low = 1`, `Medium = 2`, `High = 3`), so sorting follows business order rather than alphabetical order.
- **Embedded history:** a single-document update in MongoDB is atomic. The status change, the `updatedAt` timestamp,
  the `version` increment and the audit entry are written together without a multi-document transaction.
  Transactions would require a replica set.

### Consistency rules

| Rule | Description |
|---|---|
| I1 | A new request has `status = New`, an empty `statusHistory`, `version = 1` and `updatedAt = createdAt` |
| I2 | `version = 1 + statusHistory.length` (status is currently the only mutable field) |
| I3 | When history exists, `status` equals the last entry's `newStatus`, and the first entry starts from `New` |
| I4 | Each entry's `previousStatus` equals the preceding entry's `newStatus`, and every step is an allowed transition |
| I5 | `changedAt` never decreases, starts at or after `createdAt`, and `updatedAt` equals the last `changedAt` |
| I6 | All timestamps are UTC BSON dates (millisecond precision) |

All six rules were verified against the 100,000 seeded documents with zero violations.

## Indexes

Every index that serves sorting ends with `_id`, which is the tie-breaker for stable pagination,
so sorting never requires an in-memory sort stage.

Sizes are measured on 100,000 seeded documents, in MiB (1 MiB = 1,048,576 bytes).

| Index | Keys | Supports | Size (MiB) |
|---|---|---|---|
| `ix_createdAt_id` | `createdAt: -1, _id: -1` | Default sort (newest first), created-date range filter | 2.46 |
| `ix_updatedAt_id` | `updatedAt: -1, _id: -1` | Sort by last update | 2.45 |
| `ix_priority_createdAt_id` | `priority: 1, createdAt: -1, _id: -1` | Filter and sort by priority | 2.48 |
| `ix_status_createdAt_id` | `status: 1, createdAt: -1, _id: -1` | Filter by status (the most common filter) | 2.48 |
| `ix_assignedTo_createdAt_id` | `assignedTo: 1, createdAt: -1, _id: -1` | Filter by assignee | 2.55 |
| `ix_organizationName_createdAt_id` | `organizationName: 1, createdAt: -1, _id: -1` | Exact-match filter by organization | 2.57 |
| `ix_text_title_organizationName` | text on `title`, `organizationName`; `default_language: none` | Free-text search | 4.36 |
| **Total (all indexes)** | | | **19.37** |

**Cost:**
- **Inserts:** each insert writes 8 index entries (7 plus `_id`). The text index costs the most, because it writes one entry per word.
- **Status updates:** a status update touches only the indexes that contain changed fields: `ix_status_createdAt_id` and `ix_updatedAt_id`.
  `version` and `statusHistory` are not indexed.

**Combined filters:** MongoDB uses one index and applies the remaining conditions as a residual filter.
The effectiveness of each index is verified with `explain("executionStats")` in the performance phase.
No additional index is added without a measured justification.

## Querying: pagination, filtering, sorting and search

_TBD_ — how loading the full data set into memory is avoided.

## Status updates and concurrency

_TBD_ — optimistic concurrency with a `version` field and a conditional atomic update.

## Status change history (audit)

_TBD_

## Bulk status update

_TBD_ — failure policy (partial success) and response format.

## Cache and invalidation

_TBD_ — what is cached, expiration, invalidation, and multi-instance considerations.

## Performance

_TBD_ — see `docs/performance.md`.

## Testing

_TBD_

## Technical decisions and alternatives

_TBD_ — at least two significant decisions and the alternatives considered.

## Assumptions

### Allowed status transitions

The assignment defines the four statuses but not the transitions between them. This is the chosen business policy:

| From \ To | New | InProgress | Waiting | Completed |
|---|---|---|---|---|
| **New** | — | ✓ | | |
| **InProgress** | | — | ✓ | ✓ |
| **Waiting** | | ✓ | — | |
| **Completed** | | | | — (final) |

The policy is defined once in `Domain/StatusTransitions.cs`. It is used both by the seeder and by status updates.

_TBD_ — `ChangedBy` source.

## Known limitations and possible improvements

- **Text search matches whole words only.** `$text` does not match partial words (`Acm` does not find `Acme`).
  With `default_language: none` there is no stemming (`request` does not find `requests`).
  In Hebrew, attached prefixes are not separated (`בקשה` does not find `הבקשה`). The seeded data is in English.
- **MongoDB 5.0 is end-of-life.** The driver version is pinned to one that is fully compatible with it.
- **Seeding is restricted to a local server** by design, to prevent accidental data loss.

_TBD_

## Use of AI tools

_TBD_ — which parts were AI-assisted and what was reviewed or changed manually.
