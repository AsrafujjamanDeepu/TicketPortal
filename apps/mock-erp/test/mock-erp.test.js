// Chunk 6 / C6-5 — tests for the mock ERP itself. Run: `npm test` in apps/mock-erp (Node 18+;
// uses the built-in test runner, so there is nothing extra to install beyond express).
const test = require('node:test');
const assert = require('node:assert/strict');
const { createMockErp } = require('../src/app');

const KEY = 'test-key';

async function start(options = {}) {
  const { app } = createMockErp({ apiKey: KEY, quiet: true, slowMs: 50, timeoutMs: 120, ...options });
  const server = await new Promise((resolve) => {
    const s = app.listen(0, '127.0.0.1', () => resolve(s));
  });
  const base = `http://127.0.0.1:${server.address().port}`;

  async function call(method, path, { body, headers = {}, auth = true } = {}) {
    const res = await fetch(base + path, {
      method,
      headers: {
        ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        ...(auth ? { 'X-API-Key': KEY } : {}),
        ...headers,
      },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    let json = null;
    try { json = JSON.parse(text); } catch (e) { /* html or empty */ }
    return { status: res.status, json, text };
  }

  return { base, call, close: () => new Promise((resolve) => { server.closeAllConnections?.(); server.close(resolve); }) };
}

const confirm = (call, bookingId, extra = {}, headers = {}) =>
  call('POST', '/api/v1/bookings/confirm', {
    body: { bookingId, pnr: 'PNR12345', tripId: 'trip-1', seatNumbers: ['B2', 'B3'], grandTotal: 100, currency: 'BDT', ...extra },
    headers,
  });

test('API routes require the API key; health and the page do not', async () => {
  const s = await start();
  try {
    assert.equal((await s.call('GET', '/api/v1/trips/t1/seats', { auth: false })).status, 401);
    assert.equal((await s.call('GET', '/health', { auth: false })).status, 200);
    const page = await s.call('GET', '/', { auth: false });
    assert.equal(page.status, 200);
    assert.match(page.text, /Mock ERP/);
  } finally { await s.close(); }
});

test('ConfirmBooking is idempotent: repeating a request returns the same key and creates one booking', async () => {
  const s = await start();
  try {
    const headers = { 'Idempotency-Key': 'confirm-b1' };
    const first = await confirm(s.call, 'b1', {}, headers);
    const second = await confirm(s.call, 'b1', {}, headers);
    const third = await confirm(s.call, 'b1', {}, headers);

    assert.equal(first.status, 200);
    assert.equal(first.json.status, 'Confirmed');
    assert.equal(second.json.externalBookingKey, first.json.externalBookingKey);
    assert.equal(third.json.externalPnr, first.json.externalPnr);

    const state = (await s.call('GET', '/__state', { auth: false })).json;
    assert.equal(state.bookings.length, 1);
    assert.equal(state.bookings[0].replays, 2);
    assert.deepEqual(state.soldSeats[0].seats.sort(), ['B2', 'B3']);
  } finally { await s.close(); }
});

test('the same Idempotency-Key with a different bookingId is refused', async () => {
  const s = await start();
  try {
    assert.equal((await confirm(s.call, 'b1', {}, { 'Idempotency-Key': 'k1' })).status, 200);
    const clash = await confirm(s.call, 'b2', {}, { 'Idempotency-Key': 'k1' });
    assert.equal(clash.status, 422);
    assert.equal(clash.json.error, 'IDEMPOTENCY_KEY_REUSED');
    assert.equal((await s.call('GET', '/__state', { auth: false })).json.bookings.length, 1);
  } finally { await s.close(); }
});

test('a cancelled booking is not resurrected by a late ConfirmBooking retry', async () => {
  const s = await start();
  try {
    await confirm(s.call, 'b1');
    assert.equal((await s.call('POST', '/api/v1/bookings/b1/cancel', { body: {} })).status, 200);
    const late = await confirm(s.call, 'b1');
    assert.equal(late.status, 409);
    assert.equal(late.json.error, 'BOOKING_CANCELLED');
  } finally { await s.close(); }
});

test('bad input is rejected', async () => {
  const s = await start();
  try {
    assert.equal((await s.call('POST', '/api/v1/bookings/confirm', { body: {} })).status, 400);
    assert.equal((await confirm(s.call, 'b1', { seatNumbers: 'A1' })).status, 400);
    assert.equal((await s.call('POST', '/__scenario', { body: { scenario: 'nope' } })).status, 400);
  } finally { await s.close(); }
});

test('pending_then_confirmed answers 202 once, then confirms with a stable key', async () => {
  const s = await start();
  try {
    await s.call('POST', '/__scenario', { body: { scenario: 'pending_then_confirmed' } });
    assert.equal((await confirm(s.call, 'b1')).status, 202);
    const second = await confirm(s.call, 'b1');
    assert.equal(second.status, 200);
    assert.equal((await confirm(s.call, 'b1')).json.externalBookingKey, second.json.externalBookingKey);
  } finally { await s.close(); }
});

test('always_pending never confirms; seat_unavailable reports A1 sold and answers 409', async () => {
  const s = await start();
  try {
    await s.call('POST', '/__scenario', { body: { scenario: 'always_pending' } });
    assert.equal((await confirm(s.call, 'b1')).status, 202);
    assert.equal((await confirm(s.call, 'b1')).status, 202);

    await s.call('POST', '/__scenario', { body: { scenario: 'seat_unavailable' } });
    assert.deepEqual((await s.call('GET', '/api/v1/trips/t9/seats')).json.soldSeatNumbers, ['A1']);
    assert.equal((await confirm(s.call, 'b2')).status, 409);
  } finally { await s.close(); }
});

test('server_error fails availability AND confirm; slow and timeout delay availability', async () => {
  const s = await start();
  try {
    await s.call('POST', '/__scenario', { body: { scenario: 'server_error' } });
    assert.equal((await s.call('GET', '/api/v1/trips/t1/seats')).status, 500);
    assert.equal((await confirm(s.call, 'b1')).status, 500);
    assert.equal((await s.call('GET', '/health', { auth: false })).status, 200);

    await s.call('POST', '/__scenario', { body: { scenario: 'slow' } });
    let started = Date.now();
    assert.equal((await s.call('GET', '/api/v1/trips/t1/seats')).status, 200);
    assert.ok(Date.now() - started >= 45, 'slow should delay');

    await s.call('POST', '/__scenario', { body: { scenario: 'timeout' } });
    started = Date.now();
    assert.equal((await s.call('GET', '/api/v1/trips/t1/seats')).status, 200);
    assert.ok(Date.now() - started >= 110, 'timeout should delay longer');
  } finally { await s.close(); }
});

test('reset clears bookings, sold seats, requests and the scenario', async () => {
  const s = await start();
  try {
    await s.call('POST', '/__scenario', { body: { scenario: 'always_pending' } });
    await confirm(s.call, 'b1', {}, { 'Idempotency-Key': 'confirm-b1' });
    await s.call('POST', '/__reset', { body: {} });

    const state = (await s.call('GET', '/__state', { auth: false })).json;
    assert.equal(state.scenario, 'success');
    assert.equal(state.bookings.length, 0);
    assert.equal(state.recentRequests.length, 0);

    // The same Idempotency-Key is usable again with another booking after a reset.
    assert.equal((await confirm(s.call, 'b9', {}, { 'Idempotency-Key': 'confirm-b1' })).status, 200);
  } finally { await s.close(); }
});

test('recent requests record the Idempotency-Key and status', async () => {
  const s = await start();
  try {
    await confirm(s.call, 'b1', {}, { 'Idempotency-Key': 'confirm-b1' });
    const state = (await s.call('GET', '/__state', { auth: false })).json;
    assert.equal(state.recentRequests[0].idempotencyKey, 'confirm-b1');
    assert.equal(state.recentRequests[0].status, 200);
  } finally { await s.close(); }
});

test('state-changing /__ routes reject a cross-origin browser request', async () => {
  const s = await start();
  try {
    const evil = await s.call('POST', '/__scenario', {
      body: { scenario: 'server_error' },
      headers: { Origin: 'https://evil.example' },
    });
    assert.equal(evil.status, 403);
    assert.equal((await s.call('GET', '/__scenario', { auth: false })).json.scenario, 'success');

    const own = await s.call('POST', '/__scenario', {
      body: { scenario: 'slow' },
      headers: { Origin: s.base },
    });
    assert.equal(own.status, 200);
  } finally { await s.close(); }
});
