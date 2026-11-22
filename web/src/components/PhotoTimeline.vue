<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { apiBlob } from '@/services/api'
import type { PhotoSeries } from '@/services/directory'

const props = defineProps<{
  series: PhotoSeries[]
  startDate: string // episode start, YYYY-MM-DD
  dayNumber: number // today's day of the episode
}>()

// Every photo is fetched up front (a few at a time) and kept as an object URL, so stepping through the
// timeline is instant and never flashes or resizes the layout.
const urls = reactive<Record<string, string>>({})
const failed = reactive<Record<string, boolean>>({})
const requested = new Set<string>()
let unmounted = false

async function preload(paths: string[]) {
  const queue = paths.filter((p) => !requested.has(p))
  queue.forEach((p) => requested.add(p))
  const worker = async () => {
    for (let p = queue.shift(); p; p = queue.shift()) {
      try {
        const url = URL.createObjectURL(await apiBlob(p))
        if (unmounted) URL.revokeObjectURL(url)
        else urls[p] = url
      } catch {
        failed[p] = true
      }
    }
  }
  await Promise.all([worker(), worker(), worker(), worker()])
}
watch(
  () => props.series.flatMap((s) => s.photos.map((p) => p.mediaUrl)),
  (paths) => void preload(paths),
  { immediate: true },
)
onBeforeUnmount(() => {
  unmounted = true
  Object.values(urls).forEach((u) => URL.revokeObjectURL(u))
})

const active = ref(props.series[0]?.questionId ?? '')
const index = ref(-1) // -1 = follow the newest photo
const compare = ref(false)

watch(
  () => props.series.map((s) => s.questionId).join(),
  () => {
    if (!props.series.some((s) => s.questionId === active.value))
      active.value = props.series[0]?.questionId ?? ''
  },
)
watch(active, () => (index.value = -1))

const current = computed(
  () => props.series.find((s) => s.questionId === active.value) ?? props.series[0],
)
const photos = computed(() => current.value?.photos ?? [])
const selected = computed(() =>
  index.value < 0 ? photos.value.length - 1 : Math.min(index.value, photos.value.length - 1),
)
const photo = computed(() => photos.value[selected.value])

const DAY = 86400000
const dayOffset = (date: string) =>
  Math.round((Date.parse(date + 'T00:00:00Z') - Date.parse(props.startDate + 'T00:00:00Z')) / DAY)
const offsets = computed(() => photos.value.map((p) => dayOffset(p.date)))
const span = computed(() => Math.max(1, props.dayNumber - 1, ...offsets.value))
const pct = (offset: number) => `${(offset / span.value) * 100}%`

const dayLabel = (p: { date: string }) => `Day ${dayOffset(p.date) + 1}`
const niceDate = (date: string) =>
  new Date(date + 'T12:00:00').toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  })

/** The slider is a day range; releasing/dragging snaps to the nearest day that has a photo. */
function onSlide(e: Event) {
  const v = Number((e.target as HTMLInputElement).value)
  let best = 0
  offsets.value.forEach((o, i) => {
    if (Math.abs(o - v) < Math.abs(offsets.value[best]! - v)) best = i
  })
  index.value = best
}
function step(delta: number) {
  index.value = Math.min(photos.value.length - 1, Math.max(0, selected.value + delta))
}
function onKey(e: KeyboardEvent) {
  if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
    e.preventDefault()
    step(e.key === 'ArrowLeft' ? -1 : 1)
  }
}
</script>

<template>
  <section v-if="series.length" class="photos">
    <header>
      <h2>Photos</h2>
      <div class="tabs">
        <button
          v-for="s in series"
          :key="s.questionId"
          :class="{ on: s.questionId === active }"
          @click="active = s.questionId"
        >
          {{ s.title }} <span class="count">{{ s.photos.length }}</span>
        </button>
      </div>
    </header>

    <div v-if="photo" class="viewer">
      <!-- fixed-size frames: the layout never shifts, whatever is loading or selected -->
      <div class="stage" :class="{ two: compare && selected > 0 }">
        <figure v-if="compare && selected > 0">
          <div class="frame">
            <img
              v-if="urls[photos[0]!.mediaUrl]"
              :src="urls[photos[0]!.mediaUrl]"
              alt="First photo"
            />
            <span v-else class="meta">{{
              failed[photos[0]!.mediaUrl] ? 'Photo unavailable' : 'Loading…'
            }}</span>
          </div>
          <figcaption class="meta">
            First: {{ dayLabel(photos[0]!) }} &middot; {{ niceDate(photos[0]!.date) }}
          </figcaption>
        </figure>
        <figure>
          <div class="frame">
            <img
              v-if="urls[photo.mediaUrl]"
              :src="urls[photo.mediaUrl]"
              :alt="`Photo, ${dayLabel(photo)}`"
            />
            <span v-else class="meta">{{
              failed[photo.mediaUrl] ? 'Photo unavailable' : 'Loading…'
            }}</span>
          </div>
          <figcaption class="meta">
            <strong>{{ dayLabel(photo) }}</strong> &middot; {{ niceDate(photo.date) }} &middot;
            {{ selected + 1 }} of {{ photos.length }}
          </figcaption>
        </figure>
      </div>

      <div class="controls">
        <button class="small" :disabled="selected <= 0" @click="step(-1)">&larr; Earlier</button>
        <button
          class="small"
          :class="{ primary: compare }"
          :disabled="photos.length < 2"
          @click="compare = !compare"
        >
          Compare with first
        </button>
        <button class="small" :disabled="selected >= photos.length - 1" @click="step(1)">
          Later &rarr;
        </button>
      </div>

      <div class="timeline">
        <div class="track">
          <span
            v-for="(o, i) in offsets"
            :key="i"
            class="dot"
            :class="{ sel: i === selected }"
            :style="{ left: pct(o) }"
          />
          <span class="today" :style="{ left: pct(Math.max(0, dayNumber - 1)) }" title="Today" />
          <input
            type="range"
            min="0"
            :max="span"
            step="1"
            :value="offsets[selected]"
            aria-label="Photo timeline"
            @input="onSlide"
            @keydown="onKey"
          />
        </div>
        <div class="ends meta">
          <span>Day 1 &middot; {{ niceDate(startDate) }}</span>
          <span>Day {{ span + 1 }}</span>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.photos {
  max-width: 860px;
  margin-bottom: var(--space-5);
}
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-3);
  flex-wrap: wrap;
}
.tabs {
  display: flex;
  gap: var(--space-2);
  flex-wrap: wrap;
}
.tabs button {
  min-height: 34px;
  padding: 0 14px;
  font-size: 14px;
}
.tabs button.on {
  background: var(--ground);
  border-color: var(--ground);
  color: #fff;
  font-weight: 600;
}
.count {
  opacity: 0.75;
  margin-left: 4px;
}
.viewer {
  background: var(--fill);
  border-radius: var(--radius-card);
  padding: var(--space-4);
}
.stage {
  display: flex;
  gap: var(--space-3);
  justify-content: center;
}
.stage figure {
  margin: 0;
  flex: 0 1 420px;
  min-width: 0;
}
.stage.two figure {
  flex: 1 1 0;
  max-width: 420px;
}
.frame {
  height: clamp(320px, 58vh, 560px);
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--sheet);
  border-radius: 12px;
  overflow: hidden;
}
.frame img {
  width: 100%;
  height: 100%;
  object-fit: contain;
  display: block;
}
figcaption {
  margin-top: var(--space-2);
  min-height: 18px;
  text-align: center;
}
.controls {
  display: flex;
  justify-content: space-between;
  gap: var(--space-2);
  margin: var(--space-3) 0;
}
.timeline {
  padding: 0 10px;
}
.track {
  position: relative;
  height: 36px;
}
.track::before {
  content: '';
  position: absolute;
  left: 0;
  right: 0;
  top: 17px;
  height: 2px;
  background: var(--hairline);
  border-radius: 1px;
}
.dot {
  position: absolute;
  top: 12px;
  width: 12px;
  height: 12px;
  margin-left: -6px;
  border-radius: 50%;
  background: var(--ground);
  box-shadow: 0 0 0 2px var(--fill);
  pointer-events: none;
}
.dot.sel {
  top: 9px;
  width: 18px;
  height: 18px;
  margin-left: -9px;
  box-shadow:
    0 0 0 3px var(--fill),
    0 0 0 5px var(--ground);
}
.today {
  position: absolute;
  top: 25px;
  width: 8px;
  height: 8px;
  margin-left: -4px;
  border-radius: 50%;
  background: var(--today);
  pointer-events: none;
}
.track input[type='range'] {
  position: absolute;
  inset: 0;
  width: calc(100% + 0px);
  margin: 0;
  opacity: 0; /* the dots are the visible control; the native range provides drag + a11y */
  cursor: pointer;
  min-height: 0;
  background: transparent;
  border: 0;
  padding: 0;
}
.ends {
  display: flex;
  justify-content: space-between;
}
</style>
