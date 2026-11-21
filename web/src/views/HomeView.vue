<script setup lang="ts">
import { useAuthStore } from '@/stores/auth'

const auth = useAuthStore()
</script>

<template>
  <main v-if="auth.user">
    <h1>Hello, {{ auth.user.name }}</h1>
    <p>Role: {{ auth.user.role }}</p>
    <p v-if="!auth.isStaff">This dashboard is for staff accounts.</p>
  </main>
</template>
