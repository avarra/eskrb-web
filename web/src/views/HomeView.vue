<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
</script>

<template>
  <div v-if="auth.user">
    <h1>Hello, {{ auth.user.name }}</h1>
    <p class="meta">Signed in as {{ auth.user.role }}.</p>
    <p v-if="!auth.isStaff">This dashboard is for staff accounts. Patients use the phone app.</p>
  </div>
</template>
