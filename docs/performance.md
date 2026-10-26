# Performance

How the main API operations perform on 100,000 requests, what the query plans show, which indexes support them,
and where the bottlenecks are.

Every number in this document is **measured**, unless it is explicitly marked **estimate**.
Measurements are read-only: no index was dropped and no data was changed to produce them.

## Summary

| Operation (100,000 requests) | Measured result |
|---|---|
| `GET /api/requests`, first page, default sort | p50 **7.7 ms**, p95 18.1 ms (HTTP) |
| `GET /api/requests` with a status and date-range filter | p50 **10.4 ms**, p95 19.8 ms (HTTP) |
| `GET /api/requests` with a text search | p50 **85.7 ms**, p95 112.2 ms (HTTP) |
| `GET /api/requests` at page 2,500 (skip 49,980) | p50 **188.0 ms**, p95 235.3 ms (HTTP), the main bottleneck |
| `GET /api/requests/summary`, served from the cache | p50 **1.1 ms**, p95 1.5 ms (HTTP) |
| `GET /api/requests/summary`, computed (cache expired) | p50 **670.4 ms**, p95 715.7 ms (HTTP) |
| Same queries forced to a collection scan | 261–321 ms instead of 0–1 ms (database execution time) |

## Environment and method

| Item | Value |
|---|---|
| Date | 2026-10-25 (earlier measurements are dated where they are quoted) |
| Machine | Intel Core i7-1165G7 (8 logical CPUs), 16 GB RAM, Windows 11 Pro |
| Free memory | **HTTP measurements: 676–733 MB** (measured before and after them). **Before the `explain` run: 1,326 MB.** The machine was under memory pressure, which adds noise (see limitations) |
| Database | MongoDB 5.0.9, standalone, local; the seeded `RequestsManagement` database (100,000 requests) |
| API | .NET 8.0.408, **Release** build, `Production` environment, `MongoDb:EnsureIndexesOnStartup=false` (so the measurement does not even issue the no-op index creation) |
| HTTP measurement | [`tools/perf/measure-api.ps1`](../tools/perf/measure-api.ps1): `HttpClient` on the same machine; latency includes reading the whole response body. **5 warm-up requests, then 30 measured requests** per scenario; p50, p95, min and max |
| Cold summary | The API was started with `RequestSummaryCache:TimeToLive=00:00:01`, and the script waited 1.5 s between requests, so each request computed the summary. 1 warm-up, then **10 measured requests** |
| Query plans | [`tools/perf/explain-queries.js`](../tools/perf/explain-queries.js): `explain("executionStats")`, **5 runs per row**, median `executionTimeMillis` |

**Limitations of the measurement:**
- **One machine, no network:** the client, the API and MongoDB ran on the same machine, so there is no network
  latency. With a remote database, each round trip adds its latency.
- **Single client:** requests were sent one at a time. This measures latency, not throughput under concurrent load.
- **Memory pressure:** free memory was low (676–733 MB during the HTTP measurements). Absolute times vary between
  runs: for the same indexed queries, the `explain` run on 2026-10-21 reported 0–31 ms (the first queries cold),
  and this run reported 0–1 ms. The comparisons (keys examined, documents examined, plan shape) do not vary.
- **Rounding:** `executionTimeMillis` has a resolution of 1 ms, so `0` means "less than 1 ms".

## 1. API latency (HTTP)

Run on 2026-10-25T11:15Z, 30 requests per scenario after 5 warm-up requests:

| Scenario | Requests | p50 ms | p95 ms | min ms | max ms |
|---|---|---|---|---|---|
| A1 default page (`createdAt` desc) | 30 | 7.7 | 18.1 | 5.4 | 24.1 |
| A2 filter: `status=Waiting` + `createdAt` in [2025-01-01, 2025-07-01) | 30 | 10.4 | 19.8 | 7.8 | 21.3 |
| A3 text search: `search=invoice` | 30 | 85.7 | 112.2 | 68.1 | 116.2 |
| A4 sort by priority desc | 30 | 8.7 | 42.9 | 5.5 | 107.4 |
| A5 deep page: `page=2500` (skip 49,980) | 30 | 188.0 | 235.3 | 111.1 | 254.7 |
| S-warm summary (served from the cache) | 30 | 1.1 | 1.5 | 0.7 | 27.4 |
| S-cold summary (computed on every request) | 10 | 670.4 | 715.7 | 564.0 | 715.7 |

**Reading the results:**
- **A1, A2 and A4 are served from an index** in sort order: A1 and A2 in sections 2 and 5, and the priority sort (A4)
  in the [README table](../README.md#indexes-and-query-plans). Each runs two database operations in
  parallel: the page query and the total count.
- **A3 (text search)** is slower because `$text` cannot be combined with a sort index: the matching documents are
  sorted in memory, and the total count also examines every match (section 3).
- **A5 (deep page)** is the main bottleneck: `skip` walks every skipped index key (section 5).
- **The summary** costs about 0.67 s when computed, and about 1 ms from the cache (section 4).

## 2. Index vs forced collection scan

To show what the indexes save without removing them, each query was explained twice: with its normal winning plan,
and with `hint({$natural: 1})`, which **forces a collection scan while the indexes still exist**. This is a forced
scan for comparison, not a measurement of the collection with its indexes dropped.

Run on 2026-10-25T11:14Z, page size 20, median of 5 runs:

| Query | Plan | In-memory sort | Keys examined | Docs examined | Returned | ms |
|---|---|---|---|---|---|---|
| E1 default page, winning plan | `LIMIT ← PROJECTION ← FETCH ← IXSCAN(ix_createdAt_id)` | no | 20 | 20 | 20 | 0 |
| E1 default page, forced collection scan | `PROJECTION ← SORT ← COLLSCAN` | **yes** | 0 | **100,000** | 20 | **321** |
| E2 status + date range, winning plan | `LIMIT ← PROJECTION ← FETCH ← IXSCAN(ix_status_createdAt_id)` | no | 20 | 20 | 20 | 1 |
| E2 status + date range, forced collection scan | `PROJECTION ← SORT ← COLLSCAN` | **yes** | 0 | **100,000** | 20 | **261** |

With the index, MongoDB reads exactly the 20 entries of the page, already in the requested order.
Without it, it must read all 100,000 documents and sort them in memory to return the same 20.

The total count for E2 (3,733 matches) took 7 ms, using the same index.

Query plans for 14 other query shapes (filters by organization and assignee, sorting by `updatedAt` and `priority`,
including a misaligned sort direction that falls back to a collection scan) were measured on 2026-10-21 and are
listed in the [README](../README.md#indexes-and-query-plans).

## 3. Text search

| Query (page size 20) | Plan | In-memory sort | Keys examined | Docs examined | ms |
|---|---|---|---|---|---|
| E3 `$text: "invoice"` (text index) | `SORT ← TEXT_MATCH ← FETCH ← IXSCAN(ix_text_title_organizationName)` | **yes** | 6,719 | 6,719 | 66 |
| E3 case-insensitive regex `/\binvoice\b/i` on title or organization | `SUBPLAN ← LIMIT ← FETCH ← IXSCAN(ix_createdAt_id)` | no | 241 | 241 | 458 |

| Total count (as run by the list endpoint) | Matches | ms |
|---|---|---|
| `$text: "invoice"` | 6,719 | 61 |
| Regex `/\binvoice\b/i` | 6,719 | **450** |

- **The text index finds the matches directly** (6,719 keys for 6,719 matches), but it cannot provide the sort order,
  so the matches are sorted in memory. The cost grows with the number of matches.
- **A regex cannot use the text index.** For one page, the planner walked the sort index and tested each document
  until it found 20 matches (241 documents). The total count, however, has to test every document: 450 ms,
  compared with 61 ms with `$text`. Since the list endpoint always returns the total count, the regex alternative
  is about 7 times slower overall.
- The API measurement A3 (p50 85.7 ms) reflects the `$text` cost: the page and the count run in parallel.

## 4. Summary aggregation

The summary groups all requests by status and by priority in one `$facet` stage. No index can reduce a full
grouping: measured on 2026-10-21, the `$facet` pipeline is a collection scan of 100,000 documents (median 0.67 s),
an index-covered grouping was slower (about 0.96 s for status alone), and seven `countDocuments` calls took about
0.37 s but are seven separate reads (details in the [README](../README.md#how-it-is-computed)).

Measured over HTTP on 2026-10-25:

| Summary | p50 ms | p95 ms |
|---|---|---|
| Computed (cache expired before each request) | 670.4 | 715.7 |
| Served from the cache | 1.1 | 1.5 |

The cache (30-second absolute expiration, invalidated after successful writes) turns a 0.67 s scan into a 1 ms read
for every request but one per expiration period. Measured on 2026-10-21: 20 concurrent requests on an empty cache
produced one computation (single-flight).

## 5. Pagination: skip vs keyset

Run on 2026-10-25T11:14Z, sorted by `createdAt` desc and `_id` desc, page size 20, median of 5 runs:

| Position | `skip` plan | Keys examined (skip) | ms (skip) | Keys examined (keyset) | ms (keyset) |
|---|---|---|---|---|---|
| 0 | `IXSCAN(ix_createdAt_id)` | 20 | 0 | — | — |
| 1,000 | `SKIP ← IXSCAN` | 1,020 | 2 | 20 | 0 |
| 10,000 | `SKIP ← IXSCAN` | 10,020 | 30 | 20 | 1 |
| 50,000 | `SKIP ← IXSCAN` | 50,020 | 152 | 20 | 1 |
| 99,980 (last page) | `SKIP ← IXSCAN` | 100,000 | 281 | 20 | 0 |

- **With `skip`, the cost grows linearly with the page number:** MongoDB walks every skipped index key.
  The API measurement A5 (page 2,500) confirms it: p50 188 ms, compared with 7.7 ms for the first page.
- **Keyset pagination** continues after the last item of the previous page, using the same index:
  `{ $or: [ { createdAt: { $lt: lastCreatedAt } }, { createdAt: lastCreatedAt, _id: { $lt: lastId } } ] }`.
  Its plan merges two index scans that are already sorted (`SORT_MERGE`, not an in-memory sort) and examines
  20 keys at every position.

## 6. Indexes and storage

The indexes, and the query each one serves, are described in the [README](../README.md#indexes).
Every index that serves sorting ends with `_id`, so pagination is stable and sorting never needs an in-memory stage.
Sections 2, 3 and 5 confirm that the default sort, the status filter with a date range, and text search use their
intended indexes.

Storage, measured on 2026-10-25 (`db.requests.stats()`):

| Item | MiB |
|---|---|
| Data (uncompressed BSON) | 39.38 |
| Collection on disk (compressed) | 11.00 |
| `_id_` | 0.95 |
| `ix_createdAt_id` | 2.46 |
| `ix_updatedAt_id` | 2.45 |
| `ix_priority_createdAt_id` | 2.48 |
| `ix_status_createdAt_id` | 2.48 |
| `ix_assignedTo_createdAt_id` | 2.55 |
| `ix_organizationName_createdAt_id` | 2.57 |
| `ix_text_title_organizationName` | 4.36 |
| **All indexes** | **20.32** |

The indexes take almost twice the space of the compressed collection. That is the cost of covering each filter and
sort without an in-memory sort. Each insert writes one entry in each of the 6 single-key indexes and `_id`,
plus one entry per word of `title` and `organizationName` in the text index, so the number of text index entries
varies with the text. A status update changes only
`ix_status_createdAt_id` and `ix_updatedAt_id`.

## 7. Bottlenecks and proposed improvements

### B1. Deep pagination (main bottleneck)

- **Measured:** 152 ms of database time and 50,020 keys examined at position 50,000; 281 ms at the last page.
  Over HTTP, page 2,500 has a p50 of 188 ms, compared with 7.7 ms for the first page.
- **Cause:** `skip` cannot jump to a position in an index; it walks every earlier entry.
- **Proposed improvement: keyset (cursor) pagination.** The response would return an opaque cursor built from the
  `createdAt` and `_id` of the last item, and the next request would pass it back instead of a page number.
  The query above examined 20 keys at every position (measured with `explain`; the API itself has not been changed).
- **Trade-off:** keyset pagination supports "next" and "previous", not jumping to an arbitrary page number. A practical
  combination is page numbers for the first pages and a cursor beyond them, or a cap on the page number.
- **Expected effect (estimate):** deep pages would cost about as much as the first page (A1, p50 7.7 ms), because the
  database work is the same 20-key index scan. This was not measured through the API.

### B2. Text search sorts its matches in memory

- **Measured:** 6,719 matches for "invoice"; 66 ms for the page and 61 ms for the count (database), p50 85.7 ms over HTTP.
- **Cause:** `$text` uses the text index to find matches, but cannot use a second index for the sort order.
- **Proposed improvement:** for this data size the cost is acceptable. For larger collections, a search engine
  (for example Atlas Search) can return matches already sorted and supports partial words. A cheaper option is to
  require a minimum term length or another filter together with search, which reduces the number of matches to sort.

### B3. Summary computation scans the whole collection

- **Measured:** p50 670 ms when computed; 1.1 ms from the cache.
- **Already mitigated:** the cache serves almost every request.
- **Possible further improvement:** keep the counts in a separate document and update them in each status change.
  This would make every summary request cheap, but on a standalone server the counter update cannot be in the same
  atomic operation as the request update, so the counts could drift and would need periodic reconciliation.
  For a summary that tolerates 30 seconds of staleness, the cache is the simpler choice.

### B4. Unfiltered total count (already fixed)

- **Measured on 2026-10-21:** `countDocuments({})` took 196 ms.
- **Fix in the code:** without filters, the list endpoint uses `estimatedDocumentCount`, which reads collection
  metadata. The first page (A1) now has a p50 of 7.7 ms over HTTP, including the count.

### Bulk status update (not measured on 100,000 requests)

- **Measured in the integration tests** on the test database (about 500 seeded requests plus the ones each test
  creates), **not on the 100,000-request database:** a batch of 100 items took about 100 ms, and about 230 ms
  as the first request after startup.
- The batch makes one `$in` read and up to 100 conditional updates by `_id`. Lookups by `_id` use the primary key
  index, so collection size has little effect on each operation (**estimate**, not measured on 100,000 requests).
- With a remote database, the batch time grows by about one network round trip per item (**estimate**).
- It was not measured on the main database, because that would modify it.

## How to reproduce

The scripts only read: `explain-queries.js` runs `find`, `countDocuments`, `explain` and `stats`,
and `measure-api.ps1` sends `GET` requests only.

```bash
# Query plans. Tested with the legacy mongo shell 5.0.9 (the command used for the results above):
mongo --quiet tools/perf/explain-queries.js

# Expected to work with mongosh as well, but not tested:
mongosh --quiet tools/perf/explain-queries.js
```

```powershell
# Build and start the API in Release mode against the seeded database, without index creation at startup
dotnet build RequestsManagement.Api -c Release
$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:ASPNETCORE_URLS = "http://localhost:5130"
$env:MongoDb__ConnectionString = "mongodb://localhost:27017"
$env:MongoDb__EnsureIndexesOnStartup = "false"
.\RequestsManagement.Api\bin\Release\net8.0\RequestsManagement.Api.exe

# In another terminal: list scenarios and the cached summary
.\tools\perf\measure-api.ps1 -BaseUrl http://localhost:5130 -Iterations 30 -Warmup 5

# Cold summary: restart the API with $env:RequestSummaryCache__TimeToLive = "00:00:01", then
.\tools\perf\measure-api.ps1 -BaseUrl http://localhost:5130 -Scenario SummaryCold -Iterations 10 -Warmup 1 -DelayMs 1500
```
