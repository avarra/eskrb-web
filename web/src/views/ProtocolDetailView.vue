<script setup lang="ts">
import { ref, watchEffect } from 'vue'
import { useRoute } from 'vue-router'
import { directory, type ProtocolDetail, type ProtocolQuestion } from '@/services/directory'

const route = useRoute()
const protocol = ref<ProtocolDetail | null>(null)

watchEffect(async () => {
  protocol.value = await directory.protocol(route.params.id as string)
})

function range(q: ProtocolQuestion) {
  if (q.type === 'choice' || q.type === 'multichoice')
    return (q.options ?? []).map((o) => q.labels?.[o] ?? o).join(' / ')
  if (q.min !== null || q.max !== null)
    return `${q.min ?? ''}–${q.max ?? ''}${q.unit ? ' ' + q.unit : ''}`
  return q.unit ?? ''
}
</script>

<template>
  <RouterLink :to="{ name: 'protocols' }">&larr; Protocols</RouterLink>
  <template v-if="protocol">
    <h1>{{ protocol.name }}</h1>
    <p>{{ protocol.durationDays }} days &middot; {{ protocol.episodes.length }} episodes</p>

    <h2>Questions</h2>
    <table>
      <thead>
        <tr>
          <th>Id</th>
          <th>Type</th>
          <th>Range / options</th>
          <th>Text (patient-facing)</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="q in protocol.questions" :key="q.id">
          <td>{{ q.id }}</td>
          <td>{{ q.type }}{{ q.required ? ' (required)' : '' }}</td>
          <td>{{ range(q) }}</td>
          <td>
            {{ q.text }}
            <div v-if="q.showIf" class="meta">
              only if <code>{{ q.showIf.question }} {{ q.showIf.op }} {{ q.showIf.value }}</code>
            </div>
            <div v-if="q.immediateAlert" class="meta">
              safety stop on {{ q.immediateAlert.options.join(', ') }}
            </div>
          </td>
        </tr>
      </tbody>
    </table>

    <h2>Red-flag rules</h2>
    <table>
      <thead>
        <tr>
          <th>Id</th>
          <th>Condition</th>
          <th>Severity</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in protocol.rules" :key="r.id">
          <td>{{ r.id }}</td>
          <td>
            <code
              >{{ r.field }} {{ r.op }}
              {{ Array.isArray(r.value) ? r.value.join(' | ') : r.value }}</code
            >
          </td>
          <td>{{ r.severity }}</td>
        </tr>
      </tbody>
    </table>

    <h2>Episodes</h2>
    <table>
      <thead>
        <tr>
          <th>Patient</th>
          <th>Group</th>
          <th>Day</th>
          <th>Start</th>
          <th>End</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="e in protocol.episodes" :key="e.id">
          <td>{{ e.patient.name }}</td>
          <td>{{ e.group.name }}</td>
          <td>{{ e.dayNumber }}</td>
          <td>{{ e.startDate }}</td>
          <td>{{ e.endDate }}</td>
        </tr>
        <tr v-if="!protocol.episodes.length">
          <td colspan="5">No episodes.</td>
        </tr>
      </tbody>
    </table>
  </template>
</template>
