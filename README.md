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
| `MongoDb:EnsureIndexesOnStartup` | `true` | When `false`, the API starts without contacting MongoDB (the indexes must already exist) |
| `RequestSummaryCache:TimeToLive` | `00:00:30` | `appsettings.json`; expiration of the cached summary (1 second to 1 hour) |

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
├── Features/
│   └── Requests/               # Controller (HTTP), service (mapping, business rules), repository (MongoDB queries), DTOs
├── Infrastructure/
│   ├── ErrorHandling/          # Global exception handler (ProblemDetails)
│   ├── Mongo/                  # Options, DI registration, index definitions
│   └── Seeding/                # Deterministic data generator and the seeder
└── Program.cs                  # Composition root; normal startup or the --seed path

RequestsManagement.Api.Tests/
├── Infrastructure/             # WebApplicationFactory, MongoDB test database fixture
├── Integration/                # HTTP tests against the real API and MongoDB
└── Unit/                       # Domain rules, query parameter validation, data generator
```

_TBD_ — client project.

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

### `GET /api/requests`

| Parameter | Type | Default | Rules |
|---|---|---|---|
| `search` | string | — | Up to 100 characters; MongoDB `$text` search on `title` and `organizationName` |
| `status` | `New` \| `InProgress` \| `Waiting` \| `Completed` | — | Single value |
| `priority` | `Low` \| `Medium` \| `High` | — | Single value |
| `organizationName` | string | — | Exact, **case-sensitive** match; up to 200 characters |
| `assignedTo` | string | — | Exact, case-sensitive match; up to 200 characters |
| `createdFrom` | date-time | — | Inclusive lower bound of `createdAt` |
| `createdTo` | date-time | — | Exclusive upper bound of `createdAt`; must be later than `createdFrom` |
| `sortBy` | `createdAt` \| `updatedAt` \| `priority` | `createdAt` | |
| `sortDirection` | `asc` \| `desc` | `desc` | |
| `page` | int | 1 | 1 – 1,000,000 |
| `pageSize` | int | 20 | 1 – 100 |

- **Dates:** the date range is half-open, `[createdFrom, createdTo)`, and all dates are UTC. A value without an offset
  (for example `2025-01-01`) is interpreted as UTC.
- **Sorting by priority:** see [Sorting by priority](#sorting-by-priority) below.
- **Response:** `{ items, page, pageSize, totalCount, totalPages }`.
  Items are `RequestDto` objects without the status history, and enums are returned as strings.

### Sorting by priority

| `sortDirection` | Order |
|---|---|
| `desc` | `High` first. Within the same priority, the **oldest** request comes first |
| `asc` | `Low` first. Within the same priority, the **newest** request comes first |

**This behavior is intentional.** The reasons:

- **One index serves both directions.** The index `ix_priority_createdAt_id` is `(priority 1, createdAt -1, _id -1)`.
  MongoDB can read an index forwards or backwards, but only with every key reversed together.
  A descending priority sort can therefore use the index only if `createdAt` becomes ascending.
- **Measured cost of the alternative.** Sorting by `priority` descending with `createdAt` also descending does not match the
  index. It was measured as a full collection scan with an in-memory sort: 100,000 documents examined, 333 ms.
  The index-aligned order examines 20 keys and 20 documents, 18 ms.
- **It matches a work queue.** In the most common use, "most urgent first", the high-priority request that has waited longest
  is shown first.

**Alternatives considered:**
- **Reverse `createdAt` in the index** (`priority 1, createdAt 1, _id 1`). This only moves the asymmetry:
  a descending sort would show the newest `High` first, and an ascending sort the oldest `Low` first.
- **Add a second index** so that both directions sort newest first. This adds write and storage cost to every insert
  and status update, without a measured need.

### `GET /api/requests/{id}`

Returns `200` with a `RequestDto`, `404` if the request does not exist, or `400` if the id is not a valid ObjectId.

### Avoiding loading the data set into memory

- **Filtering, `$text` search, sorting, skip/limit and counting run in MongoDB.** The application receives one page only.
- **A projection excludes `statusHistory`,** so list queries transfer only the fields the DTO needs.
- **The page items and the total count are queried in parallel.** Without filters, the count uses the collection metadata
  (`estimatedDocumentCount`) instead of scanning, because an unfiltered `countDocuments` was measured at about 200 ms.

### Errors

All errors are returned as ProblemDetails (`application/problem+json`) with a `traceId`:

| Status | When |
|---|---|
| `400` | Invalid query parameters or id (`ValidationProblemDetails` with an error per field) |
| `404` | The request does not exist |
| `503` | MongoDB is unreachable (server selection timeout or connection failure) |
| `500` | Any other unexpected error; details are logged, not returned |

If the client disconnects, the request is cancelled through the `CancellationToken` and logged at Information level.

In .NET 8, ASP.NET Core's `ExceptionHandlerMiddleware` also logs every unhandled exception at Error level before the
global handler runs. A database outage therefore appears twice in the server log (Error and Warning). Clients are not affected.
Logs include the names of active filters, result counts and timings. Search text and filter values are not logged.

### Indexes and query plans

Query plans for the main query shapes, measured with `explain("executionStats")` on 100,000 documents with a page size of 20:

| Query | Plan | In-memory sort | Keys / docs examined | ms |
|---|---|---|---|---|
| No filter, `createdAt` desc or asc | `IXSCAN ix_createdAt_id` | No | 20 / 20 | 0–29 |
| `status` (+ date range), `createdAt` desc | `IXSCAN ix_status_createdAt_id` | No | 20 / 20 | 4–31 |
| `status`, sorted by `updatedAt` | `IXSCAN ix_updatedAt_id`, status filtered while scanning | No | 87 / 87 | 21 |
| Sorted by `priority` (index-aligned directions) | `IXSCAN ix_priority_createdAt_id` | No | 20 / 20 | 18 |
| Sorted by `priority` with **mismatched** secondary directions | `COLLSCAN` + `SORT` | **Yes** | 0 / 100,000 | 333 |
| `status`, sorted by `priority` | `IXSCAN ix_priority_createdAt_id` | No | 91 / 91 | 2 |
| `organizationName` or `assignedTo` (+ `status`) | `IXSCAN` on the matching index | No | 20–69 | 23–28 |
| `$text` (+ `status`), any sort | `TEXT` + `SORT` | **Yes** | 4,922–6,719 | 46–121 |
| `organizationName` + `priority`, sorted by `updatedAt` | `IXSCAN ix_organizationName_createdAt_id` + `SORT` | Yes (478 docs) | 478 / 478 | 11 |
| Skip 50,000 | `IXSCAN` + `SKIP` | No | 50,020 / 20 | 139 |

**Conclusions:**
- **Sorting by priority:** the secondary sort keys follow `ix_priority_createdAt_id` or its exact reverse. A mismatched direction
  turns the query into a full collection scan.
- **`$text` cannot use another index for sorting,** so matching documents are sorted in memory. The cost grows with the number
  of matches. At 100,000 documents, even the whole collection (about 40 MB) fits within MongoDB's 100 MB in-memory sort limit.
- **A filter combined with a different sort field** uses either the filter index or the sort index; the planner chooses.
  This is acceptable for the measured shapes, and is re-checked in the performance phase.
- **Deep pages are expensive:** skip scans every skipped key (a skip beyond the last page scanned the whole index, about 320 ms).
  Keyset pagination is the documented improvement.

## Status updates and concurrency

### `PUT /api/requests/{id}/status`

```http
PUT /api/requests/6ad881e1a051e7edd89d0ef0/status
X-User-Name: dana.cohen
Content-Type: application/json

{ "status": "InProgress", "expectedVersion": 3 }
```

| Field | Rules |
|---|---|
| `status` | Required; one of `New`, `InProgress`, `Waiting`, `Completed` |
| `expectedVersion` | Required; at least 1. The `version` the client last read |
| `X-User-Name` header | Required; 1–100 characters from letters, digits and `. _ @ -`. Recorded as `changedBy` in the history |

| Status | When | Body |
|---|---|---|
| `200` | The status was changed | The updated `RequestDto`, with the new `version` and `updatedAt` |
| `400` | Invalid id, body or header; a transition the policy does not allow, or the current status | `ValidationProblemDetails` (for a transition, under `errors.Status`) |
| `404` | The request does not exist | `ProblemDetails` |
| `409` | `expectedVersion` is not the current version | `ProblemDetails` with `currentVersion` and `currentStatus` |
| `503` | MongoDB is unreachable | `ProblemDetails` |

**Why `expectedVersion` is in the body and not an `If-Match` header:** a failed `If-Match` precondition is
`412 Precondition Failed` by HTTP semantics, while the assignment requires `409 Conflict` for a concurrent update.

**Why a disallowed transition is `400` and not `409`:** clients treat `409` as "reload and retry".
A disallowed transition does not go away after a reload, so it is reported as a validation error.

### How a lost update is prevented

1. **Read** the request. If it does not exist, return `404`.
2. **Check the version.** If `expectedVersion` differs from the stored `version`, return `409`. This comes before
   the transition check, because a transition judged against a state the client has not seen is meaningless.
3. **Check the transition** against the policy. If it is not allowed, return `400`.
4. **Conditional atomic update** (`findOneAndUpdate`):

   ```js
   filter: { _id: id, version: expectedVersion }
   update: { $set:  { status: newStatus, updatedAt: now },
             $inc:  { version: 1 },
             $push: { statusHistory: { previousStatus, newStatus, changedAt: now, changedBy } } }
   ```

5. **If nothing matched,** another update won between steps 1 and 4. The request is read again:
   it returns `404` if the request is gone, and `409` with the current version otherwise.

**Correctness rests on step 4, not on the read.** A single-document update is atomic in MongoDB, and the version
condition is evaluated inside it. Of two updates based on the same version, only the first can match, and the
second receives `409`. The status, `updatedAt`, `version` and the history entry are written in the same operation,
so the consistency rules above always hold, and no transaction (or replica set) is needed.
`previousStatus` comes from the read in step 1. It is accurate whenever the update matches, because any status
change would also have changed the version.

**Timestamps:** `updatedAt` and `changedAt` are the same value, truncated to milliseconds (the precision MongoDB stores).
The value is never earlier than the previous `updatedAt`, so the history stays in order even if server clocks differ.

**Cache:** the summary cache is invalidated after the update succeeds, and when its outcome is unknown (see below).
It is not invalidated for `400`, `404` or `409`, which are definite failures.

**Measured in the tests:** 10 concurrent requests with the same `expectedVersion` produce exactly one `200` and
nine `409` responses; the version increases by one and one history entry is added. In the recorded runs, 2–3 of the 9
conflicts were rejected by the atomic condition itself (after passing the early version check) and the rest by the
early check; the split varies between runs.

### When the outcome of an update is unknown

Once the update command has been sent, MongoDB applies it even if the client stops waiting: the driver closes the
connection on cancellation or a network failure, but it does not stop the command on the server. Therefore:

- **If the client disconnects** while the update is in flight, the request is cancelled (`499`, logged only).
- **If the reply is lost** (timeout or connection failure), the API returns `503`. **The update may or may not have
  been applied.** The response does not claim either outcome.
- **The summary cache is invalidated in both cases,** because the counts may have changed. This costs at most one
  extra computation. If the server applies the update only after the API stopped waiting, a summary computed in
  between can still be cached with the previous counts until the TTL expires (30 seconds).
- **The API does not retry.** A client may reload the request: if `version` increased, the update was applied.
  A client may also resend the same update with the same `expectedVersion`, which is safe: it either succeeds once
  or returns `409` if the first attempt was applied.

## Status change history (audit)

### `GET /api/requests/{id}/history`

```json
[
  { "requestId": "6ad881e1a051e7edd89d0ef0", "previousStatus": "New", "newStatus": "InProgress",
    "changedAt": "2025-05-27T10:50:46.075Z", "changedBy": "dana.biton" },
  { "requestId": "6ad881e1a051e7edd89d0ef0", "previousStatus": "InProgress", "newStatus": "Completed",
    "changedAt": "2025-05-31T14:46:43.665Z", "changedBy": "maya.peretz" }
]
```

- **Order:** chronological, oldest first, as stored.
- **Responses:** an empty array for a request without status changes, `404` for an unknown request, and `400` for an invalid id.
- **Storage:** the history is embedded in the request document and written in the same update as the status change,
  so a change can never be saved without its audit entry. `requestId` is taken from the parent document.
- **No paging:** the number of entries is bounded by the number of status changes of a single request.

## Bulk status update

_TBD_ — failure policy (partial success) and response format.

## Summary aggregations

### `GET /api/requests/summary`

Counts across all requests (no filters), grouped by status and by priority:

```json
{
  "totalCount": 100000,
  "byStatus": [
    { "status": "New", "count": 19897 },
    { "status": "InProgress", "count": 30134 },
    { "status": "Waiting", "count": 14933 },
    { "status": "Completed", "count": 35036 }
  ],
  "byPriority": [
    { "priority": "Low", "count": 40169 },
    { "priority": "Medium", "count": 39746 },
    { "priority": "High", "count": 20085 }
  ],
  "generatedAt": "2026-10-21T11:15:02.123Z"
}
```

- **Always the same shape.** Every status and priority is present, in enum order, with zero when it has no requests.
  An empty collection returns all zeros.
- **`totalCount` is the sum of `byStatus`.**
- **`generatedAt` is when the computation started.** The counts were read after that moment, so the data is
  at least that fresh. A cached response keeps the original `generatedAt`, which shows how old it is.

### How it is computed

A single aggregation pipeline runs in MongoDB:

```js
[{ $facet: {
    byStatus:   [{ $group: { _id: "$status",   count: { $sum: 1 } } }],
    byPriority: [{ $group: { _id: "$priority", count: { $sum: 1 } } }]
}}]
```

- **Both groupings come from the same collection scan** in one `$facet` stage.
- **It is not a point-in-time snapshot.** Writes made while the scan runs may or may not be reflected, so under
  concurrent writes the counts are approximate for that moment. MongoDB snapshot reads require a replica set,
  and this project runs on a standalone server.
- **No index helps a full grouping.** Measured on 100,000 documents:
  - `$facet` is a collection scan, with a median of about 0.67 s.
  - An index-covered `$sort` + `$group` scanned 100,000 index keys and was slower (about 0.96 s for status alone).
  - Seven `countDocuments` calls were faster (about 0.37 s), but they are seven separate reads, so their totals can disagree under concurrent writes.
- **Timings vary with machine load.** The cost of a full scan is why the summary is cached.

## Cache and invalidation

| Aspect | Behavior |
|---|---|
| **What is cached** | Only the summary (`RequestSummaryDto`), under one key. It is the most expensive read (a full collection scan), dashboards request it on every load, and short staleness is acceptable for summary counts |
| **Where** | `IMemoryCache` (built into .NET), wrapped by `RequestSummaryCache`. No external cache or extra package |
| **Expiration** | Absolute, 30 seconds by default (`RequestSummaryCache:TimeToLive`, from 1 second to 1 hour) |
| **Invalidation** | `IRequestSummaryCache.Invalidate()`, called by the service **after** a write that affects the counts has succeeded. A successful status update calls it, and so does an update whose outcome is unknown (cancelled or failed after being sent); definite failures (`400`, `404`, `409`) do not. Bulk updates (F5) will call it once when at least one item was updated |
| **Concurrent requests** | Single-flight: on a miss, one request computes and the others wait for the cached result. Measured: 20 concurrent requests on an empty cache produced one computation |
| **Invalidation during a computation** | A generation counter is checked together with storing the result (under one lock). If an invalidation happened while the summary was being computed, the result is returned to its caller but not cached |
| **Computation failure** | The exception propagates (for example as `503`), nothing is cached, and the next request retries |
| **Cancellation** | A request cancelled while *waiting* only stops waiting; the running computation continues for the others. If the *computing* request is cancelled, its query stops and the next waiting request computes instead |

**Why the TTL is still needed when writes invalidate.** Invalidation only reaches this instance's cache.
The TTL bounds staleness for changes this instance never sees: writes handled by other API instances,
the seeder (a separate process), and direct database edits. It also limits the impact of a write path that fails to invalidate.

**Invalidation cannot fail like a remote call,** because removing an in-memory entry involves no I/O.
If the process stops between a write and its invalidation, the cache is lost with the process.

### Limitation: multiple API instances

**The cache is not synchronized between instances.** Each instance keeps its own copy, and `Invalidate()` clears only
the instance that handled the write. Another instance can return a summary that is stale by up to the TTL (30 seconds).
Sticky sessions do not solve this, because the write and the read can reach different instances.

Ways to synchronize when running several instances:
- **A shared cache** (for example Redis through `IDistributedCache`). This adds a server and a package.
  Invalidation becomes a network call that can fail, so the TTL is still required.
- **Local caches plus an invalidation message** (for example Redis pub/sub) broadcast to all instances after a write.

## Performance

_TBD_ — see `docs/performance.md`.

## Testing

```bash
dotnet test RequestsManagement.sln
```

The tests require a local MongoDB server on `localhost:27017`.

| Package | Version |
|---|---|
| xunit | 2.9.3 |
| xunit.runner.visualstudio | 3.1.5 |
| Microsoft.NET.Test.Sdk | 18.11.0 |
| Microsoft.AspNetCore.Mvc.Testing | 8.0.32 |

### Integration tests (real API and real MongoDB)

The tests host the real API in memory with `WebApplicationFactory`, so nothing in the request pipeline is replaced.
Expected results are computed in memory from the same 500 seeded documents and compared with the HTTP responses.

| Test class | Covers |
|---|---|
| `RequestsQueryTests` | Default sort; combined filters; half-open UTC date range; sorting by priority and by `updatedAt`; all pages contain every result exactly once; a page beyond the end; `$text` whole-word search (and no partial-word match); exact, case-sensitive organization filter; get by id (`200`, `404`) |
| `RequestsValidationTests` | `400` with a field error for every invalid query parameter, too-long search text and malformed ids |
| `RequestsSummaryTests` | Summary counts match the seeded documents; the summary is served from the cache until invalidated (a temporary document is inserted into the test database and deleted in `finally`); empty and missing collections return no counts |
| `RequestsHistoryTests` | History matches the seeded entries in order, with `requestId`; an empty array for a `New` request; `404` and `400` |
| `RequestsStatusUpdateTests` | A successful update changes status, `version`, `updatedAt` and adds a history entry in the database; a sequence of transitions is recorded in order; disallowed transitions, the current status and a final status return `400`; a stale version returns `409` with the current state; `404`; body, header and id validation; **10 concurrent updates with the same version: exactly one succeeds**; the conditional update matches only the expected version; the summary cache is invalidated after a successful update and not after failed ones; validation messages name the `X-User-Name` header and do not expose internal type names. Every rejected update of an existing request is checked to leave the document unchanged |
| `StartupValidationTests` | The API refuses to start with an invalid `RequestSummaryCache:TimeToLive` (zero, negative, above one hour, not a time span) |
| `DatabaseUnavailableTests` | `503` ProblemDetails when MongoDB is unreachable (list, get by id, summary, history, status update). This uses the real failure path: the driver's server selection times out on an unused local port, and the global handler maps the exception to 503 |

**Read and write tests use separate databases.** The read-only tests compare responses with the seeded documents,
so the tests that change data (`RequestsStatusUpdateTests`) run in their own test collection, which creates a
separate test database. Each write test creates the requests it changes.

**Test database safety.** Each run uses a new database named `RequestsManagement_Tests_<guid>` on the local server.
The database is dropped at the end only when all of the following hold:
- the name has this prefix;
- the server is local (the same check the seeder uses);
- the name did not exist before the run;
- the marker document written by this run is still present.

Otherwise the database is left in place and a message is written.

### Unit tests

| Test class | Covers |
|---|---|
| `StatusTransitionsTests` | Allowed and disallowed transitions; `Completed` is final |
| `RequestQueryParametersTests` | Defaults; UTC normalization of dates without an offset and of local dates; range comparison after normalization |
| `RequestDataGeneratorTests` | Determinism for a given seed; generated documents satisfy the consistency rules |
| `RequestSummaryCacheTests` | Reuse of the cached value; still cached just before the TTL and recomputed just after it; `Invalidate()`; a failed computation is not cached; 20 calls that are all waiting before the first computation finishes compute once; an invalidation during a computation prevents caching its result; a cancelled waiter does not cancel the computation; a cancelled computation is not cached and the next waiter computes |
| `RequestServiceSummaryTests` | Missing statuses and priorities are filled with zero; `totalCount` is the sum of statuses; an empty result returns all zeros |
| `RequestServiceStatusUpdateTests` | Each outcome of a status update with a fake repository: success (history entry fields, one invalidation), not found, stale version, disallowed transitions, **an update that loses the race between the read and the write** (`409` with the latest version), a request removed in that window (`404`), timestamps that never go backwards and are truncated to milliseconds. Definite failures neither write nor invalidate; a write that is cancelled or fails with an unknown outcome propagates the exception and invalidates the summary |

**TTL tests do not wait.** They use a real `MemoryCache` with a manual clock (`MemoryCacheOptions.Clock`)
that the test moves forward, so expiration is deterministic.

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

### `ChangedBy` source

The assignment does not include authentication, so the user who made a change is sent by the client in the
`X-User-Name` header. **The value is not verified;** it records what the client declares. With authentication,
the value would come from the authenticated user's claims instead, without changing the rest of the update flow.
The value is stored in the history but not written to the logs.

## Known limitations and possible improvements

- **The `organizationName` filter is an exact, case-sensitive match** (`acme logistics` does not find `Acme Logistics`).
  Use `search` to find organizations by word. A case-insensitive filter would require a collation index.
- **Search terms are combined with OR** (`invoice payroll` matches either word), following MongoDB `$text` semantics.
  Quoted phrases and `-term` exclusions are also passed through to `$text`.
- **Text search matches whole words only.** `$text` does not match partial words (`Acm` does not find `Acme`).
  With `default_language: none` there is no stemming (`request` does not find `requests`).
  In Hebrew, attached prefixes are not separated (`בקשה` does not find `הבקשה`). The seeded data is in English.
- **MongoDB 5.0 is end-of-life.** The driver version is pinned to one that is fully compatible with it.
- **Seeding is restricted to a local server** by design, to prevent accidental data loss.
- **`changedBy` is not authenticated.** It records the `X-User-Name` header sent by the client.
- **A `503` on a status update has an unknown outcome.** The update may have been applied; clients should reload the request.

_TBD_

## Use of AI tools

_TBD_ — which parts were AI-assisted and what was reviewed or changed manually.
