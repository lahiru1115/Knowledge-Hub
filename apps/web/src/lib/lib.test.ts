import { describe, expect, it } from 'vitest'
import { normalizeTag } from '@/features/library/TagInput'
import {
  activeFilterCount,
  librarySearchSchema,
  toApiQuery,
} from '@/features/library/librarySearch'
import { plural, siteOf, timeAgo } from './format'
import { parseSearch, stringifySearch } from './searchParams'
import { parseSse, readSse } from './sse'

describe('search params', () => {
  it('writes readable URLs: repeated keys for lists, plain values otherwise', () => {
    expect(stringifySearch({ q: 'pool', type: ['video', 'pdf'], page: 2, favourite: true })).toBe(
      '?q=pool&type=video&type=pdf&page=2&favourite=true',
    )
  })

  it('leaves out empty values', () => {
    expect(stringifySearch({ q: '', tag: [], page: undefined, r: null })).toBe('')
  })

  it('reads them back: one value is a string, several are an array', () => {
    expect(parseSearch('?q=a%20b&tag=x&tag=y')).toEqual({ q: 'a b', tag: ['x', 'y'] })
  })

  it('round-trips through the library schema', () => {
    const url =
      '?q=pool&type=video&type=pdf&tag=postgres&favourite=true&page=3&size=50&sort=title&r=abc'
    const parsed = librarySearchSchema.parse(parseSearch(url))
    expect(parsed).toEqual({
      q: 'pool',
      type: ['video', 'pdf'],
      tag: ['postgres'],
      favourite: true,
      page: 3,
      size: 50,
      sort: 'title',
      r: 'abc',
    })
    // Keys come out in the schema's order, which keeps URLs stable.
    expect(stringifySearch(parsed)).toBe(
      '?q=pool&type=video&type=pdf&tag=postgres&favourite=true&sort=title&page=3&size=50&r=abc',
    )
  })

  it('drops invalid values rather than failing', () => {
    expect(
      librarySearchSchema.parse(parseSearch('?page=0&size=9999&sort=sideways&favourite=maybe')),
    ).toEqual({})
  })

  it('builds the API query: relevance with a query, newest without, and unknown types removed', () => {
    expect(toApiQuery({ q: 'x' }).sort).toBe('relevance')
    expect(toApiQuery({}).sort).toBe('newest')
    expect(toApiQuery({ sort: 'oldest', q: 'x' }).sort).toBe('oldest')
    expect(toApiQuery({ type: ['video', 'podcast'] }).type).toEqual(['video'])
    expect(toApiQuery({ collection: 'c1' }, 'scoped').collection_id).toBe('scoped')
    expect(toApiQuery({}).page_size).toBe(20)
  })

  it('counts active filters but not the query, sort or page', () => {
    expect(activeFilterCount({ q: 'x', sort: 'title', page: 2 })).toBe(0)
    expect(
      activeFilterCount({
        type: ['a', 'b'],
        tag: ['t'],
        collection: 'inbox',
        favourite: true,
        archived: true,
      }),
    ).toBe(6)
  })
})

describe('formatting', () => {
  const now = new Date('2026-11-02T12:00:00Z')

  it.each([
    ['2026-11-02T11:59:50Z', 'just now'],
    ['2026-11-02T11:30:00Z', '30 minutes ago'],
    ['2026-11-02T09:00:00Z', '3 hours ago'],
    ['2026-11-01T12:00:00Z', 'yesterday'],
    ['2026-10-30T12:00:00Z', '3 days ago'],
    ['2026-10-19T12:00:00Z', '2 weeks ago'],
    ['2026-08-02T12:00:00Z', '3 months ago'],
    ['2024-11-02T12:00:00Z', '2 years ago'],
  ])('%s is %s', (iso, expected) => {
    expect(timeAgo(iso, now)).toBe(expected)
  })

  it('names the site without www, and falls back to the text for non-URLs', () => {
    expect(siteOf('https://www.npgsql.org/doc/performance.html')).toBe('npgsql.org')
    expect(siteOf('https://sub.example.co.uk/x')).toBe('sub.example.co.uk')
    expect(siteOf('not a url')).toBe('not a url')
  })

  it('pluralises with thousands separators', () => {
    expect(plural(1, 'resource')).toBe('1 resource')
    expect(plural(0, 'resource')).toBe('0 resources')
    expect(plural(1234, 'resource')).toBe('1,234 resources')
  })
})

describe('normalizeTag (same rules as the server)', () => {
  it.each([
    ['Machine  Learning', 'machine learning'],
    ['  C# ', 'c#'],
    ['node.js', 'node.js'],
    ['日本語', '日本語'],
  ])('%s becomes %s', (input, expected) => {
    expect(normalizeTag(input)).toBe(expected)
  })

  it.each(['', '   ', 'a/b', 'a;b', 'x'.repeat(51), 'tab\there', 'emoji 😀'])(
    'refuses %j',
    (input) => {
      expect(normalizeTag(input)).toBeUndefined()
    },
  )

  it('accepts exactly 50 characters', () => {
    expect(normalizeTag('a'.repeat(50))).toBe('a'.repeat(50))
  })
})

describe('parseSse', () => {
  it('returns complete events and keeps the unfinished one', () => {
    const { events, rest } = parseSse(
      'event: token\ndata: {"text":"Hi"}\n\nevent: token\ndata: {"te',
    )
    expect(events).toEqual([{ event: 'token', data: { text: 'Hi' } }])
    expect(rest).toBe('event: token\ndata: {"te')
  })

  it('skips comment lines and events that are not JSON, and accepts CRLF', () => {
    const { events } = parseSse(
      ': keep-alive\n\nevent: x\ndata: not json\n\r\n\r\nevent: done\r\ndata: {"a":1}\r\n\r\n',
    )
    expect(events).toEqual([{ event: 'done', data: { a: 1 } }])
  })

  it('treats an event with no name as "message"', () => {
    expect(parseSse('data: {"a":1}\n\n').events).toEqual([{ event: 'message', data: { a: 1 } }])
  })

  it('reads a stream that arrives in awkward pieces, including a multi-byte character split across chunks', async () => {
    const bytes = new TextEncoder().encode(
      'event: token\ndata: {"text":"café ✨"}\n\nevent: done\ndata: {"ok":true}\n\n',
    )
    const stream = new ReadableStream<Uint8Array>({
      start(c) {
        for (let i = 0; i < bytes.length; i += 7) c.enqueue(bytes.slice(i, i + 7))
        c.close()
      },
    })
    const seen: unknown[] = []
    await readSse(stream, (e) => seen.push(e))
    expect(seen).toEqual([
      { event: 'token', data: { text: 'café ✨' } },
      { event: 'done', data: { ok: true } },
    ])
  })
})
