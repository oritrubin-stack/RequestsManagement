// Read-only performance analysis of the requests collection (no writes, no index changes).
// Uses only find, countDocuments, explain and stats.
//
// Tested with the legacy mongo shell 5.0.9:
//   mongo --quiet tools/perf/explain-queries.js
// Expected to work with mongosh as well, but not tested:
//   mongosh --quiet tools/perf/explain-queries.js
// Optional: --eval "var databaseName = 'OtherDatabase'" before the file name.

const dbName = typeof databaseName !== "undefined" ? databaseName : "RequestsManagement";
const coll = db.getSiblingDB(dbName).getCollection("requests");
const runs = 5;
const projection = { statusHistory: 0 };
const byCreatedDesc = { createdAt: -1, _id: -1 };

function stages(plan) {
  const out = [];
  (function walk(p) {
    if (!p) return;
    out.push(p.stage + (p.indexName ? "(" + p.indexName + ")" : ""));
    walk(p.inputStage);
    (p.inputStages || []).forEach(walk);
  })(plan);
  return out.join(" <- ");
}

function median(values) {
  const sorted = values.slice().sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

// Runs explain("executionStats") several times and reports the plan with the median execution time.
function explainFind(label, filter, sort, options) {
  options = options || {};
  const times = [];
  let last;
  for (let i = 0; i < runs; i++) {
    let cursor = coll.find(filter, projection).sort(sort);
    if (options.hint) cursor = cursor.hint(options.hint);
    if (options.skip) cursor = cursor.skip(options.skip);
    last = cursor.limit(options.limit || 20).explain("executionStats");
    times.push(last.executionStats.executionTimeMillis);
  }
  const s = last.executionStats;
  const plan = stages(last.queryPlanner.winningPlan);
  // Only a SORT stage is a blocking in-memory sort; SORT_MERGE merges index scans that are already sorted.
  print("| " + label + " | `" + plan + "` | " + (/(^|<- )SORT($| <-)/.test(plan) ? "yes" : "no") + " | " +
        s.totalKeysExamined + " | " + s.totalDocsExamined + " | " + s.nReturned + " | " + median(times) + " |");
}

function timeCount(label, filter) {
  const times = [];
  let count;
  for (let i = 0; i < runs; i++) {
    const start = Date.now();
    count = coll.countDocuments(filter);
    times.push(Date.now() - start);
  }
  print("| " + label + " | " + count + " | " + median(times) + " |");
}

print("# Read-only performance analysis");
print("Database: " + dbName + ", documents: " + coll.countDocuments({}) + ", server: " + db.version() +
      ", run at: " + new Date().toISOString() + ", explain runs per row: " + runs + " (median ms)");

const statusFilter = { status: "Waiting", createdAt: { $gte: ISODate("2025-01-01T00:00:00Z"), $lt: ISODate("2025-07-01T00:00:00Z") } };

print("");
// hint({$natural: 1}) forces a collection scan for comparison; the indexes still exist and are not changed.
print("## E1-E2 Winning plan vs forced collection scan (hint {$natural: 1}), page size 20");
print("| Query | Plan | In-memory sort | Keys examined | Docs examined | Returned | ms (median) |");
print("|---|---|---|---|---|---|---|");
explainFind("E1 default page, winning plan", {}, byCreatedDesc);
explainFind("E1 default page, forced collection scan", {}, byCreatedDesc, { hint: { $natural: 1 } });
explainFind("E2 status + date range, winning plan", statusFilter, byCreatedDesc);
explainFind("E2 status + date range, forced collection scan", statusFilter, byCreatedDesc, { hint: { $natural: 1 } });

print("");
// A case-insensitive regex cannot use the text index; for one page the planner may walk the sort index and filter,
// but the total count that the list endpoint also runs must examine every document.
const regexFilter = { $or: [{ title: /\binvoice\b/i }, { organizationName: /\binvoice\b/i }] };

print("## E3 Text search: $text (text index) vs case-insensitive regex (cannot use the text index), page size 20");
print("| Query | Plan | In-memory sort | Keys examined | Docs examined | Returned | ms (median) |");
print("|---|---|---|---|---|---|---|");
explainFind("E3 $text 'invoice'", { $text: { $search: "invoice" } }, byCreatedDesc);
explainFind("E3 regex /\\binvoice\\b/i on title or organization", regexFilter, byCreatedDesc);

print("");
print("## Counts used by the list endpoint (countDocuments with the same filter)");
print("| Filter | Count | ms (median) |");
print("|---|---|---|");
timeCount("status + date range (E2)", statusFilter);
timeCount("$text 'invoice' (E3)", { $text: { $search: "invoice" } });
timeCount("regex /\\binvoice\\b/i (E3 alternative)", regexFilter);

print("");
print("## E4 Deep pagination: skip vs keyset (createdAt desc, _id desc), page size 20");
print("| Query | Plan | In-memory sort | Keys examined | Docs examined | Returned | ms (median) |");
print("|---|---|---|---|---|---|---|");
[0, 1000, 10000, 50000, 99980].forEach(position => {
  explainFind("skip " + position, {}, byCreatedDesc, { skip: position });
  if (position === 0) return;
  // Keyset: continue after the last item of the previous page instead of skipping.
  const last = coll.find({}, { createdAt: 1 }).sort(byCreatedDesc).skip(position - 1).limit(1).toArray()[0];
  const keyset = { $or: [{ createdAt: { $lt: last.createdAt } }, { createdAt: last.createdAt, _id: { $lt: last._id } }] };
  explainFind("keyset after position " + position, keyset, byCreatedDesc);
});

print("");
print("## E5 Storage");
const stats = coll.stats();
print("| Item | Bytes | MiB |");
print("|---|---|---|");
const mib = bytes => (bytes / 1048576).toFixed(2);
print("| Data (uncompressed BSON) | " + stats.size + " | " + mib(stats.size) + " |");
print("| Collection storage (compressed, on disk) | " + stats.storageSize + " | " + mib(stats.storageSize) + " |");
print("| All indexes | " + stats.totalIndexSize + " | " + mib(stats.totalIndexSize) + " |");
Object.keys(stats.indexSizes).forEach(name => print("| Index " + name + " | " + stats.indexSizes[name] + " | " + mib(stats.indexSizes[name]) + " |"));
