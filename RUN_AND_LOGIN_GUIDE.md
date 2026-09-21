# TicketPortal — run & login guide

## Prerequisites
- .NET 10 SDK, SQL Server LocalDB (installed with Visual Studio), Node 22+, `npm ci` once in this folder.
- Trust the HTTPS dev certificate once: `dotnet dev-certs https --trust`

## Start (three terminals)
| App | Command | URL |
|---|---|---|
| API | `npx nx run api:serve` | https://localhost:54221/swagger |
| Customer / staff portal (Angular) | `npx nx serve frontend` | http://localhost:4200 |
| Admin (React) | `npx nx serve admin` | http://localhost:4300 (management console at http://localhost:4300/admin) |

On first start the API creates the `TicketPortalDB` LocalDB database, applies the migration, and seeds demo data
(operators, buses, trips, terminals, payment providers) plus these accounts.

## Accounts (Development only)
| Role | Username | Password |
|---|---|---|
| Platform Admin | `admin` | `Admin@12345` |
| Customer (demo) | `rahim.uddin`, `karim.sheikh` | `Demo@12345` |
| Platform finance staff (demo) | `nusrat.finance` | `Demo@12345` |
| Counter staff, Green Line (demo) | `selina.counter.gl` | `Demo@12345` |

(The full list of seeded demo users is in `apps/api/Data/DemoDataSeeder.cs`; all of them use `Demo@12345`.)

The API repairs the `admin` account **every time it starts in Development** (unlocks it, restores the role, resets the
password to the value above) and prints one line saying what it did, e.g.
`Bootstrap admin 'admin' was repaired (cleared login lockout, reset password ...)`.
In Production it never touches an existing admin — change the password after first login.

## "I can't log in to the admin panel"
The admin login now says why. Match the message:

| You see | Cause | Fix |
|---|---|---|
| `Cannot reach the API at https://localhost:54221/api ...` | API not running, wrong port, or untrusted dev certificate | Start the API; open the Swagger URL once and accept/trust the cert (`dotnet dev-certs https --trust`); check `VITE_API_BASE_URL` in `apps/admin/.env.development` |
| `Invalid username or password` | Stale/changed admin row in an old database | Restart the API (it repairs the row). Still failing? Read the API console for a line beginning `Bootstrap admin` |
| `...temporarily locked...` | 5 wrong attempts | Restart the API (clears the lockout) or wait 15 min |
| `...not a platform Admin` | Signed in with a customer/staff/operator account | Use `admin`, or the Angular portal for other roles |
| Console shows `Bootstrap admin ... could NOT be created ... e-mail already belongs to 'xyz'` | Another user already owns `admin@ticketportal.local` | Log in as `xyz`, or use a fresh database |

## "Hold Seats & Continue" fails
If it says *"Could not create the seat hold — one of the referenced records may no longer exist"* (older API) or
*"Your login belongs to an account that no longer exists"*: your browser still holds a login from **before the database was
re-created** (a new database = new user ids). Log out and log in again. The current API returns 401 for such tokens, so the
app sends you to the login page and back to the same seat map by itself.

Fresh database (deletes local data): in `apps/api` run `dotnet ef database drop --force`, then start the API again.
Or point `Initial Catalog` in `apps/api/appsettings.json` at a new name to keep the old data.

## What is where
- **Angular portal (4200):** search trips, seat map + 3–5 min seat hold, checkout/payment, My Bookings, **My Tickets (QR + PDF)**,
  **Complaints**, wallet/cancellations, **Browse Buses** (`/buses`), Verify Ticket, plus Operator / Counter / Finance panels.
- **Admin (4300):** users & roles, operators, and section pages that lead into the **Management Console** (`/admin`):
  dashboard and CRUD for the API's 76 resources (audit/activity logs, integrations & mappings, coupons/offers/banners,
  commission rules, settlements/payouts/ledgers, payment providers/methods, terminals, routes, trips, schedules ...).
  The console is a separate page with its own Bootstrap/Tailwind styling; the "Management Console" sidebar link and its
  "Back to Admin Home" link are full page loads on purpose.
- Seat map: the coach is drawn from above (door/driver at the top, rows down, aisle in the middle, window seats marked,
  Lower/Upper deck tabs for double-deckers) using each seat's row/column from the API.
- Times: the API sends UTC timestamps without a `Z`; the Angular app now adds it (`utc-dates.interceptor.ts`), so trip
  times display in the viewer's timezone (Dhaka = UTC+6).

## Smoke test (10 minutes)
1. Admin login → the landing page shows shortcut cards; click **Management Console** → the dashboard loads and the Buses / Trips / Terminals lists load.
2. Console → *System Settings* → Commission rules, Payment providers: lists load from the API.
3. Angular: log in as `rahim.uddin` (or register a customer) → search Dhaka → Chattogram → pick a trip → select seats (hold timer starts) → pay.
4. **My Bookings → Tickets**: the new ticket appears; open it, QR shows, **Download PDF** works.
5. **Complaints → New complaint**, submit; it shows in the list; in the console → Complaints it appears.
6. `/buses` lists coaches; open one, "Select seats" goes to the seat map. `/search/verify-ticket` verifies the ticket number.
7. Let a hold expire (5 min) and confirm the seats free up again.

## Deploying
- Serve `apps/admin` build output with rewrites `/admin/*` → `/console/index.html` and everything else → `/index.html`.
- Set real `apiBaseUrl` (Angular `environment.prod.ts`), `VITE_API_BASE_URL` (admin `.env.production`) and the API's CORS origins.
