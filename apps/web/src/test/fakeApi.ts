import { HttpResponse, http } from 'msw'
import type { Collection, Resource } from '@/features/library/types'
import { API, problem } from './helpers'
import { server } from './server'

export interface RecordedRequest {
  method: string
  path: string
  query: URLSearchParams
  body?: unknown
}

let counter = 0
const iso = (minutesAgo: number) => new Date(Date.now() - minutesAgo * 60_000).toISOString()

export function makeResource(over: Partial<Resource> = {}): Resource {
  counter++
  const n = counter
  return {
    id: `res-${n.toString().padStart(4, '0')}`,
    url: `https://example.com/page-${n.toString()}`,
    title: `Resource ${n.toString()}`,
    description: null,
    notes: null,
    type: 'article',
    collection_id: null,
    tags: [],
    favicon_url: null,
    image_url: null,
    site_name: null,
    summary: null,
    summary_source: 'none',
    is_favourite: false,
    is_archived: false,
    metadata_status: 'pending',
    created_at: iso(n),
    updated_at: iso(n),
    ...over,
  }
}

export function makeCollection(over: Partial<Collection> = {}): Collection {
  counter++
  return {
    id: `col-${counter.toString().padStart(4, '0')}`,
    name: `Collection ${counter.toString()}`,
    description: null,
    color: null,
    icon: null,
    is_pinned: false,
    position: counter,
    resource_count: 0,
    created_at: iso(10),
    updated_at: iso(10),
    ...over,
  }
}

export interface FakeDb {
  resources: Resource[]
  trash: Resource[]
  collections: Collection[]
  requests: RecordedRequest[]
  /** Make the next request matching `method path` fail with this problem. */
  failNext: (
    method: string,
    path: string,
    status: number,
    code: string,
    extra?: Record<string, unknown>,
  ) => void
  last: (method: string, path: string) => RecordedRequest | undefined
  all: (method: string, path: string) => RecordedRequest[]
}

/**
 * A small stateful stand-in for the library API: it filters, sorts and pages like the real one, applies edits, keeps a
 * trash, and records every request so tests can check what the app sent.
 */
export function fakeApi(seed: { resources?: Resource[]; collections?: Collection[] } = {}): FakeDb {
  const db: FakeDb = {
    resources: [...(seed.resources ?? [])],
    trash: [],
    collections: [...(seed.collections ?? [])],
    requests: [],
    failNext: () => undefined,
    last: (m, p) => [...db.requests].reverse().find((r) => r.method === m && r.path === p),
    all: (m, p) => db.requests.filter((r) => r.method === m && r.path === p),
  }
  const failures: { key: string; status: number; code: string; extra?: Record<string, unknown> }[] =
    []
  db.failNext = (method, path, status, code, extra) => {
    failures.push({ key: `${method} ${path}`, status, code, extra })
  }

  const record = async (request: Request, path: string) => {
    const body: unknown = ['POST', 'PUT', 'PATCH', 'DELETE'].includes(request.method)
      ? await request
          .clone()
          .json()
          .catch(() => undefined)
      : undefined
    db.requests.push({
      method: request.method,
      path,
      query: new URL(request.url).searchParams,
      body,
    })
  }
  const failure = (method: string, path: string) => {
    const i = failures.findIndex((f) => f.key === `${method} ${path}`)
    if (i < 0) return undefined
    const [f] = failures.splice(i, 1)
    return f ? problem(f.status, f.code, f.extra) : undefined
  }
  const withCounts = () =>
    db.collections.map((c) => ({
      ...c,
      resource_count: db.resources.filter((r) => r.collection_id === c.id).length,
    }))
  const tagCounts = () => {
    const m = new Map<string, number>()
    for (const r of db.resources) for (const t of r.tags) m.set(t, (m.get(t) ?? 0) + 1)
    return [...m.entries()].map(([name, resource_count]) => ({
      id: `tag-${name}`,
      name,
      resource_count,
    }))
  }
  const touch = (r: Resource) => ({ ...r, updated_at: new Date().toISOString() })
  const find = (id: string) => db.resources.find((r) => r.id === id)
  const replace = (r: Resource) => {
    db.resources = db.resources.map((x) => (x.id === r.id ? r : x))
  }

  server.use(
    http.get(`${API}/resources`, async ({ request }) => {
      await record(request, '/resources')
      const f = failure('GET', '/resources')
      if (f) return f
      const q = new URL(request.url).searchParams
      const text = q.get('q')?.toLowerCase()
      const types = q.getAll('type')
      const tags = q.getAll('tag')
      const col = q.get('collection_id')
      const archived = q.get('archived') === 'true'
      let items = db.resources.filter((r) => r.is_archived === archived)
      if (text)
        items = items.filter((r) =>
          [r.title, r.url, r.notes, ...r.tags].join(' ').toLowerCase().includes(text),
        )
      if (types.length) items = items.filter((r) => types.includes(r.type))
      if (tags.length) items = items.filter((r) => tags.every((t) => r.tags.includes(t)))
      if (col === 'inbox') items = items.filter((r) => r.collection_id === null)
      else if (col) items = items.filter((r) => r.collection_id === col)
      if (q.get('favourite') === 'true') items = items.filter((r) => r.is_favourite)
      const sort = q.get('sort') ?? 'newest'
      items = [...items].sort((a, b) =>
        sort === 'oldest'
          ? a.created_at.localeCompare(b.created_at)
          : sort === 'title'
            ? (a.title ?? a.url).localeCompare(b.title ?? b.url)
            : b.created_at.localeCompare(a.created_at),
      )
      const size = Number(q.get('page_size') ?? 20)
      const page = Number(q.get('page') ?? 1)
      return HttpResponse.json({
        items: items.slice((page - 1) * size, page * size),
        page,
        page_size: size,
        total: items.length,
      })
    }),
    http.post(`${API}/resources`, async ({ request }) => {
      await record(request, '/resources')
      const f = failure('POST', '/resources')
      if (f) return f
      const body = (await request.clone().json()) as Partial<Resource> & { url: string }
      const r = makeResource({
        title: body.title ?? null,
        url: body.url,
        tags: body.tags ?? [],
        collection_id: body.collection_id ?? null,
        type: body.type ?? 'website',
        notes: body.notes ?? null,
        created_at: new Date().toISOString(),
      })
      db.resources = [r, ...db.resources]
      return HttpResponse.json(r, { status: 201 })
    }),
    http.get(`${API}/resources/check-url`, async ({ request }) => {
      await record(request, '/resources/check-url')
      const url = new URL(request.url).searchParams.get('url')
      return HttpResponse.json({ existing: db.resources.find((r) => r.url === url) ?? null })
    }),
    http.post(`${API}/resources/bulk`, async ({ request }) => {
      await record(request, '/resources/bulk')
      const f = failure('POST', '/resources/bulk')
      if (f) return f
      const b = (await request.clone().json()) as {
        action: string
        ids: string[]
        collection_id?: string | null
        tags?: string[]
      }
      let changed = 0
      for (const id of b.ids) {
        if (b.action === 'restore') {
          const t = db.trash.find((r) => r.id === id)
          if (t) {
            db.trash = db.trash.filter((r) => r.id !== id)
            db.resources.push(t)
            changed++
          }
          continue
        }
        const r = find(id)
        if (!r) continue
        if (b.action === 'delete') {
          db.resources = db.resources.filter((x) => x.id !== id)
          db.trash.push(r)
        }
        if (b.action === 'move') replace(touch({ ...r, collection_id: b.collection_id ?? null }))
        if (b.action === 'add_tags')
          replace(touch({ ...r, tags: [...new Set([...r.tags, ...(b.tags ?? [])])].sort() }))
        if (b.action === 'remove_tags')
          replace(touch({ ...r, tags: r.tags.filter((t) => !(b.tags ?? []).includes(t)) }))
        changed++
      }
      return HttpResponse.json({ changed })
    }),
    http.get(`${API}/resources/:id`, async ({ request, params }) => {
      await record(request, '/resources/:id')
      const r = find(String(params.id))
      return r ? HttpResponse.json(r) : problem(404, 'not_found')
    }),
    http.patch(`${API}/resources/:id`, async ({ request, params }) => {
      await record(request, '/resources/:id')
      const f = failure('PATCH', '/resources/:id')
      if (f) return f
      const r = find(String(params.id))
      if (!r) return problem(404, 'not_found')
      const b = (await request.clone().json()) as Partial<Resource> & {
        expected_updated_at?: string
      }
      if (b.expected_updated_at && b.expected_updated_at !== r.updated_at)
        return problem(409, 'edit_conflict')
      const rest: Partial<Resource> = { ...b }
      delete (rest as { expected_updated_at?: string }).expected_updated_at
      const next = touch({
        ...r,
        ...rest,
        ...('summary' in rest
          ? { summary_source: rest.summary ? ('user' as const) : ('none' as const) }
          : {}),
      })
      replace(next)
      return HttpResponse.json(next)
    }),
    http.delete(`${API}/resources/:id`, async ({ request, params }) => {
      await record(request, '/resources/:id')
      const f = failure('DELETE', '/resources/:id')
      if (f) return f
      const r = find(String(params.id))
      if (!r) return problem(404, 'not_found')
      db.resources = db.resources.filter((x) => x.id !== r.id)
      db.trash.push(r)
      return new HttpResponse(null, { status: 204 })
    }),
    http.post(`${API}/resources/:id/restore`, async ({ request, params }) => {
      await record(request, '/resources/:id/restore')
      const r = db.trash.find((x) => x.id === params.id)
      if (!r) return problem(404, 'not_found')
      db.trash = db.trash.filter((x) => x.id !== r.id)
      db.resources.push(r)
      return HttpResponse.json(r)
    }),
    http.get(`${API}/collections`, async ({ request }) => {
      await record(request, '/collections')
      return HttpResponse.json({
        items: withCounts(),
        inbox_count: db.resources.filter((r) => r.collection_id === null).length,
      })
    }),
    http.post(`${API}/collections`, async ({ request }) => {
      await record(request, '/collections')
      const f = failure('POST', '/collections')
      if (f) return f
      const b = (await request.clone().json()) as Partial<Collection>
      const c = makeCollection({
        name: b.name,
        description: b.description ?? null,
        icon: b.icon ?? null,
        color: b.color ?? null,
      })
      db.collections.push(c)
      return HttpResponse.json(c, { status: 201 })
    }),
    http.put(`${API}/collections/order`, async ({ request }) => {
      await record(request, '/collections/order')
      const ids = ((await request.clone().json()) as { ids: string[] }).ids
      db.collections.sort((a, b) => ids.indexOf(a.id) - ids.indexOf(b.id))
      return new HttpResponse(null, { status: 204 })
    }),
    http.patch(`${API}/collections/:id`, async ({ request, params }) => {
      await record(request, '/collections/:id')
      const f = failure('PATCH', '/collections/:id')
      if (f) return f
      const b = (await request.clone().json()) as Partial<Collection>
      db.collections = db.collections.map((c) => (c.id === params.id ? { ...c, ...b } : c))
      return HttpResponse.json(db.collections.find((c) => c.id === params.id))
    }),
    http.delete(`${API}/collections/:id`, async ({ request, params }) => {
      await record(request, '/collections/:id')
      const q = new URL(request.url).searchParams
      const target = q.get('target_collection_id')
      db.resources = db.resources.flatMap((r) =>
        r.collection_id !== params.id
          ? [r]
          : q.get('resources') === 'delete'
            ? []
            : [{ ...r, collection_id: target }],
      )
      db.collections = db.collections.filter((c) => c.id !== params.id)
      return new HttpResponse(null, { status: 204 })
    }),
    http.get(`${API}/tags`, async ({ request }) => {
      await record(request, '/tags')
      return HttpResponse.json({ items: tagCounts() })
    }),
    http.patch(`${API}/tags/:id`, async ({ request, params }) => {
      await record(request, '/tags/:id')
      const f = failure('PATCH', '/tags/:id')
      if (f) return f
      const old = String(params.id).replace('tag-', '')
      const name = ((await request.clone().json()) as { name: string }).name
      db.resources = db.resources.map((r) => ({
        ...r,
        tags: r.tags.map((t) => (t === old ? name : t)),
      }))
      return HttpResponse.json({
        id: `tag-${name}`,
        name,
        resource_count: db.resources.filter((r) => r.tags.includes(name)).length,
      })
    }),
    http.post(`${API}/tags/:id/merge`, async ({ request, params }) => {
      await record(request, '/tags/:id/merge')
      const old = String(params.id).replace('tag-', '')
      const target = (
        (await request.clone().json()) as { target_tag_id: string }
      ).target_tag_id.replace('tag-', '')
      db.resources = db.resources.map((r) => ({
        ...r,
        tags: [...new Set(r.tags.map((t) => (t === old ? target : t)))],
      }))
      return HttpResponse.json({
        id: `tag-${target}`,
        name: target,
        resource_count: db.resources.filter((r) => r.tags.includes(target)).length,
      })
    }),
    http.delete(`${API}/tags/:id`, async ({ request, params }) => {
      await record(request, '/tags/:id')
      const old = String(params.id).replace('tag-', '')
      db.resources = db.resources.map((r) => ({ ...r, tags: r.tags.filter((t) => t !== old) }))
      return new HttpResponse(null, { status: 204 })
    }),
  )
  return db
}
