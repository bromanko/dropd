# Structured Logging Research Notes (Phase 1)

## Phase 1 decision

Phase 1 uses the in-memory `Dropd.Core.ApiContracts.LogEntry` record as the sole structured logging mechanism.
This keeps requirement tests deterministic and avoids introducing an external sink dependency while sync behavior is still being built.

## Why this is sufficient in Phase 1

- Requirement tests assert log codes and key-value data directly.
- `LogEntry` is shared between core and test harness, so behavior under test does not drift.
- No runtime deployment or retention pipeline is required for this phase.

## Phase 4 follow-up criteria for sink selection

When selecting a production sink library in Phase 4, evaluate candidates against these criteria:

1. Supports structured key-value events without losing `Code` and `Data` fields.
2. Can preserve deterministic behavior in tests (or has a test sink abstraction).
3. Supports retention policy configuration needed for DD-076 and DD-077.
4. Supports sink routing for local development and production deployment without changing domain logic.
5. Keeps token/credential fields redacted by default in sink outputs.
