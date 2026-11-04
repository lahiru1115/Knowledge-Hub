import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeCollection, makeResource } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'

async function selectOption(
  u: ReturnType<typeof userEvent.setup>,
  trigger: HTMLElement,
  option: string | RegExp,
) {
  await u.click(trigger)
  await u.click(await screen.findByRole('option', { name: option }))
}

function rows() {
  // Direct children only: each row also contains a list of its tags.
  return Array.from(screen.getByRole('list', { name: 'Resources' }).children) as HTMLElement[]
}

describe('library list', () => {
  it('shows the title, count and each resource with its site, type, tags, collection and age', async () => {
    signedInSession()
    const col = makeCollection({ name: 'Databases', icon: '📘' })
    fakeApi({
      collections: [col],
      resources: [
        makeResource({
          title: 'Npgsql performance',
          url: 'https://www.npgsql.org/doc/performance.html',
          description: 'Tips for tuning.',
          tags: ['postgres', 'dotnet'],
          collection_id: col.id,
        }),
        makeResource({ title: 'Inbox thing', type: 'video' }),
      ],
    })

    renderApp('/library')

    expect(await screen.findByRole('heading', { name: 'Library' })).toBeInTheDocument()
    expect(await screen.findByText('2 resources')).toBeInTheDocument()
    const [first, second] = rows()
    expect(within(first!).getByRole('button', { name: 'Npgsql performance' })).toBeInTheDocument()
    expect(within(first!).getByText(/npgsql\.org · Article/)).toBeInTheDocument()
    expect(within(first!).getByText('Tips for tuning.')).toBeInTheDocument()
    expect(
      within(first!).getByRole('button', { name: 'Filter by tag postgres' }),
    ).toBeInTheDocument()
    expect(within(first!).getByText(/📘 Databases/)).toBeInTheDocument()
    expect(within(second!).getByText(/Inbox · /)).toBeInTheDocument()
    expect(within(second!).getByText(/Video/)).toBeInTheDocument()
  })

  it('shows the URL when a resource has no title yet', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource({ title: null, url: 'https://example.com/untitled' })] })
    renderApp('/library')
    expect(
      await screen.findByRole('button', { name: 'https://example.com/untitled' }),
    ).toBeInTheDocument()
  })

  it('has a skeleton while loading', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource()] })
    renderApp('/library')
    expect(await screen.findByRole('list', { name: 'Loading resources' })).toHaveAttribute(
      'aria-busy',
      'true',
    )
    await screen.findByRole('list', { name: 'Resources' })
  })

  it('offers to try again when loading fails', async () => {
    signedInSession()
    const db = fakeApi({ resources: [makeResource({ title: 'Recovered' })] })
    db.failNext('GET', '/resources', 500, 'internal_error')
    renderApp('/library')
    const u = userEvent.setup()

    expect(await screen.findByText("Couldn't load your library.")).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('button', { name: 'Recovered' })).toBeInTheDocument()
  })

  it('shows the empty library state with a way to add the first resource', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()

    expect(await screen.findByText('Your library is empty.')).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Add your first resource' }))

    expect(await screen.findByRole('dialog', { name: 'Add a resource' })).toBeInTheDocument()
  })

  it("says what didn't match and offers to clear the filters", async () => {
    signedInSession()
    fakeApi({ resources: [makeResource({ title: 'Something else' })] })
    const { router } = renderApp('/library?q=zebra')
    const u = userEvent.setup()

    expect(
      await screen.findByText("Nothing matches 'zebra' with these filters."),
    ).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Clear filters' }))

    expect(await screen.findByRole('button', { name: 'Something else' })).toBeInTheDocument()
    expect(router.state.location.search).not.toHaveProperty('q')
  })
})

describe('search, filters and sorting live in the URL', () => {
  it('typing in the search box updates ?q= after a pause and asks the API', async () => {
    signedInSession()
    const db = fakeApi({
      resources: [
        makeResource({ title: 'Redis guide' }),
        makeResource({ title: 'Kubernetes guide' }),
      ],
    })
    const { router } = renderApp('/library')
    const u = userEvent.setup()

    await u.type(await screen.findByRole('searchbox', { name: 'Search your library' }), 'redis')

    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ q: 'redis' })
    })
    expect(router.state.location.href).toContain('q=redis')
    await waitFor(() => {
      expect(rows()).toHaveLength(1)
    })
    expect(db.last('GET', '/resources')?.query.get('q')).toBe('redis')
    expect(db.last('GET', '/resources')?.query.get('sort')).toBe('relevance')
  })

  it('starts from the URL, so a filtered view can be bookmarked', async () => {
    signedInSession()
    const db = fakeApi({
      resources: [
        makeResource({ title: 'A video', type: 'video', tags: ['x'] }),
        makeResource({ title: 'An article', type: 'article', tags: ['x'] }),
      ],
    })

    renderApp('/library?type=video&tag=x&favourite=false&page=1&size=50&sort=oldest')

    expect(await screen.findByRole('button', { name: 'A video' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'An article' })).not.toBeInTheDocument()
    const q = db.last('GET', '/resources')?.query
    expect(q?.getAll('type')).toEqual(['video'])
    expect(q?.getAll('tag')).toEqual(['x'])
    expect(q?.get('page_size')).toBe('50')
    expect(q?.get('sort')).toBe('oldest')
  })

  it('ignores junk in the URL instead of breaking', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource({ title: 'Still here' })] })
    renderApp('/library?page=banana&size=9999&sort=sideways&type=podcast')
    expect(await screen.findByRole('button', { name: 'Still here' })).toBeInTheDocument()
  })

  it('filters by type from the popover and shows a removable chip', async () => {
    signedInSession()
    fakeApi({
      resources: [
        makeResource({ title: 'Vid', type: 'video' }),
        makeResource({ title: 'Art', type: 'article' }),
      ],
    })
    const { router } = renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('button', { name: 'Vid' })

    await u.click(screen.getByRole('button', { name: /^Type/ }))
    await u.click(await screen.findByRole('checkbox', { name: 'Video' }))

    await waitFor(() => {
      expect(rows()).toHaveLength(1)
    })
    expect(router.state.location.href).toContain('type=video')
    const chips = screen.getByRole('group', { name: 'Active filters' })
    await u.click(within(chips).getByRole('button', { name: 'Remove filter Video' }))
    await waitFor(() => {
      expect(rows()).toHaveLength(2)
    })
    expect(router.state.location.href).not.toContain('type=')
  })

  it('clicking a tag on a row filters by it, and Clear all removes every filter', async () => {
    signedInSession()
    fakeApi({
      resources: [
        makeResource({ title: 'Tagged', tags: ['postgres'] }),
        makeResource({ title: 'Plain' }),
      ],
    })
    renderApp('/library')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Filter by tag postgres' }))
    await waitFor(() => {
      expect(rows()).toHaveLength(1)
    })
    expect(
      within(screen.getByRole('group', { name: 'Active filters' })).getByText('#postgres'),
    ).toBeInTheDocument()

    await u.click(screen.getByRole('button', { name: 'Clear all' }))
    await waitFor(() => {
      expect(rows()).toHaveLength(2)
    })
  })

  it('offers Relevance only while there is a search query', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource()] })
    renderApp('/library')
    const u = userEvent.setup()
    await u.click(await screen.findByRole('combobox', { name: 'Sort by' }))
    expect(screen.queryByRole('option', { name: /Relevance/ })).not.toBeInTheDocument()
    expect(screen.getByRole('option', { name: /Newest/ })).toBeInTheDocument()
  })

  it('filters by collection and by favourites and archived', async () => {
    signedInSession()
    const col = makeCollection({ name: 'Work' })
    const db = fakeApi({
      collections: [col],
      resources: [
        makeResource({ title: 'In work', collection_id: col.id }),
        makeResource({ title: 'Loved', is_favourite: true }),
        makeResource({ title: 'Old', is_archived: true }),
      ],
    })
    renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('button', { name: 'In work' })

    await selectOption(u, screen.getByRole('combobox', { name: 'Collection' }), /Work/)
    await waitFor(() => {
      expect(rows()).toHaveLength(1)
    })
    expect(db.last('GET', '/resources')?.query.get('collection_id')).toBe(col.id)

    await u.click(screen.getByRole('button', { name: 'Clear all' }))
    await u.click(screen.getByRole('button', { name: 'Favourites' }))
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Loved' })).toBeInTheDocument()
    })
    expect(rows()).toHaveLength(1)

    await u.click(screen.getByRole('button', { name: 'Favourites' }))
    await u.click(screen.getByRole('button', { name: 'Archived' }))
    expect(await screen.findByRole('button', { name: 'Old' })).toBeInTheDocument()
  })
})

describe('pagination', () => {
  it('moves between pages and changes the page size', async () => {
    signedInSession()
    const many = Array.from({ length: 25 }, (_, i) =>
      makeResource({
        title: `Item ${(i + 100).toString()}`,
        created_at: new Date(Date.now() - i * 60_000).toISOString(),
      }),
    )
    const db = fakeApi({ resources: many })
    const { router } = renderApp('/library')
    const u = userEvent.setup()

    expect(await screen.findByText('Page 1 of 2')).toBeInTheDocument()
    expect(rows()).toHaveLength(20)
    expect(screen.getByRole('button', { name: 'Prev' })).toBeDisabled()

    await u.click(screen.getByRole('button', { name: 'Next' }))
    expect(await screen.findByText('Page 2 of 2')).toBeInTheDocument()
    await waitFor(() => {
      expect(rows()).toHaveLength(5)
    })
    expect(router.state.location.href).toContain('page=2')
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()

    await selectOption(
      u,
      screen.getByRole('combobox', { name: 'Resources per page' }),
      '50 per page',
    )
    await waitFor(() => {
      expect(rows()).toHaveLength(25)
    })
    expect(db.last('GET', '/resources')?.query.get('page_size')).toBe('50')
    expect(screen.getByText('Page 1 of 1')).toBeInTheDocument()
  })
})

describe('row actions', () => {
  it('favourites at once, then confirms with the server', async () => {
    signedInSession()
    const db = fakeApi({ resources: [makeResource({ title: 'Star me' })] })
    renderApp('/library')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Add Star me to favourites' }))

    expect(
      await screen.findByRole('button', { name: 'Remove Star me from favourites' }),
    ).toHaveAttribute('aria-pressed', 'true')
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ is_favourite: true })
    })
  })

  it('rolls the favourite back and says so when the server refuses', async () => {
    signedInSession()
    const db = fakeApi({ resources: [makeResource({ title: 'Star me' })] })
    db.failNext('PATCH', '/resources/:id', 500, 'internal_error')
    renderApp('/library')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Add Star me to favourites' }))

    expect(
      await screen.findByText('Something went wrong on our side. Try again in a moment.'),
    ).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Add Star me to favourites' })).toHaveAttribute(
        'aria-pressed',
        'false',
      )
    })
  })

  it('deletes at once with an Undo that restores it', async () => {
    signedInSession()
    const db = fakeApi({
      resources: [makeResource({ title: 'Doomed' }), makeResource({ title: 'Safe' })],
    })
    renderApp('/library')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Actions for Doomed' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete' }))

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Doomed' })).not.toBeInTheDocument()
    })
    expect(await screen.findByText("Deleted 'Doomed'")).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Undo' }))

    expect(await screen.findByRole('button', { name: 'Doomed' })).toBeInTheDocument()
    expect(db.last('POST', '/resources/:id/restore')).toBeDefined()
  })

  it('archives and moves from the row menu', async () => {
    signedInSession()
    const col = makeCollection({ name: 'Target' })
    const db = fakeApi({ collections: [col], resources: [makeResource({ title: 'Mover' })] })
    renderApp('/library')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Actions for Mover' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Move to…' }))
    const dialog = await screen.findByRole('dialog', { name: 'Move resource' })
    await selectOption(u, within(dialog).getByRole('combobox'), /Target/)
    await u.click(within(dialog).getByRole('button', { name: 'Move resource' }))

    await waitFor(() => {
      expect(db.last('POST', '/resources/bulk')?.body).toMatchObject({
        action: 'move',
        collection_id: col.id,
      })
    })
    await u.click(await screen.findByRole('button', { name: 'Actions for Mover' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Archive' }))
    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Mover' })).not.toBeInTheDocument()
    })
    expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ is_archived: true })
  })
})

describe('selection and bulk actions', () => {
  it('replaces the filters with a bulk bar and moves the selection', async () => {
    signedInSession()
    const col = makeCollection({ name: 'Bucket' })
    const db = fakeApi({
      collections: [col],
      resources: [
        makeResource({ title: 'One' }),
        makeResource({ title: 'Two' }),
        makeResource({ title: 'Three' }),
      ],
    })
    renderApp('/library')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('checkbox', { name: 'Select One' }))
    await u.click(screen.getByRole('checkbox', { name: 'Select Two' }))

    const bar = screen.getByRole('toolbar', { name: 'Bulk actions' })
    expect(within(bar).getByText('2 selected')).toBeInTheDocument()
    expect(screen.queryByRole('searchbox', { name: 'Search your library' })).not.toBeInTheDocument()
    await u.click(within(bar).getByRole('button', { name: 'Move to…' }))
    const dialog = await screen.findByRole('dialog', { name: 'Move 2 resources' })
    await selectOption(u, within(dialog).getByRole('combobox'), /Bucket/)
    await u.click(within(dialog).getByRole('button', { name: 'Move 2 resources' }))

    await waitFor(() => {
      expect(db.last('POST', '/resources/bulk')?.body).toMatchObject({
        action: 'move',
        collection_id: col.id,
      })
    })
    expect((db.last('POST', '/resources/bulk')?.body as { ids: string[] }).ids).toHaveLength(2)
    await waitFor(() => {
      expect(screen.queryByRole('toolbar', { name: 'Bulk actions' })).not.toBeInTheDocument()
    })
  })

  it('adds and removes tags in bulk', async () => {
    signedInSession()
    const db = fakeApi({
      resources: [
        makeResource({ title: 'One', tags: ['old'] }),
        makeResource({ title: 'Two', tags: ['old'] }),
      ],
    })
    renderApp('/library')
    const u = userEvent.setup()
    await u.click(await screen.findByRole('checkbox', { name: 'Select One' }))
    await u.click(screen.getByRole('checkbox', { name: 'Select Two' }))

    await u.click(screen.getByRole('button', { name: 'Add tags' }))
    const dialog = await screen.findByRole('dialog', { name: 'Add tags on 2 resources' })
    const input = within(dialog).getByPlaceholderText('Add a tag…')
    await u.type(input, 'Fresh Tag{Enter}')
    await u.click(within(dialog).getByRole('button', { name: 'Add 1 tag' }))

    await waitFor(() => {
      expect(db.last('POST', '/resources/bulk')?.body).toMatchObject({
        action: 'add_tags',
        tags: ['fresh tag'],
      })
    })
    await waitFor(() => {
      expect(
        within(rows()[0]!).getByRole('button', { name: 'Filter by tag fresh tag' }),
      ).toBeInTheDocument()
    })
  })

  it('deletes a few at once with Undo, and asks first beyond ten', async () => {
    signedInSession()
    const many = Array.from({ length: 12 }, (_, i) =>
      makeResource({ title: `Bulk ${(i + 10).toString()}` }),
    )
    const db = fakeApi({ resources: many })
    renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('button', { name: 'Bulk 10' })

    await u.click(screen.getByRole('checkbox', { name: 'Select Bulk 10' }))
    await u.click(screen.getByRole('button', { name: 'Delete' }))
    expect(await screen.findByText('Deleted 1 resource')).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Undo' }))
    await waitFor(() => {
      expect(db.last('POST', '/resources/bulk')?.body).toMatchObject({ action: 'restore' })
    })

    for (const r of many)
      await u.click(await screen.findByRole('checkbox', { name: `Select ${r.title ?? ''}` }))
    await u.click(screen.getByRole('button', { name: 'Delete' }))
    const confirm = await screen.findByRole('alertdialog', { name: 'Delete 12 resources?' })
    await u.click(within(confirm).getByRole('button', { name: 'Delete 12 resources' }))
    await waitFor(() => {
      expect(db.resources).toHaveLength(0)
    })
  })

  it('Cancel clears the selection', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource({ title: 'One' })] })
    renderApp('/library')
    const u = userEvent.setup()
    await u.click(await screen.findByRole('checkbox', { name: 'Select One' }))
    await u.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(screen.getByRole('searchbox', { name: 'Search your library' })).toBeInTheDocument()
  })
})

describe('keyboard shortcuts', () => {
  it('J and K move through the list, X selects, F favourites, Enter opens', async () => {
    signedInSession()
    const db = fakeApi({
      resources: [makeResource({ title: 'First' }), makeResource({ title: 'Second' })],
    })
    const { router } = renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('button', { name: 'First' })

    await u.keyboard('j')
    expect(rows()[0]).toHaveAttribute('data-active')
    await u.keyboard('j')
    expect(rows()[1]).toHaveAttribute('data-active')
    await u.keyboard('k')
    expect(rows()[0]).toHaveAttribute('data-active')

    await u.keyboard('x')
    expect(screen.getByRole('checkbox', { name: 'Select First' })).toBeChecked()
    await u.keyboard('f')
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ is_favourite: true })
    })
    await u.keyboard('{Enter}')
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ r: db.resources[0]?.id })
    })
  })

  it('does nothing while typing in a field', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource({ title: 'First' })] })
    renderApp('/library')
    const u = userEvent.setup()
    await u.click(await screen.findByRole('searchbox', { name: 'Search your library' }))
    await u.keyboard('jjx')
    expect(rows()[0]).not.toHaveAttribute('data-active')
    expect(screen.getByRole('searchbox', { name: 'Search your library' })).toHaveValue('jjx')
  })

  it('can be switched off in settings, because single keys are a barrier for some users (WCAG 2.1.4)', async () => {
    signedInSession()
    fakeApi({ resources: [makeResource({ title: 'First' })] })
    localStorage.setItem('kh_shortcuts', 'off')
    renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('button', { name: 'First' })
    await u.keyboard('j')
    expect(rows()[0]).not.toHaveAttribute('data-active')
    await u.keyboard('n')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('? lists the shortcuts', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    await screen.findByRole('heading', { name: 'Library' })
    await u.keyboard('?')
    expect(await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })).toBeInTheDocument()
  })
})
