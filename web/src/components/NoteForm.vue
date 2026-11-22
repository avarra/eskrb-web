<script setup lang="ts">
import { ref } from 'vue'
import { errorMessage } from '@/services/api'
import { directory, type Note, type NoteKind } from '@/services/directory'

const props = defineProps<{ episodeId: string; note?: Note; compact?: boolean }>()
const emit = defineEmits<{ saved: []; cancel: [] }>()

const kind = ref<NoteKind>(props.note?.kind ?? 'advice')
const title = ref(props.note?.title ?? '')
const body = ref(props.note?.body ?? '')
const error = ref('')
const busy = ref(false)

async function save() {
  busy.value = true
  error.value = ''
  try {
    const b = { kind: kind.value, title: title.value, body: body.value }
    if (props.note) await directory.updateNote(props.episodeId, props.note.id, b)
    else await directory.addNote(props.episodeId, b)
    emit('saved')
  } catch (e) {
    error.value = errorMessage(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <form class="note-form" @submit.prevent="save">
    <div class="row">
      <label>
        Type
        <select v-model="kind">
          <option value="medication">Medication instructions</option>
          <option value="advice">Advice for the patient</option>
          <option value="private">Private, doctors only</option>
        </select>
      </label>
      <label v-if="!compact" class="grow">
        Title (optional)
        <input v-model="title" maxlength="100" />
      </label>
    </div>
    <label>
      Note
      <textarea v-model="body" rows="3" maxlength="2000" />
    </label>
    <p v-if="kind === 'medication'" class="meta">
      The patient's check-in will also ask whether they took their medication.
    </p>
    <p v-if="kind === 'private'" class="meta">The patient never sees this.</p>
    <p v-if="error" class="error">{{ error }}</p>
    <div class="row">
      <button class="primary" :disabled="busy || !body.trim()">Save note</button>
      <button type="button" @click="emit('cancel')">Cancel</button>
    </div>
  </form>
</template>

<style scoped>
.note-form {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
.row {
  display: flex;
  gap: var(--space-3);
  align-items: end;
  flex-wrap: wrap;
}
.grow {
  flex: 1;
}
label {
  display: flex;
  flex-direction: column;
  gap: var(--space-1);
}
textarea {
  width: 100%;
  background: var(--sheet);
}
p {
  margin: 0;
}
</style>
