# Prefill protocol v2

Protocol v2 allows independent runs in one daemon. Steam, Epic, and Xbox reuse
one persistent account session; Battle.net and Riot remain anonymous. Items stay
sequential inside a run, while separate runs may transfer concurrently.

## Configuration

| Setting | Default | Range | Applied |
| --- | ---: | ---: | --- |
| `PREFILL_MAX_RUNS` | `4` | `1..16` | Daemon restart |
| `PREFILL_MAX_REQUESTS` | Per service | `1..128` | Daemon restart |

Request defaults are 30 for Steam, Epic, and Xbox, 25 for Battle.net, and 20 for
Riot. The request ceiling is process-wide and independent of the run limit.
Per-run `maxConcurrency` can lower, but not replace, that ceiling. Steam's
explicit `--max-threads` value remains authoritative.

## Wire contract

Framing remains a four-byte little-endian JSON length with a 10 MB maximum.
Command parameters remain strings; `appIds` is a JSON array encoded as a string.

Status enables v2 only when it contains all of the following:

| Field | Required value |
| --- | --- |
| `protocolVersion` | `2` |
| `features` | `concurrentPrefill`, `operationProgress`, `targetedCancel`, `inlineSelection`, `activeOperations` |
| `daemonInstanceId` | Valid current boot ID |
| `maxConcurrentRuns` / `maxConcurrentRequests` | Valid advertised limits |
| `activeOperations` / `recentOperations` | Bounded run summaries |

Starts send `protocolVersion="2"` and the probed `daemonInstanceId`. The
request ID is the operation ID. An accepted response includes `state="started"`,
`runId`, and `daemonInstanceId`. Equal duplicate input replays the retained
operation; changed input returns `operation-conflict`. An explicit `[]`
selection remains empty.

Progress and terminal messages include `operationId`, `daemonInstanceId`, and a
strictly increasing per-run `sequence`. Terminal states are `completed`,
`failed`, and `cancelled`. Overlap is `skippedOverlap`, never cached.
`cancel-prefill` targets one operation; `cancelling` is an acknowledgement, not
the terminal result. `get-operation` provides bounded recovery pages. Terminal
runs are retained for 24 hours, up to 256 operations and 10,000 item snapshots.

## Compatibility and release

Missing, malformed, or partial v2 capability data selects legacy exclusive mode
before work starts. Active v2 work never downgrades mid-run. Old and new
managers and daemons remain compatible because each service negotiates
independently.

For an authorized release: publish common first, update every daemon gitlink to
that commit, release daemon images, then release the manager. Drain active v2
runs before downgrade. The conservative fallback is
`PREFILL_MAX_RUNS=1` followed by restart; keep additive manager history columns.
Local publish artifacts are not releases, and no registry or deployment action
is implied.
