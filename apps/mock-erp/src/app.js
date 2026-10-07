// Chunk 8 / Chunk 6 — the mock ERP application, built by a factory so a test can start a fresh,
// isolated instance on a random port (see test/mock-erp.test.js) and index.js can start the real
// one. All state lives INSIDE the object createMockErp() returns — nothing is module-global.
//
// What changed in Chunk 6 (C6-5):
//   - A browser page at GET / to switch scenarios, reset state, and see the bookings, sold seats
//     and recent requests (src/ui.js). No curl needed for the manual walkthrough any more.
//   - GetSeatAvailability now honours the failure scenarios (server_error / slow / timeout), so
//     the API's "ERP can't be reached" path (decision D7) can be exercised from the browser.
//   - ConfirmBooking is idempotent on bookingId, understands the Idempotency-Key header the API
//     now sends, and reports replays — see the notes above the route.
//   - Local-development-only hardening: index.js binds 127.0.0.1 and refuses to start when
//     NODE_ENV=production; state-changing /__ routes reject cross-origin browser requests.
//
// State is IN MEMORY and is lost when the process restarts — intentional for a demo fixture. After
// a restart the mock has forgotten every booking, so a retry of an earlier ConfirmBooking creates a
// NEW external booking key (a real ERP persists these). POST /__reset clears state without a restart.
const express = require('express');
const { renderUi } = require('./ui');

const SCENARIOS = {
  success: 'Confirms every booking immediately.',
  pending_then_confirmed: 'First ConfirmBooking for a booking answers 202 Pending; the next one confirms it.',
  always_pending: 'Never confirms (always 202 Pending). After Integrations:MaxSyncAttempts the API rejects the booking and refunds it.',
  seat_unavailable: 'Reports seat A1 sold on every trip and answers ConfirmBooking with 409 (seat sold elsewhere).',
  server_error: 'Seat availability and ConfirmBooking both answer 500 (simulated ERP outage). /health stays OK.',
  slow: 'Seat availability and ConfirmBooking take 8 seconds — longer than the API\'s 5 second availability cap.',
  timeout: 'Seat availability and ConfirmBooking hang for 45 seconds — longer than any API timeout.',
};

const MAX_RECENT_REQUESTS = 40;

function createMockErp(options = {}) {
  const apiKey = options.apiKey || 'demo-hanif-erp-key';
  const requireAuthEnabled = options.requireAuth !== false;
  const slowMs = options.slowMs ?? 8000;
  const timeoutMs = options.timeoutMs ?? 45000;
  const quiet = options.quiet === true;

  const startedAtUtc = new Date().toISOString();
  let currentScenario = 'success';

  /** @type {Map<string, { bookingId: string, status: string, externalBookingKey?: string, externalPnr?: string, tripId?: string, seatNumbers?: string[], attempts: number, replays: number, idempotencyKey?: string }>} */
  const bookings = new Map();

  /** @type {Map<string, Set<string>>} tripId -> seat numbers this mock considers sold */
  const soldSeatsByTrip = new Map();

  /** @type {Map<string, string>} Idempotency-Key -> bookingId it was first used with */
  const idempotencyKeys = new Map();

  /** @type {Array<{ atUtc: string, method: string, path: string, status: number, idempotencyKey: string | null, scenario: string }>} */
  const recentRequests = [];

  const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  function log(label, extra) {
    if (quiet) return;
    // eslint-disable-next-line no-console
    console.log(`[mock-erp] ${label} (scenario=${currentScenario})`, extra ? JSON.stringify(extra) : '');
  }

  function markSeatsSold(tripId, seatNumbers) {
    if (!tripId || !Array.isArray(seatNumbers)) return;
    if (!soldSeatsByTrip.has(tripId)) soldSeatsByTrip.set(tripId, new Set());
    const set = soldSeatsByTrip.get(tripId);
    for (const seat of seatNumbers) set.add(String(seat).toUpperCase());
  }

  function soldSeatsFor(tripId) {
    const set = new Set(soldSeatsByTrip.get(tripId) || []);
    // seat_unavailable always reports seat "A1" sold on every trip, regardless of booking
    // history, so the live-availability check has something to refuse immediately.
    if (currentScenario === 'seat_unavailable') set.add('A1');
    return Array.from(set);
  }

  function newExternalKey(prefix) {
    return `${prefix}-${Date.now()}-${Math.floor(Math.random() * 100000)}`;
  }

  function snapshotState() {
    return {
      scenario: currentScenario,
      scenarios: Object.entries(SCENARIOS).map(([name, description]) => ({ name, description })),
      startedAtUtc,
      bookings: Array.from(bookings.values()).map((b) => ({
        bookingId: b.bookingId,
        status: b.status,
        externalBookingKey: b.externalBookingKey || null,
        externalPnr: b.externalPnr || null,
        tripId: b.tripId || null,
        seatNumbers: b.seatNumbers || [],
        attempts: b.attempts,
        replays: b.replays,
      })),
      soldSeats: Array.from(soldSeatsByTrip.entries()).map(([tripId, seats]) => ({ tripId, seats: Array.from(seats) })),
      recentRequests: recentRequests.slice().reverse(),
    };
  }

  const app = express();
  app.disable('x-powered-by');
  app.use(express.json());

  // Record every /api/v1 call (method, path, final status, Idempotency-Key) for the page.
  app.use((req, res, next) => {
    res.on('finish', () => {
      if (!req.originalUrl.startsWith('/api/v1')) return;
      recentRequests.push({
        atUtc: new Date().toISOString(),
        method: req.method,
        path: req.originalUrl,
        status: res.statusCode,
        idempotencyKey: req.get('Idempotency-Key') || null,
        scenario: currentScenario,
      });
      while (recentRequests.length > MAX_RECENT_REQUESTS) recentRequests.shift();
    });
    next();
  });

  function requireAuth(req, res, next) {
    if (!requireAuthEnabled) return next();
    if (req.get('X-API-Key') !== apiKey) {
      return res.status(401).json({ error: 'UNAUTHORIZED', message: 'Missing or invalid X-API-Key header.' });
    }
    next();
  }

  // The /__ routes change the mock's behaviour, so a web page on some OTHER site must not be able
  // to drive them through the developer's browser. Browsers always send Origin on a cross-site
  // POST; server-to-server callers and curl send none and are unaffected.
  function sameOriginOnly(req, res, next) {
    const origin = req.get('Origin');
    if (origin) {
      let originHost = '';
      try {
        originHost = new URL(origin).host;
      } catch (e) {
        originHost = '';
      }
      if (originHost !== req.get('Host')) {
        return res.status(403).json({ error: 'CROSS_ORIGIN_FORBIDDEN', message: 'This control is for the page served by this mock only.' });
      }
    }
    next();
  }

  // -------------------------------------------------------------------------------------------
  // Operational routes — no API key. GET /health backs the API's "Test connection" button.
  // -------------------------------------------------------------------------------------------
  app.get('/', (req, res) => {
    res.set('Content-Security-Policy', "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'");
    res.type('html').send(renderUi());
  });

  app.get('/health', (req, res) => {
    res.json({ status: 'ok', scenario: currentScenario, uptimeSeconds: Math.round(process.uptime()) });
  });

  app.get('/__state', (req, res) => {
    res.json(snapshotState());
  });

  app.get('/__scenario', (req, res) => {
    res.json({ scenario: currentScenario, availableScenarios: Object.keys(SCENARIOS) });
  });

  app.post('/__scenario', sameOriginOnly, (req, res) => {
    const { scenario } = req.body || {};
    if (!Object.prototype.hasOwnProperty.call(SCENARIOS, scenario)) {
      return res.status(400).json({
        error: 'UNKNOWN_SCENARIO',
        message: `scenario must be one of: ${Object.keys(SCENARIOS).join(', ')}`,
      });
    }
    currentScenario = scenario;
    log('scenario changed');
    res.json({ scenario: currentScenario });
  });

  app.post('/__reset', sameOriginOnly, (req, res) => {
    bookings.clear();
    soldSeatsByTrip.clear();
    idempotencyKeys.clear();
    recentRequests.length = 0;
    currentScenario = 'success';
    log('state reset');
    res.json({ ok: true });
  });

  // -------------------------------------------------------------------------------------------
  // The four calls in docs/03-Remaining-Fix-Plan.md. All under /api/v1 and all require the
  // X-API-Key header, matching what ExternalBookingSyncService sends.
  // -------------------------------------------------------------------------------------------
  const api = express.Router();
  api.use(requireAuth);

  // Applies the failure scenarios shared by availability and confirm. Returns true when it has
  // already answered (so the caller must stop).
  async function applyOutageScenario(res) {
    if (currentScenario === 'server_error') {
      res.status(500).json({ error: 'INTERNAL_ERROR', message: 'Simulated ERP outage.' });
      return true;
    }
    if (currentScenario === 'slow') await delay(slowMs);
    if (currentScenario === 'timeout') await delay(timeoutMs);
    return false;
  }

  // GetSeatAvailability — GET /trips/:tripId/seats
  api.get('/trips/:tripId/seats', async (req, res) => {
    const { tripId } = req.params;
    if (await applyOutageScenario(res)) return;
    const soldSeatNumbers = soldSeatsFor(tripId);
    log('GetSeatAvailability', { tripId, soldSeatNumbers });
    res.json({ tripId, soldSeatNumbers, checkedAtUtc: new Date().toISOString() });
  });

  // ConfirmBooking — POST /bookings/confirm
  //
  // IDEMPOTENCY (C6-5). The API retries a ConfirmBooking whose reply it never saw (timeout, 5xx,
  // still-pending), so the same request WILL arrive more than once. Two rules keep that from
  // creating duplicates:
  //   1. bookingId is the identity: there is at most ONE record per bookingId, and every repeat
  //      returns that record's externalBookingKey/externalPnr (counted in `replays`).
  //   2. The Idempotency-Key header (sent as "confirm-<bookingId>") is remembered against the
  //      bookingId it was first used with; the same key with a DIFFERENT bookingId is a client
  //      bug and is refused with 422 rather than silently confirming the wrong booking.
  // A booking that has been cancelled is not resurrected by a late ConfirmBooking retry (409).
  api.post('/bookings/confirm', async (req, res) => {
    const { bookingId, pnr, tripId, seatNumbers, grandTotal, currency } = req.body || {};

    if (typeof bookingId !== 'string' || bookingId.length === 0 || bookingId.length > 100) {
      return res.status(400).json({ error: 'BAD_REQUEST', message: 'bookingId is required (a string up to 100 characters).' });
    }
    if (seatNumbers !== undefined && (!Array.isArray(seatNumbers) || seatNumbers.length > 60)) {
      return res.status(400).json({ error: 'BAD_REQUEST', message: 'seatNumbers must be an array of at most 60 seat numbers.' });
    }

    const idempotencyKey = req.get('Idempotency-Key');
    if (idempotencyKey) {
      const firstSeenFor = idempotencyKeys.get(idempotencyKey);
      if (firstSeenFor !== undefined && firstSeenFor !== bookingId) {
        return res.status(422).json({
          error: 'IDEMPOTENCY_KEY_REUSED',
          message: 'This Idempotency-Key was already used with a different bookingId.',
        });
      }
    }

    log('ConfirmBooking received', { bookingId, pnr, tripId, seatNumbers, grandTotal, currency, idempotencyKey });

    if (await applyOutageScenario(res)) return;

    if (currentScenario === 'seat_unavailable') {
      markSeatsSold(tripId, seatNumbers);
      return res.status(409).json({
        error: 'SEAT_UNAVAILABLE',
        message: `Seat(s) ${(seatNumbers || []).join(', ')} already sold through another channel.`,
      });
    }

    const existing = bookings.get(bookingId);

    if (existing && existing.status === 'Cancelled') {
      return res.status(409).json({ error: 'BOOKING_CANCELLED', message: 'This booking was cancelled and cannot be confirmed.' });
    }

    const record = existing || { bookingId, status: 'Pending', tripId, seatNumbers, attempts: 0, replays: 0 };
    if (existing) record.replays += 1;
    record.attempts += 1;
    if (idempotencyKey) {
      record.idempotencyKey = idempotencyKey;
      idempotencyKeys.set(idempotencyKey, bookingId);
    }
    bookings.set(bookingId, record);

    if (currentScenario === 'always_pending') {
      return res.status(202).json({ status: 'Pending', message: 'Still processing.' });
    }

    if (currentScenario === 'pending_then_confirmed' && record.attempts < 2) {
      return res.status(202).json({ status: 'Pending', message: 'Still processing — check back shortly.' });
    }

    // 'success', the tail of 'slow'/'timeout', the second attempt of 'pending_then_confirmed',
    // and every replay of an already-confirmed booking: the SAME key and PNR come back each time.
    record.status = 'Confirmed';
    record.externalBookingKey = record.externalBookingKey || newExternalKey('HAN-BK');
    record.externalPnr = record.externalPnr || `HAN-${(pnr || bookingId).toString().slice(-6).toUpperCase()}`;
    markSeatsSold(record.tripId, record.seatNumbers);

    res.json({ status: 'Confirmed', externalBookingKey: record.externalBookingKey, externalPnr: record.externalPnr });
  });

  // GetBookingStatus — GET /bookings/:bookingId/status
  // Not currently called by ExternalBookingSyncService (see its class-level comment) — implemented
  // here for contract completeness and for a future "check status now" admin action.
  api.get('/bookings/:bookingId/status', (req, res) => {
    const record = bookings.get(req.params.bookingId);
    if (!record) {
      return res.status(404).json({ error: 'NOT_FOUND', message: 'Unknown bookingId.' });
    }
    res.json({
      bookingId: req.params.bookingId,
      status: record.status,
      externalBookingKey: record.externalBookingKey || null,
      externalPnr: record.externalPnr || null,
    });
  });

  // CancelBooking — POST /bookings/:bookingId/cancel
  api.post('/bookings/:bookingId/cancel', (req, res) => {
    const { bookingId } = req.params;
    const record = bookings.get(bookingId);

    if (record) {
      record.status = 'Cancelled';
    }

    log('CancelBooking', { bookingId, found: Boolean(record), idempotencyKey: req.get('Idempotency-Key') || null });

    // Idempotent/permissive — cancelling something this mock never confirmed (or cancelling the
    // same booking twice) still succeeds, same as most real cancel endpoints.
    res.json({ bookingId, status: 'Cancelled' });
  });

  app.use('/api/v1', api);

  app.use((req, res) => {
    res.status(404).json({ error: 'NOT_FOUND', message: `No route for ${req.method} ${req.path}` });
  });

  return { app, scenarios: Object.keys(SCENARIOS), getScenario: () => currentScenario };
}

module.exports = { createMockErp, SCENARIOS };
