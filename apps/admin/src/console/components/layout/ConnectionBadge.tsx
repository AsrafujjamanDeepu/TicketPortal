import { useRealtimeStatus } from "@/hooks/useRealtime"

const LABELS = {
  live: { text: "Live", dot: "bg-green-500", pill: "bg-green-50 text-green-700 border-green-200" },
  reconnecting: { text: "Reconnecting", dot: "bg-amber-500 animate-pulse", pill: "bg-amber-50 text-amber-700 border-amber-200" },
  offline: { text: "Offline", dot: "bg-gray-400", pill: "bg-gray-100 text-gray-600 border-gray-200" },
} as const

const HINTS = {
  live: "Pages update automatically as data changes.",
  reconnecting: "Trying to reach the live-update service. Pages fall back to periodic refresh.",
  offline: "Live updates are unavailable. Pages refresh periodically instead.",
} as const

// Small status pill for the admin layout. Owns the shared connection for as long as the
// console is open (useRealtimeStatus retains it), so it shows the truth even on pages that
// do not subscribe to anything.
export default function ConnectionBadge() {
  const status = useRealtimeStatus()
  const { text, dot, pill } = LABELS[status]
  return (
    <span
      role="status"
      aria-live="polite"
      title={HINTS[status]}
      className={`inline-flex items-center gap-1.5 rounded-full border px-2.5 py-0.5 text-xs font-medium ${pill}`}
    >
      <span className={`h-2 w-2 rounded-full ${dot}`} aria-hidden="true" />
      {text}
    </span>
  )
}
