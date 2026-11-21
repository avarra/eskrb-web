<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import AuthImage from '@/components/AuthImage.vue'
import { errorMessage } from '@/services/api'
import { directory, type EpisodeRow, type TimelineItem } from '@/services/directory'

const route = useRoute()
const id = route.params.id as string

const episode = ref<EpisodeRow | null>(null)
const items = ref<TimelineItem[]>([])
const body = ref('')
const error = ref('')
const sending = ref(false)
let timer: ReturnType<typeof setInterval> | undefined

const when = (iso: string) => new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString()
const show = (v: unknown) => (typeof v === 'object' ? JSON.stringify(v) : String(v))

async function loadTimeline() {
  try {
    items.value = await directory.timeline(id)
  } catch (e) {
    error.value = errorMessage(e)
  }
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
  <RouterLink :to="{ name: 'episodes' }">&larr; Episodes</RouterLink>
  <h1>{{ episode?.patient.name ?? 'Episode' }}</h1>
  <p v-if="episode">
    {{ episode.protocol.name }} &middot; {{ episode.group.name }} &middot; {{ episode.status }}
    <template v-if="episode.status !== 'upcoming'">(day {{ episode.dayNumber }})</template>
  </p>
  <p v-if="error" class="error">{{ error }}</p>

  <form class="filters" @submit.prevent="send">
    <label style="flex: 1">
      Reply to patient
      <textarea v-model="body" rows="2" maxlength="2000" />
    </label>
    <button :disabled="sending || !body.trim()">Send</button>
  </form>

  <div v-for="i in items" :key="i.kind + i.id" class="entry" :class="i.kind">
    <div class="meta">
      <strong>{{
        i.kind === 'checkin'
          ? 'Check-in'
          : i.kind === 'log'
            ? i.logType === 'photo'
              ? 'Photo'
              : 'Note'
            : 'Message'
      }}</strong>
      &middot; {{ when(i.at) }}
      <template v-if="i.kind === 'log' && i.source"> &middot; {{ i.source }}</template>
      <template v-if="i.kind === 'message'"> &middot; {{ i.fromName }} ({{ i.fromRole }})</template>
      <template v-if="i.reviewed !== null">
        &middot; {{ i.reviewed ? 'reviewed' : 'not reviewed' }}</template
      >
    </div>

    <ul v-if="i.kind === 'checkin' && i.answers" class="answers">
      <li v-for="(v, k) in i.answers" :key="k">{{ k }}: {{ show(v) }}</li>
    </ul>
    <p v-if="i.kind === 'log' && i.text">{{ i.text }}</p>
    <AuthImage v-if="i.kind === 'log' && i.mediaUrl" :path="i.mediaUrl" />
    <p v-if="i.kind === 'log' && i.extracted && Object.keys(i.extracted).length" class="meta">
      extracted: <span v-for="(v, k) in i.extracted" :key="k">{{ k }}={{ show(v) }} </span>
    </p>
    <p v-if="i.kind === 'message'">{{ i.body }}</p>

    <div v-for="f in i.flags ?? []" :key="f.id" class="flag" :class="f.severity">
      {{ f.severity.toUpperCase() }} &middot; {{ f.ruleId }} &middot; “{{ f.quote }}”
    </div>
  </div>
  <p v-if="!items.length">Nothing yet.</p>
</template>

<style scoped>
.entry {
  border-bottom: 1px solid var(--color-border);
  padding: 0.6rem 0;
}
.entry.message {
  background: rgba(127, 127, 127, 0.07);
  padding-left: 0.6rem;
}
.meta {
  font-size: 0.85rem;
  opacity: 0.75;
}
.answers {
  margin: 0.3rem 0;
  columns: 2;
}
.flag {
  display: inline-block;
  margin: 0.2rem 0.4rem 0 0;
  padding: 0.1rem 0.5rem;
  border-radius: 4px;
  font-size: 0.85rem;
}
.flag.attention {
  background: #f5a62340;
}
.flag.urgent {
  background: #c0392b40;
}
textarea {
  width: 100%;
}
</style>
