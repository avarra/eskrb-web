<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import NoteForm from '@/components/NoteForm.vue'
import { errorMessage } from '@/services/api'
import { directory, type Note, type NoteKind } from '@/services/directory'

const props = defineProps<{ episodeId: string }>()

const notes = ref<Note[]>([])
const adding = ref(false)
const editing = ref<string | null>(null)
const error = ref('')

const groups: { kind: NoteKind; title: string; hint: string }[] = [
  {
    kind: 'medication',
    title: 'Medication instructions',
    hint: 'Shown to the patient. Adds a "took your meds?" question to the check-in.',
  },
  { kind: 'advice', title: 'Advice', hint: 'Shown to the patient.' },
  { kind: 'private', title: 'Private', hint: 'Doctors only. The patient never sees this.' },
]
const byKind = computed(() =>
  Object.fromEntries(groups.map((g) => [g.kind, notes.value.filter((n) => n.kind === g.kind)])),
)

async function load() {
  try {
    notes.value = await directory.notes(props.episodeId)
  } catch (e) {
    error.value = errorMessage(e)
  }
}
async function saved() {
  adding.value = false
  editing.value = null
  await load()
}
async function remove(n: Note) {
  if (!confirm('Delete this note?')) return
  try {
    await directory.deleteNote(props.episodeId, n.id)
    await load()
  } catch (e) {
    error.value = errorMessage(e)
  }
}
onMounted(load)
</script>

<template>
  <section class="notes">
    <header>
      <h2>Notes</h2>
      <button v-if="!adding" @click="adding = true">Add note</button>
    </header>
    <p v-if="error" class="error">{{ error }}</p>
    <div v-if="adding" class="panel">
      <NoteForm :episode-id="episodeId" @saved="saved" @cancel="adding = false" />
    </div>

    <template v-for="g in groups" :key="g.kind">
      <div v-if="byKind[g.kind]!.length" class="group">
        <h3>{{ g.title }}</h3>
        <p class="meta">{{ g.hint }}</p>
        <div v-for="n in byKind[g.kind]" :key="n.id" class="note" :class="g.kind">
          <NoteForm
            v-if="editing === n.id"
            :episode-id="episodeId"
            :note="n"
            @saved="saved"
            @cancel="editing = null"
          />
          <template v-else>
            <strong v-if="n.title">{{ n.title }}</strong>
            <p class="body">{{ n.body }}</p>
            <div class="meta actions">
              <span>Updated {{ new Date(n.updatedAt + 'Z').toLocaleString() }}</span>
              <button @click="editing = n.id">Edit</button>
              <button @click="remove(n)">Delete</button>
            </div>
          </template>
        </div>
      </div>
    </template>
    <p v-if="!notes.length && !adding" class="meta">No notes yet.</p>
  </section>
</template>

<style scoped>
.notes {
  max-width: 860px;
  margin-bottom: var(--space-5);
}
header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
h3 {
  margin: var(--space-4) 0 0;
  font: 600 16px/22px var(--font);
}
.panel,
.note {
  border: 1px solid var(--hairline);
  border-radius: var(--radius-card);
  padding: var(--space-3) var(--space-4);
  margin-top: var(--space-2);
}
.panel,
.note.private {
  background: var(--fill);
  border-color: transparent;
}
.body {
  white-space: pre-wrap;
  font-size: 16px;
  line-height: 24px;
  margin: 4px 0;
}
.actions {
  display: flex;
  align-items: center;
  gap: var(--space-2);
}
.actions span {
  flex: 1;
}
.actions button {
  min-height: 32px;
  padding: 0 14px;
  font-size: 13px;
}
</style>
