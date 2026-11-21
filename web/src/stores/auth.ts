import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { authService, type Me } from '@/services/auth'
import { setUnauthorizedHandler } from '@/services/api'

export const useAuthStore = defineStore('auth', () => {
  const user = ref<Me | null>(null)
  const isAuthenticated = computed(() => user.value !== null)
  const isAdmin = computed(() => user.value?.role === 'admin')
  /** Staff and admins may use the dashboard. */
  const isStaff = computed(() => user.value?.role === 'staff' || isAdmin.value)

  /** Try the stored token on app start. */
  async function restore() {
    const token = authService.getStoredToken()
    if (!token) return
    try {
      user.value = await authService.getMe(token)
    } catch {
      authService.clearToken()
      user.value = null
    }
  }

  /** Throws ApiError(401) on a bad token. Persists the token only if valid. */
  async function login(token: string) {
    const me = await authService.getMe(token.trim())
    authService.storeToken(token.trim())
    user.value = me
  }

  function logout() {
    authService.clearToken()
    user.value = null
  }

  setUnauthorizedHandler(logout)

  return { user, isAuthenticated, isStaff, isAdmin, restore, login, logout }
})
