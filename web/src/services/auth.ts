import { api, tokenStorage } from './api'

export type Role = 'patient' | 'caretaker' | 'staff' | 'admin'

export interface Me {
  id: string
  name: string
  role: Role
}

export const authService = {
  /** Validates a token by calling /me with it explicitly (does not persist it). */
  getMe: (token?: string) => api<Me>('/me', { token }),
  getStoredToken: () => tokenStorage.get(),
  storeToken: (token: string) => tokenStorage.set(token),
  clearToken: () => tokenStorage.clear(),
}
