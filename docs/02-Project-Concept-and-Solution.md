# TicketPortal — Project Concept and Solution

## Project summary

TicketPortal is a personal-project prototype of a multi-operator bus-ticket marketplace. It brings trip search, booking, counter sales, operator tools, platform administration, and financial settlement into one shared system. A passenger can compare trips from several bus companies in one place; each operator manages only its own fleet, trips, staff, and sales; the platform can reconcile online and counter-sale obligations.

The application demonstrates complete business workflows using seeded data and a simulated payment confirmation. It does not process live money or claim to be a production ticket marketplace.

## The problem it addresses

Passengers often need to search several bus-company websites or visit counters separately. Smaller operators may not have online booking tools, and online marketplaces need a reliable way to track amounts collected for operators, commissions, counter sales, refunds, and payouts. TicketPortal models a shared place to discover trips and an operational and financial back office for those businesses.

## Users and their goals

| User | Main needs |
|---|---|
| Passenger | Compare trips, choose seats, place a temporary hold, complete a demo checkout, retrieve a QR/PDF ticket, manage cancellations, and contact support. |
| Operator manager | Manage the operator's own buses, routes, fares, schedules, trips, crew, counters, and staff. |
| Counter staff | Sell walk-in tickets and print receipts at an assigned physical counter. |
| Boarding supervisor | View a trip manifest and check passenger tickets in. |
| Platform finance | Reconcile ledgers, generate/approve settlements, and manage payout workflows. |
| Platform support/operations | Find bookings, handle complaints, and perform assigned cross-operator support/approval tasks. |
| Platform administrator | Manage platform users, operators, configuration, marketing, integrations, reports, and audit data. |

Identity login roles are Admin, Staff, and Customer. Staff job roles and operator scope supply finer permissions; a Green Line employee cannot manage another operator's resources. Counter staff are additionally limited to their assigned counter.

## Operator models

### Full-platform operator

The operator uses TicketPortal for online bookings and physical-counter sales. Online payment state is simulated in this project. Counter cash is treated as collected directly by the operator, while TicketPortal records the sale and calculates the platform commission.

### API-connected operator

The operator already has its own ERP. TicketPortal models online inventory checks and booking confirmation through an operator API. It does not import that operator's cash-counter sales. A small in-memory mock ERP is included to demonstrate the connection and common response scenarios.

## Passenger booking journey

1. Search a common route/date across operators.
2. Inspect a trip and seat map, including seat type and deck where available.
3. Hold seats temporarily to prevent double-selling while completing checkout.
4. Enter passenger information and confirm a demo payment.
5. Issue the booking and tickets with a PNR, QR code, and downloadable PDF.
6. View booking history, request cancellation/refund, see wallet activity, and file a complaint.

The API and database enforce the seat lifecycle; holds expire and release inventory. The project includes regression tests for simultaneous holds and simultaneous booking creation.

## Business model and financial solution

The financial model is asymmetric by sales channel:

- **Online sale:** the model assumes the platform collects the fare, retains the configured commission, accounts for any simulated gateway fee, and owes the operator the remaining amount.
- **Counter sale:** the operator receives the cash directly. TicketPortal records the transaction and calculates commission due from the operator.
- **Settlement:** ledger entries for both channels are netted for an operator and date range. The resulting position indicates whether the platform pays the operator or the operator owes the platform.
- **Refund:** cancellation policies determine a refundable amount. The project reserves refunds against captured demo-payment value, tracks wallet/manual payout state, and proportionally reverses commission according to the recorded decision.

Commissions may be percentage or fixed amount and can vary by operator, sales channel, or route. Demo figures are illustrative; they are not a contract, accounting opinion, or payment instruction.

## Applications and architecture

| Component | Purpose and technology |
|---|---|
| Passenger and operator web app | Angular 22; customer search/booking and operator, counter, boarding, and finance screens. |
| Platform admin app | React 19 + Vite; dashboard and management console. |
| API | ASP.NET Core Web API on .NET 10; authentication, permissions, booking and financial rules. |
| Database | SQL Server through Entity Framework Core 10; migrations and seeded Development demo data. |
| Shared libraries | TypeScript API models and design tokens consumed by Angular and React. |
| Mock operator ERP | Node.js/Express; in-memory development simulator for API-connected operator flows. |
| Tests | xUnit unit, architecture, and API integration tests; integration tests use SQL Server LocalDB. |

All applications run from one Nx monorepo. Both front ends use the API as the source of truth; important business rules are enforced by the backend rather than relying only on hidden buttons or client validation.

## Security and data boundaries

The project models user authentication, fine-grained permissions, operator ownership checks, customer booking privacy, staff/counter assignments, rate-limited access, token revocation, and private storage for passenger identity photos. A local demo admin account is Development-only. Non-Development startup uses secret-backed bootstrap credentials and private storage configuration.

These are project security features, not a security certification. A production deployment still requires its host's secret management, persistent private file storage, SMTP, CORS and response-header policy, monitoring, backup, and incident processes.

## Scope and explicit limits

- Payments and refunds are simulated. No card, bank, or mobile financial service is connected.
- No active tax rate is seeded. Any optional configured tax is a demo calculation only, not VAT due.
- Bangladesh tax treatment depends on the service classification and current applicable rules. Company refund terms, such as a bus marketplace's published cancellation policy, are commercial terms and are not law. TicketPortal does not copy those terms or claim compliance.
- The ERP is a local simulator, not a real operator connector.
- The UI, seed data, and automated tests are for education and demonstration. Production launch needs the remaining hardening work in `03-Remaining-Fix-Plan.md`.

## Project value

TicketPortal demonstrates the integration of a consumer booking journey, operator operations, role-aware multi-tenant access, seat concurrency, simulated payment, refunds, and two-sided settlement in one full-stack application. The prototype offers a concrete solution design for fragmented trip discovery and operator back-office needs while keeping live payments and legal/tax configuration outside its current scope.
