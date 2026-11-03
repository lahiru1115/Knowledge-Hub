import { HttpResponse, http } from 'msw'
import type { components } from '@/api/schema'
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
    metadata_error: null,
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
  /** What POST /metadata/preview returns (default: a small page). */
  preview: (url: string) => Preview
  /** Updates a resource as the background job would, without touching updated_at. */
  finishMetadata: (id: string, patch?: Partial<Resource>) => void
  ai: FakeAi
  /** What the dashboard cannot work out from the data above. */
  dash: { triedSearch: boolean; askedQuestion: boolean; dismissed: boolean; weekly: number[] }
}

export type Preview = components['schemas']['MetadataPreview']
type Dashboard = components['schemas']['Dashboard']
type ChatSession = components['schemas']['ChatSession']
type ChatMessage = components['schemas']['ChatMessage']
type Citation = components['schemas']['Citation']

/** What the fake AI service does when a question is asked. */
export interface FakeAnswer {
  tokens: string[]
  citations?: Citation[]
  /** Ends the stream with an `error` event instead of `done`. */
  error?: string
  /** After the tokens, keep the stream open (until the browser aborts it). */
  hang?: boolean
}

/** The AI service as the web app sees it. `down` makes every AI endpoint answer 503 and the status say unavailable. */
export interface FakeAi {
  down: boolean
  profile: 'free' | 'claude'
  chatAvailable: boolean
  /** The summary the next request produces; 'thin' answers 422 not_enough_content. */
  summary: (r: Resource) => { summary: string; source: 'ai' | 'extracted' } | 'thin'
  tags: (body: { url?: string; resource_id?: string }) => { name: string; existing: boolean }[]
  related: (id: string) => Resource[]
  semantic: (q: string) => { resource: Resource; snippet: string; score: number }[]
  usage: { items: { operation: string; used: number; limit: number | null }[]; reset_at: string }
  answer: (question: string, session: ChatSession) => FakeAnswer
  sessions: ChatSession[]
  messages: Record<string, ChatMessage[]>
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
    preview: (url) => ({
      title: `Preview of ${new URL(url).hostname}`,
      description: 'A short description of the page.',
      image_url: null,
      favicon_url: null,
      type: 'article',
      error: null,
    }),
    ai: {
      down: false,
      profile: 'free',
      chatAvailable: true,
      summary: (r) => ({ summary: `A summary of ${r.title ?? r.url}.`, source: 'extracted' }),
      tags: () => [
        { name: 'postgres', existing: true },
        { name: 'pooling', existing: false },
      ],
      related: () => [],
      semantic: (q) =>
        db.resources
          .filter(
            (r) =>
              !r.is_archived &&
              q
                .split(/\s+/)
                .some((w) =>
                  (r.description ?? r.title ?? '').toLowerCase().includes(w.toLowerCase()),
                ),
          )
          .map((r) => ({
            resource: r,
            snippet: `…${r.description ?? r.title ?? ''}…`,
            score: 0.03,
          })),
      usage: {
        reset_at: new Date(Date.now() + 3 * 3600_000).toISOString(),
        items: [
          { operation: 'summarize', used: 4, limit: 300 },
          { operation: 'suggest_tags', used: 0, limit: 300 },
          { operation: 'search', used: 2, limit: 500 },
          { operation: 'chat', used: 1, limit: 100 },
        ],
      },
      answer: () => ({ tokens: ['Pooling keeps connections open ', '[1].'], citations: [] }),
      sessions: [],
      messages: {},
    },
    dash: { triedSearch: false, askedQuestion: false, dismissed: false, weekly: [] },
    finishMetadata: (id, patch = {}) => {
      const r = find(id)
      if (r) replace({ ...r, metadata_status: 'ready', metadata_error: null, ...patch })
    },
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
    http.post(`${API}/metadata/preview`, async ({ request }) => {
      await record(request, '/metadata/preview')
      const f = failure('POST', '/metadata/preview')
      if (f) return f
      const body = (await request.clone().json()) as { url: string }
      return HttpResponse.json(db.preview(body.url))
    }),
    http.post(`${API}/resources/:id/metadata/refresh`, async ({ request, params }) => {
      await record(request, '/resources/:id/metadata/refresh')
      const f = failure('POST', '/resources/:id/metadata/refresh')
      if (f) return f
      const r = find(String(params.id))
      if (!r) return problem(404, 'not_found')
      const next = { ...r, metadata_status: 'pending' as const, metadata_error: null }
      replace(next)
      return HttpResponse.json(next, { status: 202 })
    }),
    http.get(`${API}/dashboard`, async ({ request }) => {
      await record(request, '/dashboard')
      const f = failure('GET', '/dashboard')
      if (f) return f
      const live = db.resources.filter((r) => !r.is_archived)
      const count = (t: Resource['type']) => live.filter((r) => r.type === t).length
      const monday = new Date()
      monday.setUTCHours(0, 0, 0, 0)
      monday.setUTCDate(monday.getUTCDate() - ((monday.getUTCDay() + 6) % 7))
      const weekly_activity = Array.from({ length: 12 }, (_, i) => {
        const d = new Date(monday)
        d.setUTCDate(d.getUTCDate() - 7 * (11 - i))
        return { week_start: d.toISOString().slice(0, 10), count: db.dash.weekly[i] ?? 0 }
      })
      const body: Dashboard = {
        totals: {
          resources: live.length,
          collections: db.collections.length,
          tags: new Set(db.resources.flatMap((r) => r.tags)).size,
          favourites: live.filter((r) => r.is_favourite).length,
        },
        by_type: {
          article: count('article'),
          video: count('video'),
          pdf: count('pdf'),
          course: count('course'),
          repo: count('repo'),
          website: count('website'),
          other: count('other'),
        },
        recent: [...live].sort((a, b) => b.created_at.localeCompare(a.created_at)).slice(0, 5),
        weekly_activity,
        onboarding: {
          dismissed: db.dash.dismissed,
          steps: {
            first_resource: db.resources.length + db.trash.length > 0,
            first_collection: db.collections.length > 0,
            tried_search: db.dash.triedSearch,
            asked_question: db.dash.askedQuestion,
          },
        },
      }
      return HttpResponse.json(body)
    }),
    // ---- AI ----
    http.get(`${API}/ai/status`, async ({ request }) => {
      await record(request, '/ai/status')
      const { ai } = db
      return HttpResponse.json({
        available: !ai.down,
        profile: ai.profile,
        chat_available: !ai.down && ai.chatAvailable,
      })
    }),
    http.get(`${API}/me/ai-usage`, async ({ request }) => {
      await record(request, '/me/ai-usage')
      const f = failure('GET', '/me/ai-usage')
      if (f) return f
      return db.ai.down ? problem(503, 'ai_unavailable') : HttpResponse.json(db.ai.usage)
    }),
    http.post(`${API}/resources/:id/summary`, async ({ request, params }) => {
      await record(request, '/resources/:id/summary')
      const f = failure('POST', '/resources/:id/summary')
      if (f) return f
      if (db.ai.down) return problem(503, 'ai_unavailable')
      const r = find(String(params.id))
      if (!r) return problem(404, 'not_found')
      const made = db.ai.summary(r)
      if (made === 'thin') return problem(422, 'not_enough_content')
      const next = touch({ ...r, summary: made.summary, summary_source: made.source })
      replace(next)
      return HttpResponse.json(next)
    }),
    http.delete(`${API}/resources/:id/summary`, async ({ request, params }) => {
      await record(request, '/resources/:id/summary')
      const r = find(String(params.id))
      if (!r) return problem(404, 'not_found')
      replace(touch({ ...r, summary: null, summary_source: 'none' }))
      return new HttpResponse(null, { status: 204 })
    }),
    http.post(`${API}/ai/tag-suggestions`, async ({ request }) => {
      await record(request, '/ai/tag-suggestions')
      const f = failure('POST', '/ai/tag-suggestions')
      if (f) return f
      if (db.ai.down) return HttpResponse.json({ items: [], ai_available: false })
      const body = (await request.clone().json()) as { url?: string; resource_id?: string }
      return HttpResponse.json({ items: db.ai.tags(body), ai_available: true })
    }),
    http.get(`${API}/resources/:id/related`, async ({ request, params }) => {
      await record(request, '/resources/:id/related')
      if (db.ai.down) return problem(503, 'ai_unavailable')
      return HttpResponse.json({ items: db.ai.related(String(params.id)) })
    }),
    http.get(`${API}/search/semantic`, async ({ request }) => {
      await record(request, '/search/semantic')
      const f = failure('GET', '/search/semantic')
      if (f) return f
      const q = new URL(request.url).searchParams.get('q') ?? ''
      if (db.ai.down) {
        const items = db.resources
          .filter((r) => (r.title ?? '').toLowerCase().includes(q.toLowerCase()))
          .map((resource) => ({ resource, snippet: resource.description ?? '', score: 0 }))
        return HttpResponse.json({
          items,
          page: 1,
          page_size: 20,
          total: items.length,
          fallback: true,
        })
      }
      const items = db.ai.semantic(q)
      return HttpResponse.json({
        items,
        page: 1,
        page_size: 20,
        total: items.length,
        fallback: false,
      })
    }),
    http.get(`${API}/chat/sessions`, async ({ request }) => {
      await record(request, '/chat/sessions')
      if (db.ai.down) return problem(503, 'ai_unavailable')
      return HttpResponse.json({ items: [...db.ai.sessions].reverse() })
    }),
    http.post(`${API}/chat/sessions`, async ({ request }) => {
      await record(request, '/chat/sessions')
      const f = failure('POST', '/chat/sessions')
      if (f) return f
      if (db.ai.down) return problem(503, 'ai_unavailable')
      const body = (await request
        .clone()
        .json()
        .catch(() => ({}))) as { scope_collection_id?: string | null }
      const now = new Date().toISOString()
      const session: ChatSession = {
        id: `chat-${(db.ai.sessions.length + 1).toString().padStart(3, '0')}`,
        title: 'New chat',
        scope_collection_id: body.scope_collection_id ?? null,
        created_at: now,
        updated_at: now,
      }
      db.ai.sessions.push(session)
      db.ai.messages[session.id] = []
      return HttpResponse.json(session, { status: 201 })
    }),
    http.get(`${API}/chat/sessions/:id`, async ({ request, params }) => {
      await record(request, '/chat/sessions/:id')
      if (db.ai.down) return problem(503, 'ai_unavailable')
      const session = db.ai.sessions.find((x) => x.id === params.id)
      return session
        ? HttpResponse.json({ ...session, messages: db.ai.messages[session.id] ?? [] })
        : problem(404, 'not_found')
    }),
    http.patch(`${API}/chat/sessions/:id`, async ({ request, params }) => {
      await record(request, '/chat/sessions/:id')
      const session = db.ai.sessions.find((x) => x.id === params.id)
      if (!session) return problem(404, 'not_found')
      session.title = ((await request.clone().json()) as { title: string }).title
      return HttpResponse.json(session)
    }),
    http.delete(`${API}/chat/sessions/:id`, async ({ request, params }) => {
      await record(request, '/chat/sessions/:id')
      db.ai.sessions = db.ai.sessions.filter((x) => x.id !== params.id)
      return new HttpResponse(null, { status: 204 })
    }),
    http.post(`${API}/chat/sessions/:id/messages`, async ({ request, params }) => {
      await record(request, '/chat/sessions/:id/messages')
      const f = failure('POST', '/chat/sessions/:id/messages')
      if (f) return f
      if (db.ai.down) return problem(503, 'ai_unavailable')
      const session = db.ai.sessions.find((x) => x.id === params.id)
      if (!session) return problem(404, 'not_found')
      const { content } = (await request.clone().json()) as { content: string }
      const script = db.ai.answer(content, session)
      const enc = new TextEncoder()
      const send = (
        c: ReadableStreamDefaultController<Uint8Array>,
        event: string,
        data: unknown,
      ) => {
        c.enqueue(enc.encode(`event: ${event}\ndata: ${JSON.stringify(data)}\n\n`))
      }
      const stream = new ReadableStream<Uint8Array>({
        async start(c) {
          for (const text of script.tokens) {
            send(c, 'token', { text })
            await new Promise((r) => setTimeout(r, 5))
          }
          if (script.hang) return
          send(c, 'citations', { items: script.citations ?? [] })
          if (script.error) send(c, 'error', { code: script.error })
          else {
            const answer = script.tokens.join('')
            const now = new Date().toISOString()
            const list = (db.ai.messages[session.id] ??= [])
            list.push({
              id: `m-${(list.length + 1).toString()}`,
              role: 'user',
              content,
              created_at: now,
            })
            const id = `m-${(list.length + 1).toString()}`
            list.push({
              id,
              role: 'assistant',
              content: answer,
              citations: script.citations ?? [],
              created_at: now,
            })
            if (session.title === 'New chat') session.title = content.slice(0, 60)
            session.updated_at = now
            send(c, 'done', { message_id: id, usage: { tokens_in: 100, tokens_out: 10 } })
          }
          c.close()
        },
      })
      return new HttpResponse(stream, {
        headers: { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-cache' },
      })
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
