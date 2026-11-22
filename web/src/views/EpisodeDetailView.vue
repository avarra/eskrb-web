<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import AuthImage from '@/components/AuthImage.vue'
import EpisodeNotes from '@/components/EpisodeNotes.vue'
import { useAuthStore } from '@/stores/auth'
import { errorMessage } from '@/services/api'
import {
  directory,
  type Escalation,
  type EpisodeRow,
  type TimelineItem,
} from '@/services/directory'

const route = useRoute()
const auth = useAuthStore()
const id = route.params.id as string

const episode = ref<EpisodeRow | null>(null)
const items = ref<TimelineItem[]>([])
const body = ref('')
const error = ref('')
const sending = ref(false)
let timer: ReturnType<typeof setInterval> | undefined

const when = (iso: string) => new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString()
const isPhoto = (v: unknown) => typeof v === 'string' && v.startsWith('/media/')
const show = (v: unknown) => (typeof v === 'object' ? JSON.stringify(v) : String(v))

async function loadTimeline() {
  try {
    items.value = await directory.timeline(id)
    escalations.value = await directory.escalations(id)
  } catch (e) {
    error.value = errorMessage(e)
  }
}

const unreviewed = computed(() => items.value.filter((i) => i.reviewed === false).length)
const reviewing = ref(false)

// --- escalation: hand an item up to the next level of the group, with an optional comment ---
const escalations = ref<Escalation[]>([])
const escalating = ref<string | null>(null) // timeline item key being escalated
const comment = ref('')
const escalationsOf = (itemId: string) => escalations.value.filter((e) => e.sourceId === itemId)

function startEscalate(i: TimelineItem) {
  escalating.value = i.kind + i.id
  comment.value = ''
}

async function escalate(i: TimelineItem) {
  error.value = ''
  try {
    await directory.escalate(id, { sourceType: i.kind, sourceId: i.id, comment: comment.value })
    escalating.value = null
    comment.value = ''
    escalations.value = await directory.escalations(id)
  } catch (e) {
    error.value = errorMessage(e)
  }
}
async function resolve(e: Escalation) {
  try {
    await directory.resolveEscalation(id, e.id)
    escalations.value = await directory.escalations(id)
  } catch (err) {
    error.value = errorMessage(err)
  }
}

async function review() {
  reviewing.value = true
  error.value = ''
  try {
    await directory.review(id)
    await loadTimeline()
  } catch (e) {
    error.value = errorMessage(e)
  } finally {
    reviewing.value = false
  }
}

/** Pain is the only coral thing: under 35% low, 35-70% mid, 70% and up high (scale 0-10). */
function painClass(v: unknown) {
  const n = Number(v) / 10
  return n < 0.35 ? 'pain-low' : n < 0.7 ? 'pain-mid' : 'pain-high'
}

async function send() {
  sending.value = true
  error.value = ''
  try {
    await directory.sendMessage(id, body.value)
    body.value = ''
    await loadTimeline()
  } catch (e) {
    error.value = errorMessage(e)
  } finally {
    sending.value = false
  }
}

onMounted(async () => {
  episode.value = (await directory.episodes()).find((e) => e.id === id) ?? null
  await loadTimeline()
  timer = setInterval(loadTimeline, 10000) // simple polling; no websockets in v0
})
onBeforeUnmount(() => clearInterval(timer))
</script>

<template>
  <RouterLink class="back" :to="{ name: 'triage' }">&larr; Triage</RouterLink>
  <header class="head">
    <div>
      <h1>{{ episode?.patient.name ?? 'Episode' }}</h1>
      <p v-if="episode" class="meta">
        {{ episode.protocol.name }} &middot; {{ episode.group.name }} &middot; {{ episode.status }}
        <template v-if="episode.status !== 'upcoming'"
          >&middot; day {{ episode.dayNumber }}</template
        >
      </p>
    </div>
    <button class="primary" :disabled="reviewing || !unreviewed" @click="review">
      Mark as reviewed<template v-if="unreviewed"> ({{ unreviewed }})</template>
    </button>
  </header>
  <p v-if="error" class="error">{{ error }}</p>

  <form class="reply" @submit.prevent="send">
    <label>
      Reply to patient
      <textarea v-model="body" rows="2" maxlength="2000" />
    </label>
    <button class="primary" :disabled="sending || !body.trim()">Send</button>
  </form>

  <EpisodeNotes :episode-id="id" />

  <h2>History</h2>
  <article
    v-for="i in items"
    :key="i.kind + i.id"
    class="entry"
    :class="[i.kind, { fresh: i.reviewed === false }]"
  >
    <div class="meta line">
      <span v-if="i.reviewed === false" class="dot" title="Not reviewed yet" />
      <strong class="title">{{
        i.kind === 'checkin'
          ? 'Check-in'
          : i.kind === 'log'
            ? i.logType === 'photo'
              ? 'Photo'
              : 'Note'
            : 'Message'
      }}</strong>
      <span>{{ when(i.at) }}</span>
      <span v-if="i.kind === 'log' && i.source">{{ i.source }}</span>
      <span v-if="i.kind === 'message'">{{ i.fromName }} ({{ i.fromRole }})</span>
      <span v-if="i.reviewed" class="chip reviewed">Reviewed</span>
    </div>

    <dl v-if="i.kind === 'checkin' && i.answers" class="answers">
      <template v-for="(v, k) in i.answers" :key="k">
        <dt>{{ k }}</dt>
        <dd>
          <AuthImage v-if="isPhoto(v)" :path="v as string" />
          <span v-else-if="k === 'pain'" class="chip" :class="painClass(v)"
            >{{ show(v) }} / 10</span
          >
          <template v-else>{{ show(v) }}</template>
        </dd>
      </template>
    </dl>
    <p v-if="i.kind === 'log' && i.text" class="quote">{{ i.text }}</p>
    <AuthImage v-if="i.kind === 'log' && i.mediaUrl" :path="i.mediaUrl" />
    <p v-if="i.kind === 'log' && i.extracted && Object.keys(i.extracted).length" class="meta">
      Extracted:
      <span v-for="(v, k) in i.extracted" :key="k" class="ext">
        {{ k }} <span v-if="k === 'pain'" class="chip" :class="painClass(v)">{{ show(v) }}</span
        ><template v-else>{{ show(v) }}</template>
      </span>
    </p>
    <p v-if="i.kind === 'message'" class="quote">{{ i.body }}</p>

    <div v-for="e in escalationsOf(i.id)" :key="e.id" class="esc" :class="{ done: e.resolved }">
      <span class="chip attention">{{ e.resolved ? 'Escalation resolved' : 'Escalated' }}</span>
      <span class="meta">{{ e.fromName }} to level {{ e.toLevel }}</span>
      <span v-if="e.comment">“{{ e.comment }}”</span>
      <button v-if="!e.resolved" class="small" @click="resolve(e)">Resolve</button>
    </div>
    <div v-if="!auth.isAdmin" class="escalate">
      <button v-if="escalating !== i.kind + i.id" class="small" @click="startEscalate(i)">
        Escalate
      </button>
      <form v-else class="esc-form" @submit.prevent="escalate(i)">
        <input
          v-model="comment"
          maxlength="500"
          placeholder="Comment for the senior doctor (optional)"
        />
        <button class="primary small">Escalate</button>
        <button type="button" class="small" @click="escalating = null">Cancel</button>
      </form>
    </div>

    <div v-for="f in i.flags ?? []" :key="f.id" class="flag">
      <span class="chip" :class="f.severity">{{
        f.severity === 'urgent' ? 'Urgent' : 'Attention'
      }}</span>
      <span class="meta">{{ f.ruleId }}</span>
      <span>“{{ f.quote }}”</span>
    </div>
  </article>
  <p v-if="!items.length" class="meta">Nothing yet.</p>
</template>

<style scoped>
.back {
  display: inline-block;
  margin-bottom: var(--space-3);
}
.head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-4);
}
.reply {
  display: flex;
  gap: var(--space-3);
  align-items: end;
  background: var(--fill);
  border-radius: var(--radius-card);
  padding: var(--space-4);
}
.reply label {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
}
textarea {
  width: 100%;
  background: var(--sheet);
}
.entry {
  border: 1px solid var(--hairline);
  border-radius: var(--radius-card);
  padding: var(--space-3) var(--space-4);
  margin-bottom: var(--space-3);
  max-width: 860px;
}
.entry.message {
  background: var(--fill);
  border-color: transparent;
}
.line {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--space-2) var(--space-3);
}
.title {
  font: 600 16px/22px var(--font);
  color: var(--ink);
}
.answers {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 4px var(--space-4);
  margin: var(--space-2) 0;
}
dt {
  font: 500 13px/22px var(--font);
  color: var(--ink-2);
}
dd {
  margin: 0;
}
.quote {
  font: 400 16px/24px var(--font);
  margin: var(--space-2) 0;
}
.ext {
  margin-right: var(--space-3);
}
.flag {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-top: var(--space-2);
}
img {
  border-radius: var(--radius-thumb);
}
.esc,
.esc-form {
  display: flex;
  align-items: baseline;
  flex-wrap: wrap;
  gap: var(--space-2);
  margin-top: var(--space-2);
}
.esc.done {
  opacity: 0.6;
}
.esc-form {
  align-items: center;
}
.esc-form input {
  flex: 1;
  min-width: 240px;
}
.escalate {
  margin-top: var(--space-2);
}
button.small {
  min-height: 32px;
  padding: 0 14px;
  font-size: 13px;
}
</style>
