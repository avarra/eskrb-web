<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { directory, type ProtocolSummary } from '@/services/directory'

const protocols = ref<ProtocolSummary[]>([])
onMounted(async () => (protocols.value = await directory.protocols()))
</script>

<template>
  <h1>Protocols</h1>
  <table>
    <thead>
      <tr>
        <th>Protocol</th>
        <th>Days</th>
        <th>Questions</th>
        <th>Rules</th>
        <th>Episodes</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="p in protocols" :key="p.id">
        <td>
          <RouterLink :to="{ name: 'protocol', params: { id: p.id } }">{{ p.name }}</RouterLink>
        </td>
        <td>{{ p.durationDays }}</td>
        <td>{{ p.questionCount }}</td>
        <td>{{ p.ruleCount }}</td>
        <td>
          <RouterLink :to="{ name: 'episodes', query: { protocolId: p.id } }">{{
            p.episodeCount
          }}</RouterLink>
        </td>
      </tr>
    </tbody>
  </table>
</template>
