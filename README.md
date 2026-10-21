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
| Tests | xUnit | _TBD_ |

## Prerequisites

- .NET 8 SDK (any 8.0.x feature band; `global.json` rolls forward within .NET 8)
- Node.js 22 and Angular CLI 19 (for the client)
- A running MongoDB instance (standalone is sufficient; no replica set is required)

## Getting started

_TBD_ — connection configuration, seeding test data, running the API, the client and the tests.

```bash
dotnet build RequestsManagement.sln
dotnet run --project RequestsManagement.Api
```

## Solution structure

_TBD_

## Database choice and data model

_TBD_ — why MongoDB, the request document, and the embedded status history.

## Indexes

_TBD_ — each index and the query scenario it supports.

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

_TBD_ — points not defined by the assignment (allowed status transitions, `ChangedBy` source, search semantics).

## Known limitations and possible improvements

_TBD_

## Use of AI tools

_TBD_ — which parts were AI-assisted and what was reviewed or changed manually.
