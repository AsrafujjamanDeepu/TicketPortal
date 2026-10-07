// Chunk 8 — a small standalone server standing in for Hanif's own ERP. Implements the four
// calls documented in docs/03-Remaining-Fix-Plan.md, so
// apps/api/Services/ExternalBookingSyncService.cs has something real to point at in dev/demo
// instead of a fictional https://erp.hanifenterprise.example.com URL that was never reachable.
//
// Run it:   node apps/mock-erp/src/index.js   (or `npm start` / `npm run serve` from this folder)
// Then open http://localhost:5099/ to switch scenarios, reset state and watch requests (Chunk 6).
// Point the API at it: appsettings.Development.json already sets
//   Integrations:HanifErpBaseUrl              = http://localhost:5099/api/v1
//   Integrations:Secrets:HANIF_ERP_API_KEY    = demo-hanif-erp-key   (must match this server's own key below)
//
// LOCAL DEVELOPMENT ONLY (Chunk 6): it listens on 127.0.0.1 unless MOCK_ERP_HOST says otherwise,
// has no real authentication on its /__ control routes, and refuses to start when
// NODE_ENV=production.
//
// Deliberately plain Node + Express, no build step, no TypeScript — this only needs to exist for
// local dev/demo, and keeping it dependency-light means `npm install` inside apps/mock-erp is
// the only setup required. The application itself lives in app.js (so tests can start isolated
// instances); this file only reads the environment and starts listening.
const { createMockErp } = require('./app');

if (process.env.NODE_ENV === 'production') {
  // eslint-disable-next-line no-console
  console.error('[mock-erp] refusing to start: this is a local-development fixture and must not run with NODE_ENV=production.');
  process.exit(1);
}

const PORT = process.env.MOCK_ERP_PORT ? Number(process.env.MOCK_ERP_PORT) : 5099;
const HOST = process.env.MOCK_ERP_HOST || '127.0.0.1';
const API_KEY = process.env.HANIF_ERP_API_KEY || 'demo-hanif-erp-key';
const REQUIRE_AUTH = process.env.MOCK_ERP_REQUIRE_AUTH !== 'false';

const { app, getScenario } = createMockErp({ apiKey: API_KEY, requireAuth: REQUIRE_AUTH });

app.listen(PORT, HOST, () => {
  // eslint-disable-next-line no-console
  console.log(`[mock-erp] listening on http://${HOST === '127.0.0.1' ? 'localhost' : HOST}:${PORT} (auth ${REQUIRE_AUTH ? 'ON' : 'OFF'}, scenario=${getScenario()}) — open it in a browser to control it`);
});
