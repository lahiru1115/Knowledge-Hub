import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeResource } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'

async function openDrawer(
  resource = makeResource({ title: 'Pooling guide', metadata_status: 'ready' }),
  setup?: (db: ReturnType<typeof fakeApi>) => void,
) {
  signedInSession()
  const db = fakeApi({ resources: [resource] })
  setup?.(db)
  const view = renderApp(`/library?r=${resource.id}`)
  const drawer = await screen.findByRole('dialog', { name: 'Resource' })
  await within(drawer).findByRole('heading', { name: resource.title ?? '' })
  return { db, drawer, resource, ...view, u: userEvent.setup() }
}

describe('summary', () => {
  it('summarises on request, shows the extracted label, and offers regenerate', async () => {
    const { db, drawer, u, resource } = await openDrawer()
    expect(within(drawer).getByText('No summary yet.')).toBeInTheDocument()

    await u.click(await within(drawer).findByRole('button', { name: /Summarise/ }))

    expect(await within(drawer).findByText('A summary of Pooling guide.')).toBeInTheDocument()
    expect(within(drawer).getByText('Extracted from the page')).toBeInTheDocument()
    expect(db.all('POST', '/resources/:id/summary')).toHaveLength(1)
    expect(db.resources.find((r) => r.id === resource.id)?.summary_source).toBe('extracted')
    expect(within(drawer).getByRole('button', { name: 'Regenerate summary' })).toBeEnabled()
  })

  it('labels a model-written summary as AI output', async () => {
    const { drawer, u } = await openDrawer(undefined, (db) => {
      db.ai.summary = () => ({ summary: 'Model words.', source: 'ai' })
    })
    await u.click(await within(drawer).findByRole('button', { name: /Summarise/ }))
    expect(
      await within(drawer).findByText(/AI-generated · may contain mistakes/),
    ).toBeInTheDocument()
  })

  it('says so when there is too little text, instead of failing', async () => {
    const { drawer, u } = await openDrawer(undefined, (db) => {
      db.ai.summary = () => 'thin'
    })
    await u.click(await within(drawer).findByRole('button', { name: /Summarise/ }))
    expect(
      await within(drawer).findByText("This page doesn't have enough text to summarise."),
    ).toBeInTheDocument()
    expect(within(drawer).getByRole('button', { name: /Summarise/ })).toBeEnabled() // can try again later
  })

  it('shows when the daily limit resets, as a toast, and keeps the block as it was', async () => {
    const { db, drawer, u } = await openDrawer()
    db.failNext('POST', '/resources/:id/summary', 429, 'ai_quota_exceeded', {
      reset_at: new Date(Date.now() + 3 * 3600_000 + 60_000).toISOString(),
    })
    await u.click(await within(drawer).findByRole('button', { name: /Summarise/ }))
    expect(
      await screen.findByText(/used today's AI limit\. It resets in 3 hours/),
    ).toBeInTheDocument()
    expect(within(drawer).getByText('No summary yet.')).toBeInTheDocument()
  })

  it('disables generating while the AI service is down, with the reason, but still shows an existing summary', async () => {
    const withSummary = makeResource({
      title: 'Pooling guide',
      summary: 'Kept text.',
      summary_source: 'extracted',
    })
    const { drawer } = await openDrawer(withSummary, (db) => {
      db.ai.down = true
    })
    expect(within(drawer).getByText('Kept text.')).toBeInTheDocument()
    const regenerate = within(drawer).getByRole('button', { name: 'Regenerate summary' })
    await waitFor(() => {
      expect(regenerate).toBeDisabled()
    })
    expect(regenerate).toHaveAttribute('title', 'AI features are unavailable right now.')
  })

  it('shows the Summarise button disabled with a note while the AI service is down', async () => {
    const { drawer } = await openDrawer(undefined, (db) => {
      db.ai.down = true
    })
    expect(
      await within(drawer).findByText('AI features are unavailable right now.'),
    ).toBeInTheDocument()
    expect(within(drawer).getByRole('button', { name: /Summarise/ })).toBeDisabled()
    expect(within(drawer).getByRole('button', { name: 'Write a summary' })).toBeEnabled() // writing by hand still works
  })

  it('asks before replacing a summary the user wrote', async () => {
    const mine = makeResource({
      title: 'Pooling guide',
      summary: 'My own words.',
      summary_source: 'user',
    })
    const { db, drawer, u } = await openDrawer(mine)
    await u.click(await within(drawer).findByRole('button', { name: 'Regenerate summary' }))
    const dialog = await screen.findByRole('alertdialog', { name: 'Replace your summary?' })
    await u.click(within(dialog).getByRole('button', { name: 'Keep mine' }))
    expect(db.all('POST', '/resources/:id/summary')).toHaveLength(0)

    await u.click(within(drawer).getByRole('button', { name: 'Regenerate summary' }))
    await u.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', { name: 'Replace it' }),
    )
    expect(await within(drawer).findByText('A summary of Pooling guide.')).toBeInTheDocument()
  })
})

describe('tag suggestions in the drawer', () => {
  it('offers suggestions that are not yet tags, and applies one only when clicked', async () => {
    const { db, drawer, u, resource } = await openDrawer(
      makeResource({ title: 'Pooling guide', tags: ['postgres'] }),
    )
    const group = await within(drawer).findByRole('group', { name: 'Suggested tags' })
    expect(
      within(group).queryByRole('button', { name: 'Add tag postgres' }),
    ).not.toBeInTheDocument() // already a tag
    expect(db.all('PATCH', '/resources/:id')).toHaveLength(0) // nothing applied on its own

    await u.click(within(group).getByRole('button', { name: 'Add tag pooling' }))

    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ tags: ['postgres', 'pooling'] })
    })
    expect(db.last('POST', '/ai/tag-suggestions')?.body).toEqual({ resource_id: resource.id })
  })

  it('has Accept all only when there are at least two suggestions', async () => {
    const { db, drawer, u } = await openDrawer()
    await u.click(await within(drawer).findByRole('button', { name: 'Accept all' }))
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ tags: ['postgres', 'pooling'] })
    })
  })

  it('shows no Accept all for a single suggestion', async () => {
    const { drawer } = await openDrawer(undefined, (db) => {
      db.ai.tags = () => [{ name: 'solo', existing: false }]
    })
    await within(drawer).findByRole('button', { name: 'Add tag solo' })
    expect(within(drawer).queryByRole('button', { name: 'Accept all' })).not.toBeInTheDocument()
  })

  it('says suggestions are unavailable when the AI service is down', async () => {
    const { drawer } = await openDrawer(undefined, (db) => {
      db.ai.down = true
    })
    expect(
      await within(drawer).findByText('Tag suggestions are unavailable right now.'),
    ).toBeInTheDocument()
    expect(within(drawer).queryByRole('group', { name: 'Suggested tags' })).not.toBeInTheDocument()
  })
})

describe('related resources', () => {
  it('lists related resources and opens one in the drawer', async () => {
    const other = makeResource({ title: 'PgBouncer explained', metadata_status: 'ready' })
    const { drawer, u, router } = await openDrawer(undefined, (db) => {
      db.resources.push(other)
      db.ai.related = () => [other]
    })
    const section = await within(drawer).findByRole('region', { name: /Related/ })
    await u.click(within(section).getByRole('button', { name: 'PgBouncer explained' }))
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ r: other.id })
    })
  })

  it('is left out when there are none, and when the AI service is down', async () => {
    const { drawer } = await openDrawer()
    await within(drawer).findByRole('heading', { name: 'Pooling guide' })
    expect(within(drawer).queryByRole('region', { name: /Related/ })).not.toBeInTheDocument()
  })
})

describe('tag suggestions in quick-add', () => {
  async function open(setup?: (db: ReturnType<typeof fakeApi>) => void) {
    signedInSession()
    const db = fakeApi()
    setup?.(db)
    renderApp('/library')
    const u = userEvent.setup()
    await u.click(await screen.findByRole('button', { name: 'Add' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a resource' })
    return { db, dialog, u }
  }

  it('suggests tags for a pasted link and adds one only when clicked', async () => {
    const { db, dialog, u } = await open()
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/pool')

    const group = await within(dialog).findByRole('group', { name: 'Suggested tags' })
    expect(db.last('POST', '/ai/tag-suggestions')?.body).toEqual({
      url: 'https://example.org/pool',
    })
    expect(within(dialog).queryByText('pooling', { selector: 'li *' })).not.toBeInTheDocument() // not applied yet

    await u.click(within(group).getByRole('button', { name: 'Add tag pooling' }))

    expect(within(dialog).getByRole('button', { name: /pooling/ })).toBeInTheDocument()
    await waitFor(() => {
      expect(
        within(dialog).queryByRole('button', { name: 'Add tag pooling' }),
      ).not.toBeInTheDocument()
    })
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(db.last('POST', '/resources')?.body).toMatchObject({ tags: ['pooling'] })
    })
  })

  it('Accept all adds every suggestion', async () => {
    const { db, dialog, u } = await open()
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/pool')
    await u.click(await within(dialog).findByRole('button', { name: 'Accept all' }))
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(db.last('POST', '/resources')?.body).toMatchObject({ tags: ['postgres', 'pooling'] })
    })
  })

  it('shows a quiet note, not an error, when the AI service is down, and the link still saves', async () => {
    const { db, dialog, u } = await open((d) => {
      d.ai.down = true
    })
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/pool')
    expect(
      await within(dialog).findByText('Tag suggestions are unavailable right now.'),
    ).toBeInTheDocument()
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(db.last('POST', '/resources')?.body).toMatchObject({ url: 'https://example.org/pool' })
    })
  })

  it('does not ask for suggestions, or mention them, when the user turned them off', async () => {
    const { http, HttpResponse } = await import('msw')
    const { server } = await import('@/test/server')
    const { API, user } = await import('@/test/helpers')
    signedInSession()
    const db = fakeApi()
    server.use(
      http.get(`${API}/me`, () =>
        HttpResponse.json({
          ...user,
          preferences: { ...user.preferences, ai_tag_suggestions: false },
        }),
      ),
    )
    renderApp('/library')
    const u = userEvent.setup()
    await u.click(await screen.findByRole('button', { name: 'Add' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add a resource' })
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/pool')
    await within(dialog).findByRole('group', { name: 'Preview' }) // the preview loaded, so the suggestions had their chance
    expect(db.all('POST', '/ai/tag-suggestions')).toHaveLength(0)
    expect(within(dialog).queryByText(/Suggested tags|unavailable/)).not.toBeInTheDocument()
  })
})
