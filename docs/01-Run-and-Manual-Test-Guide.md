---
title: "TicketPortal — Run & End-to-End Test Guide"
subtitle: "Setup, single-command startup, and a detailed browser click-through test script"
date: "October 2026"
---

# 1. What this document covers

This guide has two parts:

1. **How to run the project** — prerequisites, first-time setup, and starting the
   Angular customer/staff portal, the React admin console, and the ASP.NET Core API
   together with a single `npx nx` command.
2. **A complete end-to-end test script, driven entirely by real buttons in the
   browser** — every role, every screen, full Create/Read/Update/Delete, from
   anonymous search through booking, ticket verification, cancellation/refund, and
   every staff/operator/finance/admin screen, using the repo's own seeded demo
   accounts.

**Manual testing rule.** Follow the browser click paths in this guide. The feature tour does not require Swagger, Postman, curl, or direct API calls. A few security and concurrency guarantees have no dedicated UI control; those limits are called out in Section 12 and covered, where available, by the repository's automated tests. The optional mock ERP scenarios are controls for a separate local simulator, not TicketPortal endpoints or end-user screens.

This guide follows the current project source and the three maintained project documents: [Project Concept](02-Project-Concept-and-Solution.md), [Remaining Fix Plan](03-Remaining-Fix-Plan.md), and this run guide. If a button label differs slightly in your current browser build, use the matching screen and visible action; the expected result is the behavior to verify.

---

# 2. Prerequisites

| Tool | Version | Why |
|---|---|---|
| Node.js | 22+ | Runs Nx, Angular, and the React/Vite admin app |
| npm | bundled with Node | `npm ci` / `npm install` |
| .NET SDK | .NET 10 | Builds and runs `apps/api` |
| SQL Server LocalDB | installed with Visual Studio | The API's database (see the callout below if you're not on Windows) |
| A modern browser | — | Trust the dev HTTPS certificate once (below) |

**LocalDB is Windows-only.** `apps/api/appsettings.json`'s connection string is
literally `Data Source=(localdb)\MSSQLLocalDB; ...`, and `scripts/reset-demo-db.sh`
refuses to run against anything that doesn't contain `(localdb)` in the connection
string — that's a deliberate safety guard, not an oversight. If you're on macOS or
Linux: run inside Windows or a Windows VM, or stand up a reachable SQL Server
yourself (e.g. the `mcr.microsoft.com/mssql/server` Docker image), point
`ConnectionStrings:DefaultConnection` in `apps/api/appsettings.Development.json` at
it, and adjust the `(localdb)` guard in the reset script to match.

---

# 3. First-time setup

```bash
git clone https://github.com/AsrafujjamanDeepu/TicketPortal.git
cd TicketPortal
npm install
dotnet dev-certs https --trust
```

Nothing else to configure for local Development — `appsettings.Development.json`
already ships a working, clearly-labeled non-secret JWT key, so a fresh clone runs
as-is as long as `ASPNETCORE_ENVIRONMENT=Development` (the default for a plain
`dotnet run`). Only if you run under a different environment name do you need:

```bash
dotnet user-secrets set "JWT:SigningKey" "<a real, random, 32+ character string>" --project apps/api
```

---

# 4. Running the three apps

## 4.1 One command for everything

Every one of the three apps already exposes an Nx `serve` target — confirmed in
each project's `project.json` / the `@nx/vite` plugin config in `nx.json` — so:

```bash
npx nx run-many -t serve -p frontend,admin,api --parallel=3
```

starts all three together, streaming each one's logs with its own colored prefix.
`Ctrl+C` once stops all three.

**First run only:** restore the API's dependencies once beforehand, or its panel in
the combined log will just fail on repeat:

```bash
npx nx run api:restore
```

**If `nx run-many` errors immediately** (`hashArray is not a function`,
`WorkspaceContext is not a constructor`) instead of starting the servers: that's
Nx's native binary (`@nx/nx-<platform>`) not matching this machine — typically from
`node_modules` being copied between OSes/architectures rather than installed fresh.
Fix:

```bash
npx nx reset
# Then, if dependencies are still mismatched, run npm ci from a clean repository checkout.
```

## 4.2 Running apps individually

| App | Command | URL |
|---|---|---|
| API | `npx nx run api:serve` | `https://localhost:54221` |
| Angular portal | `npx nx serve frontend` | `http://localhost:4200` |
| React admin | `npx nx serve admin` | `http://localhost:4300` (console at `/admin`) |

## 4.3 Optional fourth process: the mock Hanif ERP

Needed only for Section 10.13. Not an Nx project — a standalone Node script:

```bash
cd apps/mock-erp && npm install && npm start   # http://localhost:5099
```

`apps/api/appsettings.Development.json` already points at it — nothing else to
configure.

## 4.4 Ports & URLs at a glance

| Service | URL |
|---|---|
| API (Swagger — reference only, see the note in Section 1) | `https://localhost:54221` |
| Angular customer/staff portal | `http://localhost:4200` |
| React admin — console | `http://localhost:4300/admin` |
| Mock Hanif ERP (optional, not TicketPortal's UI) | `http://localhost:5099` |

On first start, the API creates `TicketPortalDB`, applies migrations, and seeds all
demo data — every account in Section 7, plus operators, buses, trips, terminals,
and payment providers.

---

# 5. Resetting the demo database

```bash
./scripts/reset-demo-db.sh        # macOS/Linux-style shell
./scripts/reset-demo-db.ps1       # Windows PowerShell
```

Both refuse to run against anything but a Development, LocalDB connection — they
cannot be pointed at a shared or production database by mistake. Start the API
again afterward and it recreates and reseeds automatically.

---

# 6. Troubleshooting

| You see | Cause | Fix |
|---|---|---|
| `Cannot reach the API at https://localhost:54221/api ...` | API not running, or untrusted dev certificate | Start the API; open the API origin once and trust the development certificate |
| `Invalid username or password` (admin login) | Stale admin row in an old database | Restart the API — it repairs the `admin` account on every Development start |
| `...temporarily locked...` | 5 wrong login attempts | Restart the API, or wait 15 minutes |
| `...not a platform Admin` | Signed in with a non-Admin account on the admin login | Use `admin`, or the Angular portal for every other role |
| "Hold Seats & Continue" fails: *"...may no longer exist"* | Browser has a login token from **before** a database reset (new DB = new user IDs) | Log out and back in — the app now catches this automatically |

---

# 7. Demo accounts reference

Since RBAC Amendment v3, **Identity role** (Admin / Staff / Customer — what's in the
JWT) and **Job role** (Manager, CounterStaff, Supervisor, Finance, Support — the
`StaffRole` on a Staff account's profile) are separate. Keep both in mind.

### Platform (Admin / Staff, no operator)

| Username | Password | Job role | What it's for |
|---|---|---|---|
| `admin` | `Admin@12345` | — (Identity Admin) | Full platform access |
| `nusrat.finance` | `Demo@12345` | Finance | Reconciliation, settlement, payouts — not finance config |
| `tanvir.ops` | `Demo@12345` | Manager | Cross-operator oversight, cancellation approval — not finance |
| `rezaul.support` | `Demo@12345` | Support | Bookings/complaints only — deliberately narrower than `tanvir.ops` |

### Green Line Paribahan (`PlatformManaged`)

| Username | Password | Job role |
|---|---|---|
| `abdul.karim.gl` | `Demo@12345` | Manager — full operator control |
| `selina.counter.gl` | `Demo@12345` | CounterStaff — **Gabtoli only** |
| `farida.counter.gl` | `Demo@12345` | CounterStaff — **Kalyanpur only** |

### Ena Transport (`PlatformManaged`)

| Username | Password | Job role |
|---|---|---|
| `nasima.counter.ena` | `Demo@12345` | CounterStaff — Gabtoli |

### Shohagh Paribahan (`Hybrid` inventory mode)

| Username | Password | Job role |
|---|---|---|
| `rina.counter.sho` | `Demo@12345` | CounterStaff — Kalyanpur |
| `delwar.supervisor.sho` | `Demo@12345` | Supervisor — manifest/check-in only |

### Hanif Enterprise (`ExternalApiManaged`)

| Username | Password | Job role |
|---|---|---|
| `iqbal.manager.han` | `Demo@12345` | Manager — back-office only, no counter login |

### Customers

`rahim.uddin`, `karim.sheikh`, `fatema.begum`, `nasrin.sultana`, `jashim.uddin`,
`shirin.akter`, `mitu.rahman` — password `Demo@12345` for all.

---

# 8. Role → permission cheat sheet

`Identity Admin` implicitly holds every permission everywhere.

### Platform-scoped

| Job role | Can do | Notably **cannot** |
|---|---|---|
| Manager | Fleet/trip oversight across every operator, cancel trips, crew, cancellation approval, complaints, reports | Manage users, finance config, payouts, settlement approval |
| Finance | Reconcile, approve settlements, process payouts | Configure commission/tax rules, fleet/trip/counter/HR |
| Support | Read bookings, read/manage complaints | Cancellation approval, finance, fleet/trip/counter/HR |

### Operator-scoped (own operator only)

| Job role | Can do | Notably **cannot** |
|---|---|---|
| Manager | Fleet, network, fares, trips (incl. cancel), crew, counters, staff, own finance (read) | Another operator's data, platform config, finance config, payouts |
| CounterStaff | Sell/cancel — **only at a counter they're actively assigned to** | Counter config, staff/fleet/finance, any other counter |
| Supervisor | Manifest read, ticket check-in | Selling, configuration, staff, finance |
| Finance | Read own operator's finance | Configure, approve settlements, another operator's data |

---

# 9. How to use the test script

Every row is **Actor → Click-path → Expected**. All steps are in the browser at
`http://localhost:4200` (customer/staff portal) or `http://localhost:4300/admin`
(admin console) unless stated otherwise. Run Part 10.2 (Customer) before the
staff/operator parts that need a real booking to exist. Start from a freshly reset
database (Section 5) for the cleanest run.

---

# 10. End-to-end test script

## 10.1 Anonymous / public

| # | Click-path | Expected |
|---|---|---|
| 1 | Open `http://localhost:4200` with no login | Redirects to **Search**; the search-home screen loads |
| 2 | Search a route/date with seeded trips (e.g. Dhaka → Chattogram) | Results span **multiple operators** in one search — Green Line, Ena, Shohagh, Hanif |
| 3 | Search a route/date with no trips | Empty-state message, not an error |
| 4 | Search with the same "from" and "to" terminal | Inline validation error |
| 5 | Open **Browse Buses** (`/buses`) | Public coach listing loads, no login |
| 6 | Open **Verify Ticket** (`/search/verify-ticket`) and enter any ticket number | Works with no login — deliberately public |
| 7 | On a trip's seat map, pick a seat, click **Hold Seats & Continue**, while logged out | Redirects to **Log In** with a return path back to this exact trip |

## 10.2 Customer journey (`rahim.uddin` / `Demo@12345`, or register a new account)

1. **Register (optional):** Registration page → Full Name, Username, Email,
   Password (6+ chars) → **Create Account**.
2. **Log in.**
3. **Search → select → hold a seat:** search a route, open a `Scheduled` trip's seat
   map, click a seat, click **Hold Seats & Continue**. **Expected:** hold timer
   starts (3–5 min), seat shows as *Held*.
4. **Seat-hold race:** open the *same* trip in a second tab (or as `karim.sheikh` in
   an incognito window), select the *same* seat, click **Hold Seats & Continue** in
   both within a couple of seconds. **Expected:** one tab proceeds; the other shows
   a "just taken by another customer" toast and its seat map refreshes.
5. **Trip-state gating** (needs one seeded trip already `Cancelled` and one already
   `Completed` — check via Admin → Trips list, or set one yourself: log in as
   `admin`, open **Admin Console → Trips**, click a `Scheduled` trip, **Edit**, set
   **Status** to `Cancelled`, **Save`). Back as `rahim.uddin`, paste that trip's URL
   directly (`http://localhost:4200/search/trip/<tripId>`) and click **Hold Seats &
   Continue**. **Expected:** rejected with *"This trip has been cancelled..."*. Same
   with a `Completed` trip → *"This trip has already finished."*
6. **Hold expiry (optional, takes a few minutes):** hold a seat, don't pay, wait out
   the timer. **Expected:** the hold screen counts down to 0 and marks itself
   Expired; the seat becomes bookable again with no manual fix needed.
7. **Checkout & payment:** from a fresh hold, continue through **Passenger
   Details**, **Payment**, complete the (demo-mode) payment. **Expected:** lands on
   the confirmation page; booking is `Confirmed`.
8. **My Bookings / My Tickets:** open **My Bookings** — the booking appears; open
   **My Tickets** — the ticket appears with a QR code and a working **Download PDF**
   button; open the ticket's own detail page.
9. **Verify the ticket:** at **Verify Ticket**, enter the ticket number from step 8.
10. **Request a cancellation:** open the booking's detail page, click **Request
    Cancellation**, pick the ticket, enter a reason, **Submit**. **Expected:** shows
    under **My Bookings → Cancellations** as *Pending* (a staff member processes it
    in 10.4, step 8).
11. **Wallet:** open **Wallet** — balance and transaction history are visible
    (top-ups, payments, refund credits); read-only in the current build.
12. **Complaints:** **Complaints → New**, submit one tied to the booking, confirm it
    appears in the list with a status.
13. **Profile:** **Profile**, edit a contact field, **Save**.

## 10.3 Counter Staff journey (`selina.counter.gl` Gabtoli, `farida.counter.gl` Kalyanpur — Green Line)

1. **Dashboard:** log in as `selina.counter.gl`, open **Counter**. **Expected:**
   lands on the **Dashboard** automatically, header reads "Green Line
   Paribahan — Gabtoli." Note "Tickets sold today" / "Cash sales total," complete
   one walk-in sale (next step), reload — both numbers increase, attributed to
   Gabtoli, not Kalyanpur. Log in as `abdul.karim.gl` instead — the Manager's
   dashboard totals are the **sum** of every counter's row.
2. **Walk-in cash sale:** **Walk-In Sale** → counter **Gabtoli** → search a trip →
   hold a seat → passenger details → **Confirm & Collect Payment** (Cash). Note the
   PNR. **Expected:** sale completes; searching that same trip afterward (any
   account, or logged out) shows the seat gone.
3. **Assigned-counter enforcement:** still as `selina.counter.gl`, restart
   **Walk-In Sale**, this time pick **Kalyanpur** from the counter dropdown (the
   list shows both — nothing in the UI itself stops the wrong pick), search, hold,
   fill in details, **Confirm & Collect Payment**. **Expected:** rejected with a
   "not assigned to this counter" toast.
4. **Printable receipt:** after a walk-in sale (ideally 2+ seats), click **Print
   Tickets** — a QR card per seat; **Print** hides navigation chrome and starts each
   ticket on its own page.
5. **Boarding desk / check-in:** log in as `delwar.supervisor.sho`, open
   **Boarding**, check a valid Issued ticket in. **Expected:** success; scanning the
   same ticket again still succeeds but reports "already checked in." Log in as
   `selina.counter.gl` and try the same screen — unreachable (wrong role and wrong
   operator).
6. **Cancellations & Refunds desk** (processing 10.2 step 10): log in as
   `abdul.karim.gl`, open **Cancellations & Refunds**, find the pending request,
   click **Approve**, then **Complete**; click **Approve** on the resulting refund,
   then **Process**. **Expected:** on the customer side, the request now shows
   resolved and the refund processed (Wallet reflects it if the refund routes there
   as credit). Try **Reject** with a reason on a different request too.
7. **Staff HR:** as `abdul.karim.gl`, open **Staff HR** — Profile / Attendance /
   Salary tabs, all with real Create/Edit/Delete forms (full CRUD walkthrough in
   Section 10.4, step 9 below).
8. **Supervisor's restricted view:** as `delwar.supervisor.sho`, open **Counter** —
   **Counter Setup** and **Staff HR** are unreachable.
9. **Complaints intake:** **Complaints** — any Staff account of the operator can
   view/triage (this screen is intentionally open to every Staff role for now, not
   permission-gated — see Section 13).

## 10.4 Operator Manager journey (`abdul.karim.gl`, Green Line) — full CRUD

A repeatable pattern applies to every screen below: **Create** a new record with
sample data → confirm it appears in the list → **Edit** one field → confirm it
persisted after a reload → **Delete** it (confirm dialog) → confirm it's gone.

| # | Screen | What to Create / Edit / Delete |
|---|---|---|
| 1 | **Fleet** | Add a bus (registration, category, seat layout); edit its capacity; upload/replace an image (validated for type/size); delete the bus |
| 2 | **Network Setup** | Add a route + stops; edit a stop's sequence/timing; delete a stop, then the route |
| 3 | **Fare & Cancellation Policies** | Add a fare rule (base fare + per-km rate); add a cancellation policy (fee tiers by time-before-departure); edit and delete each |
| 4 | **Crew Assignment** | Assign a driver/helper to a trip; remove the assignment |
| 5 | **Branches** | Add an operator branch/office; edit its address; delete it |
| 6 | **Trips & Scheduling** | Create a trip (route + bus + departure time); edit its fare; note the status dropdown deliberately has **no** "Cancelled" option — cancellation is the separate flow in step 7 |
| 7 | **Trip cancellation cascade:** on **Trips & Scheduling**, pick a `Scheduled` trip with at least one paid booking, click **Cancel trip…**. **Expected:** a preview loads first (how many bookings/holds affected) *before* anything is confirmed. Enter a reason, confirm. **Expected:** trip → `Cancelled`; every active hold on it released; every paid booking gets a full, no-fee refund automatically. Confirm on the customer side that the refund appears under Cancellations. |
| 8 | **Sales counter configuration** (**Counter Setup**) | Add a new sales counter for Green Line; edit its name; delete it |
| 9 | **Staff HR — Profile tab** | Create a staff profile (needs an existing login's User ID, employee code, a role from the dropdown); edit an employee's Role — set `selina.counter.gl`'s role from CounterStaff to Supervisor and save (**Expected:** succeeds — a lower/lateral change), then try setting it to Manager (**Expected:** rejected — a Manager cannot promote CounterStaff into Manager tier); delete a profile |
| 10 | **Staff HR — Attendance / Salary tabs** | Create an attendance record and a salary record for a staff member; edit and delete each |
| 11 | **Self-promotion guard:** still on **Staff HR**, find `abdul.karim.gl`'s **own** row, click **Edit**, try changing his own Role in the dropdown, **Save**. **Expected:** rejected — this holds even though the exact same form just worked on Selina's row one step ago; it's the amendment's guard against self-promotion, enforced on the save action itself, not just hidden in the UI. |
| 12 | **Cross-operator boundary:** open **Fleet → Add Bus** and look for any field to pick a different operator. **Expected:** there isn't one — the form hard-codes your own operator ID before it ever calls the API, so there's no button that lets you even attempt writing to Ena's or Shohagh's fleet from this account (see Section 12 for the one place this guard is *not* yet enforced server-side either). |

## 10.5 Supervisor journey (`delwar.supervisor.sho`, Shohagh)

Covered in 10.3 steps 5 and 8. Summary: confirm **Walk-In Sale**, **Fleet**, and
every **Finance** screen are unreachable for this account — only **Boarding** and a
read-only trips/manifest view are.

## 10.6 Platform Finance journey (`nusrat.finance`)

1. **Reconciliation:** open **Reconciliation** — a **Ledger Gaps** list (any
   Confirmed/Completed booking missing its ledger row). If any appear, click
   **Re-post** on one, confirm in the modal. **Expected:** succeeds; clicking
   **Re-post** again on the *same* row is refused ("...already has a ... entry —
   nothing to re-post") — the button itself is now a no-op, not a duplicate-posting
   risk.
2. **Settlements:** open **Settlements**, generate one for an operator/date range,
   **Approve** it. (Hand-check the numbers in Section 10.12.)
3. **Payouts / Invoices:** open **Payouts**, **Process** a pending payout for an
   operator the settlement determined the platform owes.
4. **Wallets & Ledger:** open **Wallets** — read-only running balance per operator.
5. **Negative test:** open **Commission Rules** or **System Config**. **Expected:**
   unreachable — Platform Finance can read/reconcile/approve/pay out, but only
   Admin can *configure* commission/tax/provider rules.

## 10.7 Platform Manager / Ops journey (`tanvir.ops`)

1. Browse fleet/trip data across every operator without an operator-specific login.
2. Use the platform-level cancellation-approval, complaints, and reports screens.
3. **Negative test:** every **Finance** screen and everything under **Admin** is
   unreachable.

## 10.8 Platform Support journey (`rezaul.support`)

Confirm booking/complaint lookup works, and that everything `tanvir.ops` can do
beyond that (cancellation approval, fleet/trip oversight, reports) is unreachable —
this narrower scope was the specific fix RBAC Amendment v3 made (this account was
previously mis-seeded with the wider Manager permission set).

## 10.9 Admin console — dedicated pages (`admin` / `Admin@12345`, `http://localhost:4300`)

1. Log in → landing page with shortcut cards → **Management Console** (`/admin`) —
   Buses/Trips/Terminals lists populated.
2. **Users & Roles:** open **Users**. Use the **Create account** form (name,
   username, email, password, Login Role, Job Role) to create a Staff account.
   **Retired-role rejection:** in the same form's Login Role dropdown, pick
   `Operator` and submit → rejected ("Operator identity role is retired"). In the
   **Assign / Change Role** form, pick Job Role `Admin` for an existing staff
   account and submit → rejected ("JobRole 'Admin' is retired"). Both dropdowns
   deliberately still list the retired options — the server is the one enforcing
   the rule, and clicking Submit on the wrong option is exactly how you prove that.
3. **Atomicity check:** in **Create account**, submit a duplicate Employee Code on
   purpose. **Expected:** rejected — and the login account the form would otherwise
   have created is *not* left behind orphaned; re-submitting with the same username
   afterward is refused as "already exists," proving the whole failed attempt rolled
   back together.
4. **Bus Operators:** **Bus Operators** list → **Create** a new operator → **Edit**
   its settlement-mode field → note it in the list → (optional) **Delete** it.
5. **Buses / Terminals:** same Create → Edit → Delete pattern.
6. **Trips:** **Create** a trip; **Edit** it and set **Status** to `Cancelled` or
   `Completed` (this is exactly what step 5 of Section 10.2 uses); **Delete** one.
7. **Trip Crews:** assign a crew member to a trip via **Create**; **Edit**; remove
   via **Delete**.
8. **Admin exemption:** on **Staff HR** (reachable from the Angular portal as
   `admin`, or via the Trips/StaffProfiles admin screens), set `selina.counter.gl`'s
   Role to Manager and save. **Expected:** succeeds — Admin is exempt from the
   operator-tier restriction that blocked `abdul.karim.gl` from doing the same thing
   in Section 10.4, step 11.

## 10.10 Admin console — every generated CRUD resource

Every resource below lives at **Admin Console → sidebar**, and every one in the
first table follows the exact same recipe as 10.4's table: open the list, click
**Create**, fill the form, **Save**, confirm it's listed; click a row, **Edit** a
field, **Save**, reload to confirm it stuck; click **Delete**, confirm the modal,
confirm it's gone. There are 47 of these — you don't need to run all 47 to trust the
pattern, but every one is a real form with real validation, not a stub:

<div style="font-size:8.7pt; line-height:1.5;">
Agents · Bookings · Bus Amenities · Bus Amenity Mappings · Bus Categories · Bus
Images · Bus Maintenance Logs · Bus Operators · Bus Routes · Buses · Cancellation
Policies · Commission Rules · Complaints · Coupons · Currencies · Customer
Addresses · Customer Profiles · Driver Licenses · Emergency Contacts · External
Booking Mappings · External Route Mappings · External Seat Mappings · External Trip
Mappings · Fare Rules · Languages · Offers · Operator Branches · Operator Contracts
· Operator Integration Endpoints · Operator Integrations · Operator Route Stops ·
Operator Settings · Payment Method Configurations · Payment Providers · Promo
Banners · Reviews · Route Stops · Sales Counters · Schedules · Staff Attendances ·
Staff Profiles · Staff Salaries · System Settings · Tax Rules · Terminals · Trip
Crews · Trips
</div>

A worked example, since "click Create" means little without seeing the fields once:
**Currencies** → **Create** → Code `BDT`-style text field, Symbol, Exchange Rate
(number), Is Active (checkbox) → **Save** → appears in the list → click it →
**Edit** → change Exchange Rate → **Save** → reload the page → the new rate
persisted → **Delete** → confirm modal → gone from the list.

**Read-only (list/detail only, no Create/Edit/Delete buttons by design — these are
logs and system-computed records):** Activity Logs, Audit Logs, Customer Wallet
Transactions, Integration Sync Logs, Integration Webhook Logs, Login Histories,
Notification Logs, Operator Payment Receipts, Operator Settlement Items, Operator
Statement Items, Operator Statements, Operator Wallets, Payment Histories, Payment
Webhook Events, Platform Ledgers, Refund Histories, Seat Hold Items, Tickets, Trip
Status Histories.

**Workflow-action screens (no generic Create/Edit/Delete — specific action buttons
instead, all real):** Cancellation Requests (Approve / Reject / Complete — used in
10.3 step 6), Coupon Usages, Operator Invoices, Operator Payouts (Process — used in
10.6 step 3), Operator Settlements (Generate / Approve — used in 10.6 step 2),
Payments, Refunds (Approve / Process — used in 10.3 step 6), Seat Holds.

## 10.11 Cross-role permission checks — all via real buttons

Everything below is a click-path you've already done somewhere above; this table
just collects the *negative* cases in one place so you can run them back-to-back.

| Actor | Click-path | Expected |
|---|---|---|
| `selina.counter.gl` | Walk-In Sale with counter = Gabtoli (her own) | Succeeds |
| `selina.counter.gl` | Walk-In Sale with counter = Kalyanpur (Farida's, same operator) | Rejected |
| `delwar.supervisor.sho` | Open Walk-In Sale at all | Unreachable — no selling permission |
| `selina.counter.gl` | Staff HR → edit her own Role | Screen itself is unreachable — CounterStaff has no Staff HR access |
| `abdul.karim.gl` | Staff HR → edit **his own** Role | Rejected (self-promotion guard) |
| `abdul.karim.gl` | Staff HR → edit **Selina's** Role, CounterStaff → Supervisor | Succeeds |
| `abdul.karim.gl` | Staff HR → edit **Selina's** Role, CounterStaff → Manager | Rejected |
| `selina.counter.gl` | Fleet → Add Bus | Unreachable — no Fleet.Manage |
| `abdul.karim.gl` | Fleet → Add Bus | Succeeds |
| any account | Profile page → own contact info | Always reachable and editable — self-service is intentionally universal |

## 10.12 Finance ledger & settlement hand-check

Use this to confirm the money math behind the screens in 10.6, not just that they
load.

**Example A.** An online booking, Green Line, total 1,000 BDT, 10% commission (no
gateway fee):

```
commission = round(1000 * 0.10, 2) = 100
Ledger:  Credit 1000 OnlineTicketSale
         Debit   100 PlatformCommission
Net owed to Green Line for this booking = 900 BDT
```

One counter sale for Green Line with a flat 10 BDT commission rule:

```
Ledger: Debit 10 CounterSaleCommission
Green Line now owes the platform 10 BDT for this ticket
```

Settling both: `(1000 − 100) + (0 − 10) = 890` → positive → **the platform pays
Green Line 890 BDT.** Generate a real settlement (10.6 step 2) covering both and
click into it — confirm `NetAmount`, `Direction`, and `PlatformCharge` on screen
match this by hand.

**Example B — gateway fee borne by the operator.** Same 1,000 BDT booking, 10%
commission, 20 BDT gateway fee, contract has the operator bear it:

```
commission = 100
Ledger:  Credit 1000 OnlineTicketSale
         Debit   100 PlatformCommission
         Debit    20 GatewayCharge
Net owed to the operator = 880 BDT
```

If the platform bears the fee instead, the `GatewayCharge` line never posts and the
operator is owed the full 900.

**Idempotency:** click **Generate** on Settlements a second time for the *exact same*
date range you just settled. **Expected:** "No unsettled ledger entries..." — the
button itself refuses to double-settle the same money.

## 10.13 Hanif ERP integration scenarios (optional — remote-controls the fake external ERP, not TicketPortal)

Needs `apps/mock-erp` running (Section 4.3). Hanif Enterprise is
`ExternalApiManaged` — its own ERP, not a TicketPortal counter login, is the source
of truth for seat availability and booking confirmation. This `curl` switch is how
you make the *stand-in* ERP behave a certain way before testing TicketPortal's
**real** booking UI against it — it isn't a shortcut around TicketPortal's own
buttons:

```bash
curl -X POST http://localhost:5099/__scenario -H "Content-Type: application/json" -d '{"scenario": "seat_unavailable"}'
curl -X POST http://localhost:5099/__reset
```

| Scenario | Then book a Hanif trip in the normal customer UI and expect... |
|---|---|
| `success` (default) | Confirms normally on the first try |
| `pending_then_confirmed` | First attempt Pending; confirms after the background sweep (every 2 min) |
| `seat_unavailable` | Seat-hold is refused immediately — no booking created |
| `server_error` | After `MaxSyncAttempts` failed sweeps (default 5), booking auto-marked `Failed`, tickets cancelled, seats released, refund created |
| `always_pending` | Retries forever, never hits the max-attempts limit — a documented scope boundary |
| `slow` | ~8s response — exceeds the availability check's 5s cap but stays under the 30s confirm timeout |

---

# 11. Known gaps you may run into while testing (current project limits)

These items are still open in [the remaining fix plan](03-Remaining-Fix-Plan.md). They are not evidence that the click-through guide is broken, but they define what this personal demo does not guarantee yet:

- **Finance policy decisions remain open:** fixed commission basis (per booking or per ticket), payout balance release timing, and payout-to-settlement validation need explicit decisions and implementation. Commission date selection also needs consistent Dhaka-local effective dates and overlapping-rule validation.
- **Trip rescheduling and inventory limits:** the UI validates trip status transitions, but the policy for changing a trip's time after ticket sales needs a decision. Hold rate limiting exists; maximum seats per hold and active holds per user are not yet configured.
- **ERP hardening:** the mock ERP is a local simulator. There is no browser scenario selector yet; its scenario controls are developer helpers. The API needs an explicit ERP-unavailable policy and additional outbound URL/secret protections before production use.
- **Database and release work:** the active-ticket-per-seat index is filtered but not unique; Bookings and Tickets list endpoints need paging; CI and centralized startup option validation remain follow-up work.
- **Tax and live payments are outside this personal project's scope:** the payment flow is simulated, no real gateway is connected, and no statutory tax rate is seeded. Do not treat demo financial values as money movement, tax advice, or a contract.
- **Verification scope:** the API suite passed 179 tests on 5 October 2026. That does not mean every screen has browser automation; this document is the manual UI walkthrough. The remaining plan lists the specific implementation and assurance work still open.

# 12. UI coverage boundaries (no Swagger/Postman required)


The workflows above use the visible application screens and buttons wherever the UI exposes an action. A few backend guarantees cannot be fully proven by ordinary clicks because the product intentionally has no screen for performing the unsafe action:

1. **Taking over another counter's pending payment.** The counter sale is one continuous staff session. There is no screen for resuming another staff member's pending booking. Do not use a real sale or another user's account to attempt this manually. Use the existing automated API tests when available, or leave this check to a future dedicated test.
2. **Forging another operator's identifier in a create request.** Operator forms scope new data to the signed-in operator and do not expose another operator selector. The UI test verifies the visible boundary; it cannot prove backend rejection of a forged request. The authorization tests and remaining C2 work track backend coverage.
3. **External ERP failure modes.** The normal customer booking journey remains a real UI test, but switching the mock ERP among `success`, `pending_then_confirmed`, `seat_unavailable`, `server_error`, `slow`, and `always_pending` is currently a developer helper, not a browser button. Do not confuse the mock's scenario controls with TicketPortal API testing. A local scenario page is listed as follow-up work.

You can complete the normal feature walkthrough without Swagger or Postman. Use Section 13 for the automated test command if you also want to check backend behavior that has no UI action.

# 13. Appendix — full command reference

```bash
# One-time setup
npm install
dotnet dev-certs https --trust
npx nx run api:restore

# Run everything together
npx nx run-many -t serve -p frontend,admin,api --parallel=3

# Run individually
npx nx run api:serve            # https://localhost:54221
npx nx serve frontend           # http://localhost:4200
npx nx serve admin              # http://localhost:4300 (console at /admin)

# Optional: mock Hanif ERP (fake external system, not TicketPortal's UI)
cd apps/mock-erp && npm install && npm start   # http://localhost:5099

# Reset the demo database
./scripts/reset-demo-db.sh        # macOS/Linux-style shell
./scripts/reset-demo-db.ps1       # Windows PowerShell

# Backend build/test (needs the real .NET SDK)
npx nx run api:build
npx nx run api.tests:restore
npx nx run api.tests:test
```
