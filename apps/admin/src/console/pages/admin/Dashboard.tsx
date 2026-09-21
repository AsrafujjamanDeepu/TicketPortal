import { useEffect, useState } from "react"
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, PieChart, Pie, Cell, CartesianGrid } from "recharts"
import { Users2, Bus, Building2, Ticket, DollarSign, Clock, AlertCircle, RefreshCcw } from "lucide-react"
import { api } from "@/lib/api"

// No dashboard-summary endpoint exists in the API (there is no DashboardController), so per
// the master prompt every number here is computed client-side from real list endpoints —
// nothing is invented or hard-coded.
interface Kpi { label: string; value: string | number; icon: any; hint?: string }

export default function Dashboard() {
  const [loading, setLoading] = useState(true)
  const [kpis, setKpis] = useState<Kpi[]>([])
  const [statusDist, setStatusDist] = useState<{ name: string; value: number }[]>([])
  const [bookingTrend, setBookingTrend] = useState<{ date: string; count: number }[]>([])

  useEffect(() => {
    let cancelled = false
    async function load() {
      setLoading(true)
      const safe = async (url: string) => {
        try { const r = await api.get(url); return Array.isArray(r.data) ? r.data : [] } catch { return [] }
      }
      const [customers, operators, buses, bookings, payments, refunds, cancellations, seatHolds] = await Promise.all([
        safe("/api/CustomerProfiles"),
        safe("/api/BusOperators"),
        safe("/api/Buses"),
        safe("/api/Bookings"),
        safe("/api/Payments"),
        safe("/api/Refunds"),
        safe("/api/CancellationRequests"),
        safe("/api/SeatHolds"),
      ])
      if (cancelled) return

      const today = new Date().toDateString()
      const todaysBookings = bookings.filter((b: any) => new Date(b.createdAtUtc).toDateString() === today)
      const revenue = payments.filter((p: any) => p.status === "Succeeded").reduce((sum: number, p: any) => sum + (p.netReceivedAmount ?? p.amount ?? 0), 0)
      const pendingPayments = payments.filter((p: any) => ["Initiated","Pending"].includes(p.status)).length
      const pendingRefunds = refunds.filter((r: any) => ["Requested","Approved","Processing"].includes(r.status)).length
      const pendingCancellations = cancellations.filter((c: any) => c.status === "Requested").length
      const activeHolds = seatHolds.filter((h: any) => h.status === "Active").length

      setKpis([
        { label: "Total Customers", value: customers.length, icon: Users2 },
        { label: "Total Operators", value: operators.length, icon: Building2 },
        { label: "Total Buses", value: buses.length, icon: Bus },
        { label: "Total Bookings", value: bookings.length, icon: Ticket },
        { label: "Today Bookings", value: todaysBookings.length, icon: Clock },
        { label: "Revenue (Succeeded)", value: revenue.toLocaleString(undefined, { maximumFractionDigits: 2 }), icon: DollarSign },
        { label: "Pending Payments", value: pendingPayments, icon: AlertCircle },
        { label: "Pending Cancellations", value: pendingCancellations, icon: RefreshCcw },
        { label: "Pending Refunds", value: pendingRefunds, icon: RefreshCcw },
        { label: "Active Seat Holds", value: activeHolds, icon: Clock },
      ])

      const dist: Record<string, number> = {}
      for (const b of bookings) dist[b.status] = (dist[b.status] ?? 0) + 1
      setStatusDist(Object.entries(dist).map(([name, value]) => ({ name, value })))

      const byDay: Record<string, number> = {}
      for (const b of bookings) {
        const d = new Date(b.createdAtUtc).toISOString().slice(0, 10)
        byDay[d] = (byDay[d] ?? 0) + 1
      }
      const days = Object.entries(byDay).sort(([a], [b]) => a.localeCompare(b)).slice(-14)
      setBookingTrend(days.map(([date, count]) => ({ date: date.slice(5), count })))

      setLoading(false)
    }
    load()
    return () => { cancelled = true }
  }, [])

  const COLORS = ["#2563eb","#16a34a","#f59e0b","#dc2626","#7c3aed","#0891b2","#db2777","#65a30d","#ea580c"]

  return (
    <div className="space-y-6">
      <h1 className="text-xl font-semibold text-gray-900">Overview</h1>

      <div className="grid grid-cols-2 gap-4 sm:grid-cols-3 lg:grid-cols-5">
        {(loading ? Array.from({length:10}) : kpis).map((k: any, i) => (
          <div key={i} className="card p-4">
            {loading ? (
              <div className="h-12 animate-pulse rounded bg-gray-100" />
            ) : (
              <>
                <div className="flex items-center gap-2 text-gray-400"><k.icon className="h-4 w-4" /><span className="text-xs font-medium uppercase tracking-wide">{k.label}</span></div>
                <div className="mt-1 text-2xl font-bold text-gray-900">{k.value}</div>
              </>
            )}
          </div>
        ))}
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <div className="card p-4">
          <h2 className="mb-2 text-sm font-semibold text-gray-700">Booking Trend (last 14 days)</h2>
          <ResponsiveContainer width="100%" height={260}>
            <BarChart data={bookingTrend}>
              <CartesianGrid strokeDasharray="3 3" stroke="#f1f5f9" />
              <XAxis dataKey="date" fontSize={11} />
              <YAxis fontSize={11} allowDecimals={false} />
              <Tooltip />
              <Bar dataKey="count" fill="#2563eb" radius={[4,4,0,0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
        <div className="card p-4">
          <h2 className="mb-2 text-sm font-semibold text-gray-700">Booking Status Distribution</h2>
          <ResponsiveContainer width="100%" height={260}>
            <PieChart>
              <Pie data={statusDist} dataKey="value" nameKey="name" outerRadius={90} label>
                {statusDist.map((_, i) => <Cell key={i} fill={COLORS[i % COLORS.length]} />)}
              </Pie>
              <Tooltip />
            </PieChart>
          </ResponsiveContainer>
        </div>
      </div>
    </div>
  )
}
