<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import AppModal from '@/components/AppModal.vue'
import { errorMessage } from '@/services/api'
import {
  directory,
  type BuddyCandidate,
  type BuddyPair,
  type BuddyThreadMessage,
  type EpisodeRow,
} from '@/services/directory'

const pairs = ref<BuddyPair[]>([])
const episodes = ref<EpisodeRow[]>([])
const error = ref('')

// new pair
const creating = ref(false)
const episodeA = ref('')
const candidates = ref<BuddyCandidate[]>([])
const episodeB = ref('')
const consent = ref(false)

// thread
const viewing = ref<BuddyPair | null>(null)
const thread = ref<BuddyThreadMessage[]>([])

const activeEpisodes = computed(() => episodes.value.filter((e) => e.status === 'active'))
const when = (iso: string) => new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString()
const endText = (p: BuddyPair) =>
  p.endReason === 'left'
    ? 'A patient left'
    : p.endReason === 'staff'
      ? 'Unpaired by staff'
      : 'Episode ended'

async function load() {
  try {
    ;[pairs.value, episodes.value] = await Promise.all([directory.buddies(), directory.episodes()])
  } catch (e) {
    error.value = errorMessage(e)
  }
}

function openCreate() {
  episodeA.value = ''
  episodeB.value = ''
  candidates.value = []
  consent.value = false
  error.value = ''
  creating.value = true
}
watch(episodeA, async (id) => {
  episodeB.value = ''
  candidates.value = id ? await directory.buddyCandidates(id) : []
})

async function create() {
  error.value = ''
  try {
    await directory.createBuddyPair({
      episodeAId: episodeA.value,
      episodeBId: episodeB.value,
      consentConfirmed: consent.value,
    })
    creating.value = false
    await load()
  } catch (e) {
    error.value = errorMessage(e)
  }
}

async function unpair(p: BuddyPair) {
  if (!confirm(`Unpair ${p.a.patientName} and ${p.b.patientName}? Their thread closes.`)) return
  try {
    await directory.unpairBuddies(p.id)
    await load()
  } catch (e) {
    error.value = errorMessage(e)
  }
}

async function openThread(p: BuddyPair) {
  viewing.value = p
  thread.value = await directory.buddyThread(p.id)
}
async function hide(m: BuddyThreadMessage) {
  await directory.hideBuddyMessage(viewing.value!.id, m.id)
  thread.value = await directory.buddyThread(viewing.value!.id)
  await load()
}

onMounted(load)
</script>

<template>
  <header class="page-head">
    <h1>Buddies</h1>
    <button class="primary" @click="openCreate">New pair</button>
  </header>
  <p class="meta">
    Recovery twins: two patients on the same protocol who see each other's first name, recovery day
    and check-in streak, and can send cheers and short messages. Staff can read the threads.
  </p>
  <p v-if="error && !creating" class="error">{{ error }}</p>

  <table>
    <thead>
      <tr>
        <th>Pair</th>
        <th>Protocol</th>
        <th>Paired by</th>
        <th>Status</th>
        <th>Messages</th>
        <th></th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="p in pairs" :key="p.id" :class="{ inactive: p.status === 'ended' }">
        <td>
          {{ p.a.patientName }} (day {{ p.a.dayNumber }}) &harr; {{ p.b.patientName }} (day
          {{ p.b.dayNumber }})
        </td>
        <td>{{ p.protocolName }}</td>
        <td>
          {{ p.createdByName }}<br /><span class="meta">{{ when(p.createdAt) }}</span>
        </td>
        <td>
          <span class="chip">{{ p.status === 'active' ? 'Active' : endText(p) }}</span>
        </td>
        <td>
          {{ p.messages }}
          <span v-if="p.reported" class="chip attention">{{ p.reported }} reported</span>
        </td>
        <td>
          <button class="small" @click="openThread(p)">View thread</button>
          <button v-if="p.status === 'active'" class="small" @click="unpair(p)">Unpair</button>
        </td>
      </tr>
      <tr v-if="!pairs.length">
        <td colspan="6">No pairs yet.</td>
      </tr>
    </tbody>
  </table>

  <AppModal :open="creating" title="New buddy pair" @close="creating = false">
    <form class="modal-form" @submit.prevent="create">
      <label>
        First patient
        <select v-model="episodeA">
          <option value="">select…</option>
          <option v-for="e in activeEpisodes" :key="e.id" :value="e.id">
            {{ e.patient.name }} (day {{ e.dayNumber }}, {{ e.protocol.name }})
          </option>
        </select>
      </label>
      <label>
        Partner (same protocol, no buddy yet, closest start date first)
        <select v-model="episodeB" :disabled="!episodeA">
          <option value="">select…</option>
          <option v-for="c in candidates" :key="c.episodeId" :value="c.episodeId">
            {{ c.patientName }} (day {{ c.dayNumber }}, {{ c.groupName }})
          </option>
        </select>
      </label>
      <p v-if="episodeA && !candidates.length" class="meta">No free partner on this protocol.</p>
      <label class="check">
        <input v-model="consent" type="checkbox" />
        Both patients agreed to be paired and know that staff can read their messages.
      </label>
      <p v-if="error" class="error">{{ error }}</p>
      <div class="actions">
        <button class="primary" :disabled="!episodeA || !episodeB || !consent">Pair them</button>
        <button type="button" @click="creating = false">Cancel</button>
      </div>
    </form>
  </AppModal>

  <AppModal
    :open="!!viewing"
    :title="viewing ? `${viewing.a.patientName} & ${viewing.b.patientName}` : ''"
    @close="viewing = null"
  >
    <p class="meta">Peer chat between patients. Hide a message that should not be there.</p>
    <div v-for="m in thread" :key="m.id" class="msg" :class="{ hidden: m.hidden }">
      <div class="meta">
        <strong>{{ m.fromName }}</strong> &middot; {{ when(m.createdAt) }}
        <template v-if="m.kind === 'cheer'"> &middot; cheer</template>
        <template v-else-if="m.source"> &middot; {{ m.source }}</template>
        <span v-if="m.reported" class="chip attention">Reported</span>
        <span v-if="m.hidden" class="chip">Hidden</span>
      </div>
      <p>{{ m.text }}</p>
      <button v-if="!m.hidden" class="small" @click="hide(m)">Hide</button>
    </div>
    <p v-if="!thread.length" class="meta">No messages yet.</p>
  </AppModal>
</template>

<style scoped>
.check {
  flex-direction: row !important;
  align-items: flex-start;
  gap: var(--space-2) !important;
  font: 400 15px/22px var(--font) !important;
  color: var(--ink) !important;
}
.check input {
  min-height: 0;
  margin-top: 4px;
  accent-color: var(--ground);
}
.msg {
  border: 1px solid var(--hairline);
  border-radius: 16px;
  padding: var(--space-2) var(--space-3);
  margin-bottom: var(--space-2);
}
.msg p {
  margin: 4px 0;
}
.msg.hidden {
  opacity: 0.5;
}
</style>
