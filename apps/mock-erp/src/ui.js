// The page served at GET / by the mock ERP (Chunk 6 / C6-5): switch the scenario, reset state,
// and watch what the TicketPortal API has sent. Plain HTML + a little script — no build step, no
// external requests (the Content-Security-Policy in app.js allows only this page's own inline
// script and same-origin fetches). Everything the server sends is rendered with textContent, never
// as HTML, so a booking id or seat number cannot inject markup.
function renderUi() {
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Mock ERP — local development only</title>
<style>
  :root { color-scheme: light dark; --b: #8884; --ok: #1a7f37; --bad: #b42318; }
  body { font: 14px/1.45 system-ui, sans-serif; margin: 0; padding: 16px 20px 40px; max-width: 1100px; }
  h1 { font-size: 20px; margin: 0 0 2px; }
  h2 { font-size: 15px; margin: 22px 0 8px; }
  .muted { opacity: .7; }
  .warn { border: 1px solid var(--b); border-left: 4px solid #d29922; padding: 8px 12px; margin: 10px 0 0; }
  .scenarios { display: grid; gap: 6px; }
  .scenarios label { display: flex; gap: 8px; align-items: flex-start; border: 1px solid var(--b); border-radius: 6px; padding: 8px 10px; cursor: pointer; }
  .scenarios label.active { border-color: var(--ok); box-shadow: inset 3px 0 0 var(--ok); }
  .scenarios code { font-weight: 600; }
  button { font: inherit; padding: 6px 12px; border-radius: 6px; border: 1px solid var(--b); cursor: pointer; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  th, td { text-align: left; padding: 4px 8px; border-bottom: 1px solid var(--b); vertical-align: top; word-break: break-all; }
  th { font-weight: 600; }
  .s2 { color: var(--ok); } .s4, .s5 { color: var(--bad); }
  #err { color: var(--bad); min-height: 1.2em; }
</style>
</head>
<body>
<h1>Mock ERP <span class="muted">— Hanif Enterprise (demo)</span></h1>
<div class="muted">Stands in for an operator's own booking system for TicketPortal's API. <span id="meta"></span></div>
<div class="warn">Local development only. State is held in memory and is lost when this process restarts; after a restart a retried ConfirmBooking creates a <em>new</em> external booking key.</div>

<h2>Scenario</h2>
<div class="scenarios" id="scenarios"></div>
<p><button id="reset">Reset all state (bookings, sold seats, scenario → success)</button> <span id="err"></span></p>

<h2>Bookings the API has sent</h2>
<table><thead><tr><th>Booking id</th><th>Status</th><th>External key</th><th>PNR</th><th>Seats</th><th>Attempts</th><th>Replays</th></tr></thead><tbody id="bookings"></tbody></table>

<h2>Seats this mock reports as sold</h2>
<table><thead><tr><th>Trip id</th><th>Seats</th></tr></thead><tbody id="sold"></tbody></table>

<h2>Recent requests (newest first)</h2>
<table><thead><tr><th>Time (UTC)</th><th>Request</th><th>Status</th><th>Idempotency-Key</th><th>Scenario</th></tr></thead><tbody id="requests"></tbody></table>

<script>
(function () {
  var errEl = document.getElementById('err');

  function cell(tr, text, cls) {
    var td = document.createElement('td');
    td.textContent = text == null ? '' : String(text);
    if (cls) td.className = cls;
    tr.appendChild(td);
  }

  function fill(id, rows, empty, build) {
    var body = document.getElementById(id);
    body.textContent = '';
    if (!rows.length) {
      var tr = document.createElement('tr');
      var td = document.createElement('td');
      td.colSpan = 7; td.className = 'muted'; td.textContent = empty;
      tr.appendChild(td); body.appendChild(tr);
      return;
    }
    rows.forEach(function (r) { var tr = document.createElement('tr'); build(tr, r); body.appendChild(tr); });
  }

  function send(path, body) {
    return fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body || {}) })
      .then(function (r) { if (!r.ok) throw new Error(path + ' answered ' + r.status); return r.json(); });
  }

  function render(state) {
    errEl.textContent = '';
    document.getElementById('meta').textContent = 'Started ' + state.startedAtUtc + '.';

    var box = document.getElementById('scenarios');
    box.textContent = '';
    state.scenarios.forEach(function (s) {
      var label = document.createElement('label');
      if (s.name === state.scenario) label.className = 'active';
      var input = document.createElement('input');
      input.type = 'radio'; input.name = 'scenario'; input.checked = s.name === state.scenario;
      input.addEventListener('change', function () {
        send('/__scenario', { scenario: s.name }).then(refresh).catch(showError);
      });
      var text = document.createElement('span');
      var code = document.createElement('code'); code.textContent = s.name;
      var desc = document.createElement('span'); desc.className = 'muted'; desc.textContent = ' — ' + s.description;
      text.appendChild(code); text.appendChild(desc);
      label.appendChild(input); label.appendChild(text); box.appendChild(label);
    });

    fill('bookings', state.bookings, 'No bookings received yet.', function (tr, b) {
      cell(tr, b.bookingId); cell(tr, b.status); cell(tr, b.externalBookingKey); cell(tr, b.externalPnr);
      cell(tr, b.seatNumbers.join(', ')); cell(tr, b.attempts); cell(tr, b.replays);
    });
    fill('sold', state.soldSeats, 'Nothing sold.', function (tr, t) { cell(tr, t.tripId); cell(tr, t.seats.join(', ')); });
    fill('requests', state.recentRequests, 'No requests yet.', function (tr, r) {
      cell(tr, r.atUtc); cell(tr, r.method + ' ' + r.path);
      cell(tr, r.status, 's' + String(r.status).charAt(0));
      cell(tr, r.idempotencyKey || ''); cell(tr, r.scenario);
    });
  }

  function showError(e) { errEl.textContent = 'Could not reach the mock: ' + e.message; }

  function refresh() {
    return fetch('/__state').then(function (r) { return r.json(); }).then(render).catch(showError);
  }

  document.getElementById('reset').addEventListener('click', function () {
    send('/__reset').then(refresh).catch(showError);
  });

  refresh();
  setInterval(refresh, 2000);
})();
</script>
</body>
</html>`;
}

module.exports = { renderUi };
