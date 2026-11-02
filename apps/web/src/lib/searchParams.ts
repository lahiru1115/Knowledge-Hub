/**
 * Readable URLs for the router's search parameters (Doc 03 §5.2): `/library?q=pool&type=video&type=pdf&tag=postgres`
 * instead of the default JSON blobs. Repeated keys become arrays; everything else stays a string and is turned
 * into the right type by each route's zod schema.
 */
export function parseSearch(search: string): Record<string, unknown> {
  const params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search)
  const out: Record<string, unknown> = {}
  for (const key of new Set(params.keys())) {
    const values = params.getAll(key)
    out[key] = values.length > 1 ? values : values[0]
  }
  return out
}

export function stringifySearch(search: Record<string, unknown>): string {
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(search)) {
    if (value === undefined || value === null || value === '') continue
    for (const v of Array.isArray(value) ? (value as unknown[]) : [value]) {
      if (v === undefined || v === null || v === '') continue
      params.append(key, typeof v === 'string' ? v : JSON.stringify(v))
    }
  }
  const text = params.toString()
  return text ? `?${text}` : ''
}
