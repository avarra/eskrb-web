<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import AppModal from '@/components/AppModal.vue'
import NoteForm from '@/components/NoteForm.vue'
import { errorMessage } from '@/services/api'
import { useAuthStore } from '@/stores/auth'
import { directory, reviewStatusLabel, type Triage, type TriageCard } from '@/services/directory'

const auth = useAuthStore()
const triage = ref<Triage | null>(null)
const escalating = ref<TriageCard | null>(null)
const comment = ref('')

function startEscalate(c: TriageCard) {
  escalating.value = c
  comment.value = ''
}

async function escalate() {
  const c = escalating.value!
  try {
    await directory.escalate(c.episodeId, {
      sourceType: 'episode',
      sourceId: c.episodeId,
      comment: comment.value,
    })
    escalating.value = null
    comment.value = ''
    await load()
  } catch (e) {
    error.value = errorMessage(e)
  }
}
const error = ref('')
const noting = ref<TriageCard | null>(null) // card whose quick-note modal is open
let timer: ReturnType<typeof setInterval> | undefined

const columns = computed(() => {
  const t = triage.value
  return [
    {
      key: 'needsAttention',
      title: 'Needs attention',
      hint: 'Open flags, urgent first',
      cards: t?.needsAttention ?? [],
    },
    {
      key: 'missedCheckIn',
      title: 'Missed check-in',
      hint: 'Nothing submitted today',
      cards: t?.missedCheckIn ?? [],
    },
    {
      key: 'awaitingPatientReply',
      title: 'Awaiting reply',
      hint: 'Last message is from staff',
      cards: t?.awaitingPatientReply ?? [],
    },
    { key: 'stable', title: 'Stable', hint: 'Nothing to do', cards: t?.stable ?? [] },
  ]
})

const when = (iso: string) => new Date(iso.endsWith('Z') ? iso : iso + 'Z').toLocaleString()
const lastCheckIn = (c: TriageCard) =>
  c.lastCheckInDate ? `Last check-in ${c.lastCheckInDate}` : 'No check-ins yet'

async function load() {
  try {
    triage.value = await directory.triage()
    error.value = ''
  } catch (e) {
    error.value = errorMessage(e)
  }
}

onMounted(() => {
  load()
  timer = setInterval(load, 10000) // polling, no websockets in v0
})
onBeforeUnmount(() => clearInterval(timer))
</script>

<template>
  <h1>Triage</h1>
  <p v-if="error" class="error">{{ error }}</p>

  <div class="board">
    <section v-for="col in columns" :key="col.key" class="col" :class="col.key">
      <header>
        <h2>{{ col.title }}</h2>
        <span class="count">{{ col.cards.length }}</span>
      </header>
      <p class="meta hint">{{ col.hint }}</p>

      <article
        v-for="c in col.cards"
        :key="c.episodeId"
        class="card"
        :class="{ urgent: c.topFlag?.severity === 'urgent' }"
      >
        <RouterLink class="name" :to="{ name: 'episode', params: { id: c.episodeId } }">{{
          c.patientName
        }}</RouterLink>
        <div class="meta">
          Day {{ c.dayNumber }} &middot; {{ c.protocolName }} &middot; {{ c.groupName }}
        </div>

        <template v-if="c.topFlag">
          <div class="flagline">
            <span class="chip" :class="c.topFlag.severity">{{
              c.topFlag.severity === 'urgent' ? 'Urgent' : 'Attention'
            }}</span>
            <span class="meta"
              >{{ c.topFlag.ruleId
              }}<template v-if="c.openFlags > 1"> +{{ c.openFlags - 1 }} more</template></span
            >
          </div>
          <blockquote>“{{ c.topFlag.quote }}”</blockquote>
        </template>

        <div v-if="c.missedCheckIn" class="meta marker"><span class="dot" /> No check-in today</div>
        <div v-if="c.lastMessageFromStaff && c.lastMessageAt" class="meta">
          You wrote {{ when(c.lastMessageAt) }}
        </div>
        <div v-if="c.escalation" class="esc">
          <span class="chip attention">Escalated to level {{ c.escalation.toLevel }}</span>
          <span class="meta">by {{ c.escalation.fromName }}</span>
          <blockquote v-if="c.escalation.comment">“{{ c.escalation.comment }}”</blockquote>
        </div>
        <div v-if="c.lastReview" class="meta reviewed-line">
          <span class="chip reviewed">{{ reviewStatusLabel[c.lastReview.status] }}</span>
          {{ c.lastReview.reviewerName
          }}<template v-if="c.lastReview.comment">: “{{ c.lastReview.comment }}”</template>
        </div>
        <div class="meta">{{ lastCheckIn(c) }}</div>
        <div class="quick">
          <RouterLink :to="{ name: 'episode', params: { id: c.episodeId } }">Open</RouterLink>
          <span class="btns">
            <button v-if="!auth.isAdmin" class="small" @click="startEscalate(c)">Escalate</button>
            <button class="small" @click="noting = c">Add note</button>
          </span>
        </div>
      </article>

      <p v-if="!col.cards.length" class="meta empty">No one here.</p>
    </section>
  </div>

  <AppModal
    :open="!!noting"
    :title="`Add note: ${noting?.patientName ?? ''}`"
    @close="noting = null"
  >
    <NoteForm
      v-if="noting"
      :episode-id="noting.episodeId"
      @saved="noting = null"
      @cancel="noting = null"
    />
  </AppModal>

  <AppModal
    :open="!!escalating"
    :title="`Escalate: ${escalating?.patientName ?? ''}`"
    @close="escalating = null"
  >
    <form class="modal-form" @submit.prevent="escalate">
      <p class="meta">
        The whole episode moves to the next level above you in the group, into their needs
        attention.
      </p>
      <label>
        Comment (optional)
        <textarea v-model="comment" rows="3" maxlength="500" />
      </label>
      <div class="actions">
        <button class="primary">Escalate</button>
        <button type="button" @click="escalating = null">Cancel</button>
      </div>
    </form>
  </AppModal>
</template>

<style scoped>
.btns {
  display: flex;
  gap: var(--space-2);
}
.reviewed-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px 8px;
}
.board {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: var(--space-4);
  align-items: start;
}
.col {
  background: var(--fill);
  border-radius: var(--radius-card);
  padding: var(--space-4);
  min-height: 160px;
}
.col header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.col h2 {
  margin: 0;
}
.count {
  font: 600 13px/18px var(--font);
  background: var(--sheet);
  border-radius: 999px;
  padding: 2px 10px;
}
.hint {
  margin: 2px 0 var(--space-3);
}
.card {
  background: var(--sheet);
  border-radius: 16px;
  padding: var(--space-3) var(--space-4);
  margin-bottom: var(--space-3);
  box-shadow: var(--shadow-object);
  display: flex;
  flex-direction: column;
  gap: 4px;
}
.card.urgent {
  box-shadow:
    var(--shadow-object),
    inset 4px 0 0 var(--urgent);
}
.name {
  font: 600 17px/24px var(--font);
}
.flagline {
  display: flex;
  align-items: center;
  gap: var(--space-2);
  margin-top: var(--space-1);
}
blockquote {
  margin: 0;
  font: 400 15px/22px var(--font);
}
.marker {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--ink);
  font-weight: 500;
}
.esc {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  margin-top: var(--space-1);
}
.quick {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: var(--space-2);
}
button.small {
  min-height: 32px;
  padding: 0 14px;
  font-size: 13px;
}
.empty {
  text-align: center;
  padding: var(--space-4) 0;
}
@media (max-width: 1180px) {
  .board {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
@media (max-width: 620px) {
  .board {
    grid-template-columns: 1fr;
  }
}
</style>
