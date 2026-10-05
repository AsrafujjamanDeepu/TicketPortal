# TicketPortal — Remaining Fix Plan for the Next AI Pass

This is the single handoff plan for continuing work after the C1–C4 implementation. It was checked against the current repository on 5 October 2026. Use it with the concept document and click-through guide; do not treat old plans or comments in removed documents as current instructions.

## Project constraints and rules for the next pass

- This is a **personal demo project**, not a live ticket marketplace.
- Do not connect a real card, bank, or mobile-finance payment provider, accept live funds, or add live payment webhooks. Demo payment confirmation remains simulated and protected by the existing production guard.
- Do not seed active statutory tax rules or describe demo tax as VAT due. A tax-liability ledger is future scope until the project has a legally reviewed, service-scoped tax model.
- Preserve the existing uncommitted application changes. Inspect `git status` before editing and do not reset/rebase/clean the user's work.
- Work sequentially in chunks and run the relevant tests after each one. Prefer browser-visible tests where a user interaction exists; use the existing xUnit/LocalDB suite for integrity and failure cases that a click-through cannot reliably prove.
- Before schema changes, inspect the current model, all migrations, and existing data. Add a preflight query and migration/backfill only when needed. Never create a redundant migration.
- Update this plan and the run guide after code changes. Keep only the three project documents in `docs/` plus the root `README.md` entry point.

## Verified completed baseline

- **C1–C4:** implementation complete for the demo scope. The 5 October 2026 verification passed 179 API tests against SQL Server LocalDB, Angular compilation, both admin TypeScript builds, `git diff --check`, and EF's pending-model-change check. Current tests include auth/bootstrap, representative RBAC, booking-create race, refund-cap, and fare-allocation checks.
- **C3 schema:** one booking per non-null `SeatHoldId` is already enforced by the filtered unique index in the existing schema. EF's model represents the index and the create endpoint handles the race. Do not add another migration for it.
- **C5 portions already present:** proportional capped commission reversal on refund; `DhakaClock` for passenger trip-search day ranges; atomic operator-wallet reservation at payout creation; refund/settlement services and ledger integration tests.
- **C6 portions already present:** trip status-transition validation/history in the trip update path; trip cancellation cascade; rate limiting attached to hold create/release; external ERP integration and a mock server; environment-reference secret lookup and masked responses; retry/timeout handling; mock booking records keyed by booking ID.
- **C7 portions already present:** EF migrations and a LocalDB `WebApplicationFactory` test fixture; broad-role architecture guard; demo data reset scripts; an API paged-size cap on some anonymous trip listing paths; baseline fare/booking migrations.

The completion statement above does not mean every row in the plan is closed. Remaining implementation and assurance work is below.

## Decisions still needed

The next AI should inspect current code and present a short recommendation with trade-offs before implementing any decision-dependent financial or ERP behavior. Record the chosen policy here and in source docs before changing it.

| Decision | Current question | Blocks |
|---|---|---|
| **D3 — fixed commission basis** | A fixed rule is currently applied once to a booking-level fare. Keep it per booking or define it per ticket? The current code suggests per booking but the rule must be explicit. | C5-3, finance examples/tests |
| **D6 — rescheduling after sales** | Permit changing departure/arrival time after tickets exist, with an explicit customer-notification workflow, or require cancel/refund and recreate? | C6-1 |
| **D7 — ERP unavailable** | When the operator's availability API is down, fail open or closed? Define whether policy is global or configurable per integration. Current API has an in-process availability cache and timeout but no explicit fail-mode option. | C6-4 |
| **D8 — payout balance release** | Release settlement value into available payout balance when settlement is generated or only when approved? Current payout creation reserves whatever wallet balance exists and does not validate an optional settlement's approval/operator/currency/unpaid remainder. | C5-1 |

D1 (proportional commission reversal), D2 (zero tax default; demo tax only after operator-funded discounts), D4 (short-lived access tokens, uniform auth failures, rate limits, no remote hard account lockout), and D5 (no real gateway) are recorded decisions. Preserve them.

## Chunk 5 — Finance rules, settlement and payout integrity

### C5-1 — Make settlement-linked payouts safe

In `PayoutProcessingService.CreateAsync`, validate every supplied `OperatorSettlementId` before reserving funds:

- the settlement exists, belongs to the requested operator, is Approved (or the explicitly chosen permitted state), and uses the requested currency;
- the requested payout does not exceed the settlement's unpaid remainder or any amount already paid/reserved against it;
- reject reuse of the same settlement remainder through concurrent requests; keep wallet reservation and payout creation atomic;
- enforce D8's chosen point for releasing the payable amount to `AvailablePayoutBalance`;
- return a clear validation response and record an audit event.

Add integration tests for Draft, Rejected, foreign-operator, wrong-currency, over-remainder, repeated, and concurrent payout attempts. Existing controller state changes for payout processing are platform-only; preserve that boundary. Include a backfill/reconciliation note for existing wallets if the chosen D8 change affects balances.

### C5-2 — Use Dhaka-local effective dates consistently

`DhakaClock.DayRangeUtc` is used for trip-search date boundaries, but commission-rule selection in sale and reconciliation code still derives “today” from UTC. Make commission effective-date selection consistent in payment confirmation, counter sale, and reconciliation: derive the Dhaka `DateOnly` once and use inclusive effective-from/effective-to semantics. Test a sale near UTC/Dhaka midnight and the first/last day of an effective period.

### C5-3 — Make commission rules deterministic and non-overlapping

- Keep route-specific rules ahead of operator-wide rules, then apply the documented latest-effective rule tie-break.
- Reject overlapping rules for the same operator, sales channel, route scope, and effective date interval on create/update; return a clear validation error.
- Resolve D3 before changing fixed-amount semantics. Update `FINANCIAL_RULES_AND_EXAMPLES` material (now maintained in the concept or this plan) with executable tests for percentage and fixed commission, online/counter channels, full/partial refunds, and retained cancellation fees.
- Preserve D1: use the posted commission as the source of truth; proportional reversals must be idempotent and capped at the original commission.

### C5-4 — Deferred tax boundary

Do not add active tax defaults. Keep tax-liability ledger work explicitly deferred until someone defines and validates a jurisdiction-, service-, operator-, and date-scoped tax model. If future scope changes, require legal review and tests before any rate is seeded.

### C5 acceptance checks

- Payout creation refuses ineligible/foreign/unapproved settlement references, currency mismatch, excessive amounts, and duplicate/concurrent reservation.
- Both online and counter commission lookup use Dhaka business dates and a deterministic rule order.
- Overlapping commission rules fail with a clear error; selected rule and computed amount match D3.
- Finance tests and the full API test suite pass; UI settlement/payout workflows still display the resulting states correctly.

## Chunk 6 — Inventory limits, trip editing, and ERP hardening

### C6-1 — Define and enforce trip rescheduling after sales (D6)

The current trip update path validates legal status transitions and writes status history; do not replace that working behavior. Inspect how it reconciles physical/held/booked seats. Record D6, then enforce the selected rule when a trip has bookings/tickets. If rescheduling is allowed, require a deliberate staff action, record old/new times, and make customer notification visible/trackable. If blocked, provide a clear conflict response and route staff to cancel/refund. Add API tests and a browser walkthrough.

### C6-2 — Prevent seat-hold hoarding

There is a `holds` rate-limit policy attached to hold creation/release, but no configured `MaxSeatsPerHold` or `MaxActiveHoldsPerUser` policy was found. Add validated settings, count a user's unexpired active holds atomically, reject duplicate seat IDs, and enforce a maximum seat count before inventory changes. Ensure hold expiry/release still cleans up seats, bookings, and coupon usage. Add parallel integration tests for limits and ensure no partial hold is created on a rejected request.

### C6-3 — Harden outbound ERP requests and secret references

Current code masks returned secret text and resolves `env:` references through configuration, but accepts arbitrary environment-key names and still permits literal values. Current HTTP requests do not show an explicit SSRF/redirect guard. Implement:

- permit references only within a dedicated configuration prefix (for example `Integrations:Secrets:*`), reject literals, and redact values from logs and error text;
- validate BaseUrl at save and call time: HTTPS outside Development, reject loopback/private/link-local destinations after DNS resolution, and prevent redirects to an unvalidated host;
- make the HTTP handler refuse automatic redirects and retain per-call timeouts;
- add tests for invalid schemes, local/private IPs, DNS-to-private addresses, redirects, missing secret values, and log redaction.

Do not break the Development mock ERP configuration; migrate it to the selected permitted reference format.

### C6-4 — Decide availability behavior (D7)

Make ERP availability failure mode explicit (open or closed, global or per integration). Expose it as validated configuration, surface the behavior in the operator integration UI, and alert/log when an external-inventory operator has no working integration. Test reachable, timeout, error, stale-cache, and seat-unavailable cases. Never silently claim a seat is confirmed when the external system rejected it.

### C6-5 — ERP idempotency and UI scenario controls

`mock-erp` already stores one record per `bookingId`, so its confirm operation is idempotent in memory. The .NET request does not currently send an `Idempotency-Key` header. Decide whether booking ID in the JSON body is sufficient for the contract or send the header as well; test repeated requests and restart behavior clearly. Add a small browser-accessible scenario selector/reset page to the mock ERP (success, pending, unavailable, timeout/error) so the failure paths requested in the manual test guide can be tested with clicks rather than direct JSON calls. Keep its interface local-development-only.

### C6 acceptance checks

- D6 is enforced when trip schedules have sales; user-visible transition and cancellation behavior is documented and tested.
- Hold count/seat caps reject safely with no inventory or booking side effects.
- ERP secrets cannot be returned or leaked; unsafe destinations/redirects are rejected.
- D7 behavior is predictable, shown to the operator, and covered by failure tests.
- Same booking repeated to mock ERP yields one booking; scenario failures are controllable from its local browser UI.

## Chunk 7 — Database integrity, test automation, and release quality

### C7-1 — Enforce one active ticket per seat

`AppDbContext` currently has a filtered index on `Ticket.TripSeatId` for non-cancelled/non-refunded records, but the model declaration does not mark it unique. Run a duplicate preflight against a disposable copy and clean duplicates deliberately before adding uniqueness. Add an EF migration whose filter is based on the enum values and soft-delete policy. Test the database rejects two active tickets for one seat while allowing the intended cancelled/refunded history. Verify `Down` and empty-database migration.

### C7-2 — Gateway transaction identifier

The current project has no real gateway by D5. Do not invent live provider behavior. Decide whether demo transaction identifiers need uniqueness now; if not, record this item as deferred until a real provider is authorized. If uniqueness is implemented, use provider/gateway plus transaction ID, a filtered unique index, a preflight query, and an idempotent conflict response.

### C7-3 — Page large list endpoints

The current API caps some trip listings, but Bookings and Tickets list actions return unpaged collections. Add a shared capped page response with backward-compatible defaults, total count, sort stability, and page-size limits; update the Angular/React list pages to use the controls. Verify page 1/page 2 have no duplicate/missing rows, owner/operator filters apply before paging, and high values are capped.

### C7-4 — CI and repeatable SQL testing

The current xUnit fixture uses LocalDB and was run successfully on Windows, but no repository CI workflow or SQL Server container setup was found. Add a CI workflow that restores/builds the .NET API and runs all tests on a Windows runner with LocalDB or an appropriately supported SQL Server service; run Angular/admin type checks and builds. Add migration-from-empty validation and upload test output as a build artifact. Document local commands only in the run guide (do not add another setup document).

### C7-5 — Startup option validation and migration governance

Several Production guards exist, but a central `ValidateOnStart` options check was not found. Review JWT, bootstrap admin, private storage, SMTP, and ERP settings; validate required values, ranges, URL safety, and cross-setting requirements before serving requests. Do not require a gateway secret under D5. Ensure CI applies migrations to a clean DB and produces an idempotent SQL script. Require explicit review for migration/backfill/rollback.

### C7-6 — Manual and automated regression coverage

- Keep `docs/01-Run-and-Manual-Test-Guide.md` in sync with visible UI buttons and real flows.
- Add a browser automation smoke path for search → hold → demo payment → ticket → cancellation/refund → settlement/payout. It must not call real payment services.
- Add targeted failure-path coverage for payment confirmation retry/recovery, trip-cancellation cascade, External ERP availability policy, and C5 payout/commission decisions.
- Preserve the C2 static guard and extend denied-case coverage as sensitive routes change. C2's every-route-by-persona review remains a known bounded gap, so report what is actually exercised.
- Add concurrency/load checks for same-seat holds and active-ticket uniqueness before any production claim.

### C7-7 — Cleanup and release checks

- The old `permission-matrix-init-order.patch` is a leftover file. Confirm its change is present in `PermissionMatrix.cs`, then remove the patch artifact.
- Confirm the repository contains only the three maintained project documents under `docs/`; keep the root README as a short entry-point linking to them.
- Correct code comments/config comments to link to the surviving documents, not deleted files.
- Verify a clean release build, test suite, migration script, and browser checklist; summarize residual host-only requirements. No production deploy is implied.

### C7 acceptance checks

- SQL rejects duplicate active tickets for a seat; migrations apply cleanly from empty DB.
- Bookings/Tickets paging works, has stable ordering and capped input, and UI navigation is usable.
- CI restores/builds/tests and validates migrations for a clean database.
- Production options fail fast on invalid settings without introducing gateway/tax configuration requirements for the personal demo.
- The click-through guide can be completed in a fresh seeded Development database without Swagger or Postman.

## Recommended sequence for the next AI

1. Re-open this plan and inspect current `git status`; confirm C1–C4 still pass and do not overwrite local modifications.
2. Finish C5 decisions and finance safeguards (D3/D8 first, then tests).
3. Finish C6 decisions and safe inventory/ERP controls (D6/D7 first, then tests and scenario UI).
4. Finish C7 schema, pagination, CI, migration checks, and regression automation.
5. Run the full UI checklist and all CI-equivalent checks. Update this document and the run guide with verified results only.
