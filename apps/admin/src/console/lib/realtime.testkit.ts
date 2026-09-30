// Test helpers for lib/realtime.ts and hooks/useRealtime.ts (REALTIME_SIGNALR_PLAN.md, Chunk 7).
// Not imported by the app, so it never reaches the production bundle.

import { RealtimeClient, type RealtimeChange, type RealtimeConnection } from './realtime';

/** A stand-in for SignalR's HubConnection that the test drives by hand. */
export class FakeConnection implements RealtimeConnection {
  handlers = new Map<string, (...args: unknown[]) => void>();
  reconnecting: Array<(e?: Error) => void> = [];
  reconnected: Array<(id?: string) => void> = [];
  closed: Array<(e?: Error) => void> = [];
  startCalls = 0;
  stopCalls = 0;
  /** Make start() reject with this error (e.g. { statusCode: 404 }). */
  startError: unknown = null;

  async start(): Promise<void> {
    this.startCalls += 1;
    if (this.startError) throw this.startError;
  }
  async stop(): Promise<void> {
    this.stopCalls += 1;
  }
  on(methodName: string, handler: (...args: unknown[]) => void): void {
    this.handlers.set(methodName, handler);
  }
  onreconnecting(handler: (e?: Error) => void): void {
    this.reconnecting.push(handler);
  }
  onreconnected(handler: (id?: string) => void): void {
    this.reconnected.push(handler);
  }
  onclose(handler: (e?: Error) => void): void {
    this.closed.push(handler);
  }

  // ---- drivers ----
  emitChanges(changes: Array<Partial<RealtimeChange> & { entity: string }>): void {
    this.handlers.get('changes')?.(changes.map((c) => ({ action: 'updated', ...c })));
  }
  dropAndRecover(): void {
    this.reconnecting.forEach((h) => h());
    this.reconnected.forEach((h) => h('new-id'));
  }
  serverClose(error?: Error): void {
    this.closed.forEach((h) => h(error));
  }
}

export interface Harness {
  client: RealtimeClient;
  connections: FakeConnection[];
  /** The newest connection created so far. */
  current: () => FakeConnection;
  setToken: (token: string | null) => void;
  /** Change what start() does for connections created from now on (null = succeed). */
  setStartError: (error: unknown) => void;
  sessionLost: { count: number };
}

export function createHarness(opts: { token?: string | null; retryDelaysMs?: number[]; startError?: unknown } = {}): Harness {
  let token: string | null = opts.token === undefined ? 'jwt' : opts.token;
  let startError: unknown = opts.startError ?? null;
  const connections: FakeConnection[] = [];
  const sessionLost = { count: 0 };
  const client = new RealtimeClient({
    hubUrl: 'https://api.test/hubs/realtime',
    getToken: () => token,
    createConnection: () => {
      const c = new FakeConnection();
      c.startError = startError;
      connections.push(c);
      return c;
    },
    onSessionLost: () => {
      sessionLost.count += 1;
    },
    retryDelaysMs: opts.retryDelaysMs ?? [1000, 5000],
  });
  return {
    client,
    connections,
    current: () => connections[connections.length - 1],
    setToken: (t) => {
      token = t;
    },
    setStartError: (e) => {
      startError = e;
    },
    sessionLost,
  };
}

/** Let pending promise continuations (connect()'s await start()) run. */
export async function flush(): Promise<void> {
  for (let i = 0; i < 5; i++) await Promise.resolve();
}
