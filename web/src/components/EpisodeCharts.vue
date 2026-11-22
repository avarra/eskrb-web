<script setup lang="ts">
import { computed } from 'vue'
import type { ChartRule, ChartVariable, EpisodeCharts } from '@/services/directory'

const props = defineProps<{ data: EpisodeCharts }>()

// Shared geometry, so every chart and the activity strip line up on the same days.
const W = 860
const LEFT = 56
const RIGHT = 14
const PLOT = W - LEFT - RIGHT
const span = computed(() => Math.max(1, props.data.dayNumber - 1, props.data.days.length - 1))
const dayOf = (date: string) =>
  Math.round(
    (Date.parse(date + 'T00:00:00Z') - Date.parse(props.data.startDate + 'T00:00:00Z')) / 86400000,
  )
const x = (day: number) => LEFT + (day / span.value) * PLOT
const todayX = computed(() => x(Math.max(0, props.data.dayNumber - 1)))
const weekTicks = computed(() => {
  const t: number[] = []
  for (let d = 0; d <= span.value; d += 7) t.push(d)
  return t
})

// ---------- activity strip ----------
const ROW = 24
const rows = [
  { key: 'checkIns', label: 'Check-ins' },
  { key: 'notes', label: 'Notes' },
  { key: 'photos', label: 'Photos' },
  { key: 'messages', label: 'Messages' },
  { key: 'flags', label: 'Flags' },
] as const
const activityH = rows.length * ROW + 26
const rowY = (i: number) => 12 + i * ROW + ROW / 2
const daysList = computed(() => props.data.days.map((d) => ({ ...d, day: dayOf(d.date) })))
const missed = computed(() =>
  daysList.value.filter((d) => d.checkIns === 0 && d.date < props.data.today),
)

// ---------- numeric charts ----------
const H = 150
const TOP = 12
const BOTTOM = 26
const plotH = H - TOP - BOTTOM

type State = 'ok' | 'attention' | 'urgent'
const hit = (r: ChartRule, v: number) =>
  r.op === '>'
    ? v > r.value
    : r.op === '>='
      ? v >= r.value
      : r.op === '<'
        ? v < r.value
        : v <= r.value
function stateOf(v: ChartVariable, value: number): State {
  const hits = v.rules.filter((r) => hit(r, value))
  return hits.some((r) => r.severity === 'urgent') ? 'urgent' : hits.length ? 'attention' : 'ok'
}

function view(v: ChartVariable) {
  const vals = [...v.points.map((p) => p.value), ...v.rules.map((r) => r.value)]
  let lo = v.type === 'scale' && v.min != null ? v.min : Math.min(...vals)
  let hi = v.type === 'scale' && v.max != null ? v.max : Math.max(...vals)
  if (v.type === 'number') {
    const pad = Math.max(0.3, (hi - lo) * 0.15)
    lo -= pad
    hi += pad
  }
  if (hi - lo < 1) hi = lo + 1
  const y = (val: number) => TOP + (1 - (val - lo) / (hi - lo)) * plotH
  const clampY = (val: number) => Math.min(TOP + plotH, Math.max(TOP, y(val)))

  const bands = [...v.rules]
    .sort((a, b) => (a.severity === 'urgent' ? 1 : 0) - (b.severity === 'urgent' ? 1 : 0))
    .map((r) => {
      const up = r.op === '>' || r.op === '>='
      const edge = clampY(r.value)
      return {
        severity: r.severity,
        y: up ? TOP : edge,
        h: up ? edge - TOP : TOP + plotH - edge,
        line: edge,
        label: `${r.op} ${r.value}${v.unit ?? ''}`,
      }
    })

  const points = v.points.map((p) => ({
    ...p,
    px: x(dayOf(p.date)),
    py: y(p.value),
    state: stateOf(v, p.value),
  }))
  // one line per day-ordered reading (several readings on a day stay as separate dots)
  const path = points
    .map((p, i) => `${i ? 'L' : 'M'}${p.px.toFixed(1)},${p.py.toFixed(1)}`)
    .join(' ')
  const ticks = [lo, (lo + hi) / 2, hi].map((t) => ({
    y: y(t),
    label: Number(t.toFixed(1)).toString(),
  }))
  const latest = points[points.length - 1]
  return { bands, points, path, ticks, latest }
}

const charts = computed(() => props.data.variables.map((v) => ({ v, ...view(v) })))
const fmtDate = (d: string) =>
  new Date(d + 'T12:00:00').toLocaleDateString(undefined, { day: 'numeric', month: 'short' })
</script>

<template>
  <section class="trends">
    <h2>Activity</h2>
    <div class="card">
      <svg :viewBox="`0 0 ${W} ${activityH}`" role="img" aria-label="Events over time">
        <g v-for="t in weekTicks" :key="t">
          <line :x1="x(t)" :x2="x(t)" y1="6" :y2="rows.length * ROW + 12" class="grid" />
          <text :x="x(t)" :y="activityH - 6" class="axis" text-anchor="middle">
            Day {{ t + 1 }}
          </text>
        </g>
        <g v-for="(r, i) in rows" :key="r.key">
          <text :x="LEFT - 8" :y="rowY(i) + 4" class="rowlabel" text-anchor="end">
            {{ r.label }}
          </text>
          <line :x1="LEFT" :x2="W - RIGHT" :y1="rowY(i)" :y2="rowY(i)" class="rowline" />
        </g>

        <!-- missed check-ins: an open ring where a check-in was expected -->
        <circle
          v-for="d in missed"
          :key="'m' + d.date"
          :cx="x(d.day)"
          :cy="rowY(0)"
          r="4"
          class="missed"
        >
          <title>{{ fmtDate(d.date) }}: no check-in</title>
        </circle>
        <template v-for="d in daysList" :key="d.date">
          <circle v-if="d.checkIns" :cx="x(d.day)" :cy="rowY(0)" r="5" class="dot checkin">
            <title>
              {{ fmtDate(d.date) }}: {{ d.checkIns }} check-in{{ d.checkIns > 1 ? 's' : '' }}
            </title>
          </circle>
          <circle v-if="d.notes" :cx="x(d.day)" :cy="rowY(1)" r="5" class="dot note">
            <title>{{ fmtDate(d.date) }}: {{ d.notes }} note{{ d.notes > 1 ? 's' : '' }}</title>
          </circle>
          <rect
            v-if="d.photos"
            :x="x(d.day) - 5"
            :y="rowY(2) - 5"
            width="10"
            height="10"
            rx="2"
            class="dot photo"
          >
            <title>{{ fmtDate(d.date) }}: {{ d.photos }} photo{{ d.photos > 1 ? 's' : '' }}</title>
          </rect>
          <circle
            v-if="d.patientMessages"
            :cx="x(d.day) - (d.staffMessages ? 4 : 0)"
            :cy="rowY(3)"
            r="4.5"
            class="dot msg-patient"
          >
            <title>{{ fmtDate(d.date) }}: {{ d.patientMessages }} from the patient</title>
          </circle>
          <circle
            v-if="d.staffMessages"
            :cx="x(d.day) + (d.patientMessages ? 4 : 0)"
            :cy="rowY(3)"
            r="4.5"
            class="msg-staff"
          >
            <title>{{ fmtDate(d.date) }}: {{ d.staffMessages }} from staff</title>
          </circle>
          <circle
            v-if="d.attention"
            :cx="x(d.day) - (d.urgent ? 4 : 0)"
            :cy="rowY(4)"
            r="4.5"
            class="dot flag-attention"
          >
            <title>
              {{ fmtDate(d.date) }}: {{ d.attention }} attention flag{{
                d.attention > 1 ? 's' : ''
              }}
            </title>
          </circle>
          <circle
            v-if="d.urgent"
            :cx="x(d.day) + (d.attention ? 4 : 0)"
            :cy="rowY(4)"
            r="4.5"
            class="dot flag-urgent"
          >
            <title>
              {{ fmtDate(d.date) }}: {{ d.urgent }} urgent flag{{ d.urgent > 1 ? 's' : '' }}
            </title>
          </circle>
        </template>
        <line :x1="todayX" :x2="todayX" y1="4" :y2="rows.length * ROW + 14" class="today" />
      </svg>
      <p class="meta legend">
        <span><i class="sw checkin" /> check-in</span>
        <span><i class="sw ring" /> missed</span>
        <span><i class="sw note" /> note</span>
        <span><i class="sw photo sq" /> photo</span>
        <span
          ><i class="sw msg-patient" /> / <i class="sw ring dark" /> patient / staff message</span
        >
        <span
          ><i class="sw flag-attention" /> / <i class="sw flag-urgent" /> attention / urgent
          flag</span
        >
        <span><i class="sw today-sw" /> today</span>
      </p>
    </div>

    <h2>Trends</h2>
    <div v-for="c in charts" :key="c.v.questionId" class="card chart">
      <header>
        <div>
          <strong>{{ c.v.text }}</strong>
        </div>
        <div v-if="c.latest" class="latest">
          <span class="value" :class="c.latest.state"
            >{{ c.latest.value }}{{ c.v.unit ?? '' }}</span
          >
          <span class="meta">latest, {{ fmtDate(c.latest.date) }}</span>
        </div>
      </header>
      <svg :viewBox="`0 0 ${W} ${H}`" role="img" :aria-label="c.v.text">
        <rect
          :x="LEFT"
          :y="TOP"
          :width="PLOT"
          :height="plotH"
          :class="c.v.rules.length ? 'zone-ok' : 'zone-plain'"
        />
        <rect
          v-for="(b, i) in c.bands"
          :key="i"
          :x="LEFT"
          :y="b.y"
          :width="PLOT"
          :height="Math.max(0, b.h)"
          :class="'zone-' + b.severity"
        />
        <g v-for="t in weekTicks" :key="t">
          <line :x1="x(t)" :x2="x(t)" :y1="TOP" :y2="TOP + plotH" class="grid" />
          <text :x="x(t)" :y="H - 8" class="axis" text-anchor="middle">Day {{ t + 1 }}</text>
        </g>
        <g v-for="(t, i) in c.ticks" :key="'y' + i">
          <text :x="LEFT - 8" :y="t.y + 4" class="axis" text-anchor="end">{{ t.label }}</text>
        </g>
        <g v-for="(b, i) in c.bands" :key="'l' + i">
          <line :x1="LEFT" :x2="W - RIGHT" :y1="b.line" :y2="b.line" :class="'thr-' + b.severity" />
        </g>
        <path :d="c.path" class="line" :class="{ pain: c.v.questionId === 'pain' }" />
        <circle
          v-for="(p, i) in c.points"
          :key="i"
          :cx="p.px"
          :cy="p.py"
          r="4.5"
          class="pt"
          :class="[p.state, { pain: c.v.questionId === 'pain', note: p.source === 'note' }]"
        >
          <title>
            {{ fmtDate(p.date) }}: {{ p.value }}{{ c.v.unit ?? ''
            }}{{ p.source === 'note' ? ' (from a note)' : '' }}
          </title>
        </circle>
        <line :x1="todayX" :x2="todayX" :y1="TOP" :y2="TOP + plotH" class="today" />
      </svg>
      <p class="meta legend">
        <span v-if="c.bands.length"><i class="sw zone-ok-sw" /> normal</span>
        <span v-if="c.bands.some((b) => b.severity === 'attention')"
          ><i class="sw zone-attention-sw" /> attention zone</span
        >
        <span v-if="c.bands.some((b) => b.severity === 'urgent')"
          ><i class="sw zone-urgent-sw" /> urgent zone</span
        >
        <span v-for="(b, i) in c.bands" :key="i" class="thrlabel"
          >{{ b.severity }} {{ b.label }}</span
        >
      </p>
    </div>
    <p v-if="!charts.length" class="meta">No numeric readings yet.</p>
  </section>
</template>

<style scoped>
.trends {
  max-width: 860px;
  margin-bottom: var(--space-5);
}
.card {
  background: var(--sheet);
  border: 1px solid var(--hairline);
  border-radius: var(--radius-card);
  padding: var(--space-3) var(--space-4);
  margin-bottom: var(--space-3);
}
svg {
  width: 100%;
  height: auto;
  display: block;
}
.chart header {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: var(--space-3);
}
.latest {
  display: flex;
  align-items: baseline;
  gap: var(--space-2);
}
.value {
  font: 600 22px/28px var(--font);
}
.value.attention {
  color: var(--ground);
}
.value.urgent {
  color: var(--urgent);
}
.grid {
  stroke: var(--hairline);
  stroke-width: 1;
}
.axis,
.rowlabel {
  font: 400 11px var(--font);
  fill: var(--ink-2);
}
.rowline {
  stroke: var(--hairline);
  stroke-width: 1;
}
.today {
  stroke: var(--today);
  stroke-width: 2;
  stroke-linecap: round;
}
/* state-coloured background: normal / attention / urgent */
.zone-ok {
  fill: #e9f6f0;
}
.zone-plain {
  fill: var(--fill);
}
.zone-attention {
  fill: #dfe9fb;
}
.zone-urgent {
  fill: #f9dcde;
}
.thr-attention {
  stroke: var(--ground);
  stroke-width: 1;
  stroke-dasharray: 4 3;
}
.thr-urgent {
  stroke: var(--urgent);
  stroke-width: 1;
  stroke-dasharray: 4 3;
}
.line {
  fill: none;
  stroke: var(--ink-2);
  stroke-width: 2;
  stroke-linejoin: round;
  stroke-linecap: round;
}
.line.pain {
  stroke: var(--pain-high);
}
.pt {
  fill: var(--ink-2);
  stroke: #fff;
  stroke-width: 1.5;
}
.pt.pain {
  fill: var(--pain-high);
}
.pt.attention {
  fill: var(--ground);
}
.pt.urgent {
  fill: var(--urgent);
}
.pt.note {
  fill: #fff;
  stroke: var(--ink-2);
  stroke-width: 2;
}
.dot.checkin {
  fill: var(--ground);
}
.dot.note {
  fill: var(--ink-2);
}
.dot.photo {
  fill: var(--ink);
}
.dot.msg-patient {
  fill: var(--ink);
}
.msg-staff {
  fill: #fff;
  stroke: var(--ink);
  stroke-width: 2;
}
.dot.flag-attention {
  fill: var(--ground);
}
.dot.flag-urgent {
  fill: var(--urgent);
}
.missed {
  fill: none;
  stroke: var(--ink-2);
  stroke-width: 1.5;
  stroke-dasharray: 2 2;
  opacity: 0.7;
}
.legend {
  display: flex;
  flex-wrap: wrap;
  gap: var(--space-1) var(--space-4);
  margin: var(--space-2) 0 0;
}
.sw {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 50%;
  vertical-align: -1px;
  margin-right: 4px;
}
.sw.sq {
  border-radius: 2px;
}
.sw.checkin,
.sw.flag-attention {
  background: var(--ground);
}
.sw.note {
  background: var(--ink-2);
}
.sw.photo {
  background: var(--ink);
}
.sw.msg-patient {
  background: var(--ink);
}
.sw.ring {
  border: 1.5px dashed var(--ink-2);
}
.sw.ring.dark {
  border: 2px solid var(--ink);
}
.sw.flag-urgent {
  background: var(--urgent);
}
.sw.today-sw {
  width: 3px;
  border-radius: 2px;
  background: var(--today);
}
.sw.zone-ok-sw {
  background: #e9f6f0;
  border-radius: 3px;
  box-shadow: inset 0 0 0 1px var(--hairline);
}
.sw.zone-attention-sw {
  background: #dfe9fb;
  border-radius: 3px;
}
.sw.zone-urgent-sw {
  background: #f9dcde;
  border-radius: 3px;
}
.thrlabel {
  opacity: 0.85;
}
</style>
