## What changed

<!-- One or two sentences. Link the fix-plan item (e.g. C7-3) if there is one. -->

## How it was checked

- [ ] `dotnet test apps/api.tests` passes (LocalDB, see docs/01-Run-and-Manual-Test-Guide.md)
- [ ] Angular and admin type-check / build pass
- [ ] Clicked through the affected screens (no Swagger/Postman needed)

## Database migration review

Delete this section only if the PR does **not** touch `apps/api/Migrations/`. CI fails a PR that changes a
migration unless all three boxes below are ticked.

- [ ] Migration reviewed: I read the generated `Up` **and** `Down` SQL (the CI artifact `ticketportal-idempotent.sql` has it) and the snapshot diff contains only the intended change
- [ ] Backfill / preflight: existing rows were considered; anything that could fail on real data has a preflight query, and the migration refuses to run (instead of deleting or guessing) when data would break it
- [ ] Rollback plan: `Down` was exercised (CI does `database update <previous>` then back) and I wrote down what cannot be undone, if anything
