<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { directory, type Patient } from '@/services/directory'

const patients = ref<Patient[]>([])
onMounted(async () => (patients.value = await directory.patients()))
</script>

<template>
  <h1>Patients</h1>
  <table>
    <thead>
      <tr>
        <th>Patient</th>
        <th>Episodes</th>
      </tr>
    </thead>
    <tbody>
      <tr v-for="p in patients" :key="p.id">
        <td>{{ p.name }}</td>
        <td>
          <RouterLink :to="{ name: 'episodes', query: { patientId: p.id } }">{{
            p.episodeCount
          }}</RouterLink>
        </td>
      </tr>
    </tbody>
  </table>
</template>
