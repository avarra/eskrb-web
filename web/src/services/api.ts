const BASE = '/api'
const TOKEN_KEY = 'eskrb.token'

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message)
  }
}

export const tokenStorage = {
  get(): string | null {
    try {
      return localStorage.getItem(TOKEN_KEY)
    } catch {
      return null
    }
  },
  set(token: string) {
    try {
      localStorage.setItem(TOKEN_KEY, token)
    } catch {
      /* storage unavailable: session-only */
    }
  },
  clear() {
    try {
      localStorage.removeItem(TOKEN_KEY)
    } catch {
      /* ignore */
    }
  },
}

let onUnauthorized: (() => void) | null = null
/** Registered by the auth store so a 401 anywhere logs the user out. */
export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn
}

export async function api<T>(
  path: string,
  init: RequestInit & { token?: string } = {},
): Promise<T> {
  const { token = tokenStorage.get(), headers, ...rest } = init
  const res = await fetch(BASE + path, {
    ...rest,
    headers: {
      Accept: 'application/json',
      ...(rest.body && !(rest.body instanceof FormData)
        ? { 'Content-Type': 'application/json' }
        : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...headers,
    },
  })

  if (res.status === 401) onUnauthorized?.()
  if (!res.ok) throw new ApiError(res.status, (await res.text().catch(() => '')) || res.statusText)
  return res.status === 204 ? (undefined as T) : ((await res.json()) as T)
}

/** Authorized binary fetch (e.g. photos): <img src> can't send the bearer header, so fetch and use an object URL. */
export async function apiBlob(path: string): Promise<Blob> {
  const token = tokenStorage.get()
  const res = await fetch(BASE + path, {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
  })
  if (res.status === 401) onUnauthorized?.()
  if (!res.ok) throw new ApiError(res.status, res.statusText)
  return res.blob()
}

/** Human-readable message from an ApiError (RFC 9457 problem details) or any error. */
export function errorMessage(e: unknown): string {
  if (e instanceof ApiError) {
    try {
      const p = JSON.parse(e.message)
      return p.detail ?? p.title ?? e.message
    } catch {
      return e.message || `Request failed (${e.status})`
    }
  }
  return e instanceof Error ? e.message : String(e)
}
