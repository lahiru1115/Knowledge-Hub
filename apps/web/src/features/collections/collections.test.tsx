import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeCollection, makeResource } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'

type User = ReturnType<typeof userEvent.setup>

async function selectOption(u: User, trigger: HTMLElement, option: string | RegExp) {
  await u.click(trigger)
  await u.click(await screen.findByRole('option', { name: option }))
}

function collectionRows() {
  return Array.from(screen.getByRole('list', { name: 'Collections' }).children) as HTMLElement[]
}

describe('collections list', () => {
  it('lists collections with counts and a descriptive Inbox row', async () => {
    signedInSession()
    const a = makeCollection({
      name: 'Databases',
      icon: '📘',
      description: 'Notes on Postgres',
      is_pinned: true,
    })
    const b = makeCollection({ name: 'Algorithms' })
    fakeApi({
      collections: [a, b],
      resources: [
        makeResource({ collection_id: a.id }),
        makeResource({ collection_id: a.id }),
        makeResource(),
      ],
    })

    renderApp('/collections')

    expect(await screen.findByRole('heading', { name: 'Collections' })).toBeInTheDocument()
    await waitFor(() => {
      expect(collectionRows()).toHaveLength(3)
    })
    const [first, second, inbox] = collectionRows()
    expect(within(first!).getByRole('link', { name: /Databases/ })).toHaveAttribute(
      'href',
      `/collections/${a.id}`,
    )
    expect(within(first!).getByText('(pinned)')).toBeInTheDocument()
    expect(within(first!).getByText('Notes on Postgres')).toBeInTheDocument()
    expect(within(first!).getByText('2 resources')).toBeInTheDocument()
    expect(within(second!).getByText('0 resources')).toBeInTheDocument()
    expect(within(inbox!).getByRole('link', { name: 'Inbox' })).toHaveAttribute(
      'href',
      '/library?collection=inbox',
    )
    expect(within(inbox!).getByText('1 resource')).toBeInTheDocument()
  })

  it('explains what collections are when there are none', async () => {
    signedInSession()
    fakeApi()
    renderApp('/collections')
    expect(
      await screen.findByText('Group related resources, for example one collection per course.'),
    ).toBeInTheDocument()
    expect(
      within(await screen.findByRole('list', { name: 'Collections' })).getByRole('link', {
        name: 'Inbox',
      }),
    ).toBeInTheDocument()
  })
})

describe('creating and editing', () => {
  it('creates a collection with a name, description, emoji and colour', async () => {
    signedInSession()
    const db = fakeApi()
    renderApp('/collections')
    const u = userEvent.setup()
    await u.click(
      within(await screen.findByRole('main')).getByRole('button', { name: 'New collection' }),
    )
    const dialog = await screen.findByRole('dialog', { name: 'New collection' })

    await u.type(within(dialog).getByLabelText('Name'), '  Reading list ')
    await u.type(within(dialog).getByLabelText('Description (optional)'), 'Books')
    await u.click(within(dialog).getByRole('button', { name: 'Use 📚' }))
    await u.click(within(dialog).getByRole('radio', { name: 'teal' }))
    await u.click(within(dialog).getByRole('button', { name: 'Create collection' }))

    await waitFor(() => {
      expect(db.last('POST', '/collections')?.body).toEqual({
        name: 'Reading list',
        description: 'Books',
        icon: '📚',
        color: 'teal',
      })
    })
    expect(
      await within(screen.getByRole('main')).findByRole('link', { name: /Reading list/ }),
    ).toBeInTheDocument()
  })

  it('needs a name', async () => {
    signedInSession()
    const db = fakeApi()
    renderApp('/collections')
    const u = userEvent.setup()
    await u.click(
      within(await screen.findByRole('main')).getByRole('button', { name: 'New collection' }),
    )
    const dialog = await screen.findByRole('dialog', { name: 'New collection' })

    await u.click(within(dialog).getByRole('button', { name: 'Create collection' }))

    expect(await within(dialog).findByText('Enter a name.')).toBeInTheDocument()
    expect(within(dialog).getByLabelText('Name')).toHaveAttribute('aria-invalid', 'true')
    expect(db.last('POST', '/collections')).toBeUndefined()
  })

  it('shows "already have a collection" under the name when it is taken', async () => {
    signedInSession()
    const db = fakeApi({ collections: [makeCollection({ name: 'Taken' })] })
    db.failNext('POST', '/collections', 409, 'collection_name_taken')
    renderApp('/collections')
    const u = userEvent.setup()
    await u.click(
      within(await screen.findByRole('main')).getByRole('button', { name: 'New collection' }),
    )
    const dialog = await screen.findByRole('dialog', { name: 'New collection' })

    await u.type(within(dialog).getByLabelText('Name'), 'taken')
    await u.click(within(dialog).getByRole('button', { name: 'Create collection' }))

    expect(
      await within(dialog).findByText("You already have a collection called 'taken'."),
    ).toBeInTheDocument()
    expect(dialog).toBeInTheDocument()
  })

  it('edits a collection and pins it', async () => {
    signedInSession()
    const c = makeCollection({ name: 'Old name' })
    const db = fakeApi({ collections: [c] })
    renderApp('/collections')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Actions for Old name' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Edit' }))
    const dialog = await screen.findByRole('dialog', { name: 'Edit collection' })
    const name = within(dialog).getByLabelText('Name')
    await u.clear(name)
    await u.type(name, 'New name')
    await u.click(within(dialog).getByRole('button', { name: 'Save changes' }))
    await waitFor(() => {
      expect(db.last('PATCH', '/collections/:id')?.body).toMatchObject({ name: 'New name' })
    })

    await u.click(await screen.findByRole('button', { name: 'Actions for New name' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Pin' }))
    await waitFor(() => {
      expect(db.last('PATCH', '/collections/:id')?.body).toEqual({ is_pinned: true })
    })
  })

  it('reorders with Move up and Move down (the keyboard alternative to dragging)', async () => {
    signedInSession()
    const a = makeCollection({ name: 'Alpha' })
    const b = makeCollection({ name: 'Beta' })
    const c = makeCollection({ name: 'Gamma' })
    const db = fakeApi({ collections: [a, b, c] })
    renderApp('/collections')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Actions for Gamma' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Move up' }))

    await waitFor(() => {
      expect(db.last('PUT', '/collections/order')?.body).toEqual({ ids: [a.id, c.id, b.id] })
    })
    await waitFor(() => {
      expect(within(collectionRows()[1]!).getByRole('link', { name: /Gamma/ })).toBeInTheDocument()
    })

    await u.click(screen.getByRole('button', { name: 'Actions for Alpha' }))
    expect(await screen.findByRole('menuitem', { name: 'Move up' })).toHaveAttribute(
      'aria-disabled',
      'true',
    )
  })
})

describe('deleting', () => {
  it('asks what to do with the resources and moves them to the Inbox by default', async () => {
    signedInSession()
    const c = makeCollection({ name: 'Databases' })
    const db = fakeApi({
      collections: [c],
      resources: [makeResource({ collection_id: c.id }), makeResource({ collection_id: c.id })],
    })
    renderApp('/collections')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Actions for Databases' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete…' }))
    const dialog = await screen.findByRole('dialog', { name: /Delete .*Databases/ })
    expect(
      within(dialog).getByText('It contains 2 resources. What should happen to them?'),
    ).toBeInTheDocument()
    expect(within(dialog).getByRole('radio', { name: 'Move them to' })).toBeChecked()
    await u.click(within(dialog).getByRole('button', { name: 'Delete' }))

    await waitFor(() => {
      expect(db.last('DELETE', '/collections/:id')?.query.get('resources')).toBe('move')
    })
    expect(db.last('DELETE', '/collections/:id')?.query.has('target_collection_id')).toBe(false)
    expect(db.resources).toHaveLength(2) // still there, in the Inbox
    expect(await screen.findByText("Deleted 'Databases'")).toBeInTheDocument()
  })

  it('can move them to another collection, or delete them too', async () => {
    signedInSession()
    const a = makeCollection({ name: 'Source' })
    const b = makeCollection({ name: 'Elsewhere' })
    const db = fakeApi({ collections: [a, b], resources: [makeResource({ collection_id: a.id })] })
    renderApp('/collections')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Actions for Source' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete…' }))
    let dialog = await screen.findByRole('dialog', { name: /Delete .*Source/ })
    await selectOption(
      u,
      within(dialog).getByRole('combobox', { name: 'Move resources to' }),
      /Elsewhere/,
    )
    expect(within(dialog).queryByRole('option', { name: /Source/ })).not.toBeInTheDocument()
    await u.click(within(dialog).getByRole('button', { name: 'Delete' }))
    await waitFor(() => {
      expect(db.last('DELETE', '/collections/:id')?.query.get('target_collection_id')).toBe(b.id)
    })

    await u.click(await screen.findByRole('button', { name: 'Actions for Elsewhere' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete…' }))
    dialog = await screen.findByRole('dialog', { name: /Delete .*Elsewhere/ })
    await u.click(within(dialog).getByRole('radio', { name: 'Delete them too' }))
    await u.click(within(dialog).getByRole('button', { name: 'Delete' }))
    await waitFor(() => {
      expect(db.last('DELETE', '/collections/:id')?.query.get('resources')).toBe('delete')
    })
    await waitFor(() => {
      expect(db.resources).toHaveLength(0)
    })
  })

  it('does not ask about resources when the collection is empty', async () => {
    signedInSession()
    fakeApi({ collections: [makeCollection({ name: 'Empty' })] })
    renderApp('/collections')
    const u = userEvent.setup()
    await u.click(await screen.findByRole('button', { name: 'Actions for Empty' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete…' }))
    const dialog = await screen.findByRole('dialog', { name: /Delete .*Empty/ })
    expect(within(dialog).getByText('It has no resources.')).toBeInTheDocument()
    expect(within(dialog).queryByRole('radio')).not.toBeInTheDocument()
  })
})

describe('collection page', () => {
  it("shows the collection's own header and only its resources", async () => {
    signedInSession()
    const c = makeCollection({ name: 'Databases', icon: '📘', description: 'Notes on Postgres' })
    const db = fakeApi({
      collections: [c],
      resources: [
        makeResource({ title: 'Inside', collection_id: c.id }),
        makeResource({ title: 'Outside' }),
      ],
    })

    renderApp(`/collections/${c.id}`)

    expect(await screen.findByRole('heading', { name: '📘 Databases' })).toBeInTheDocument()
    expect(screen.getByText('Notes on Postgres')).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Inside' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Outside' })).not.toBeInTheDocument()
    expect(db.last('GET', '/resources')?.query.get('collection_id')).toBe(c.id)
    expect(screen.queryByRole('combobox', { name: 'Collection' })).not.toBeInTheDocument() // fixed by the page
  })

  it('has an empty state that points to Move to… and a way to add', async () => {
    signedInSession()
    const c = makeCollection({ name: 'Empty one' })
    fakeApi({ collections: [c] })
    renderApp(`/collections/${c.id}`)
    expect(await screen.findByText('Nothing here yet.')).toBeInTheDocument()
    expect(screen.getByText(/Move to…/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Add a resource/ })).toBeInTheDocument()
  })

  it('says so when the collection does not exist or is not yours', async () => {
    signedInSession()
    fakeApi()
    renderApp('/collections/not-here')
    expect(
      await screen.findByText("This collection doesn't exist or was deleted."),
    ).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Back to collections' })).toBeInTheDocument()
  })

  it('edits from the header, and deleting returns to the list', async () => {
    signedInSession()
    const c = makeCollection({ name: 'Temp' })
    const db = fakeApi({ collections: [c] })
    const { router } = renderApp(`/collections/${c.id}`)
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: /Edit/ }))
    const edit = await screen.findByRole('dialog', { name: 'Edit collection' })
    await u.click(within(edit).getByRole('button', { name: 'Cancel' }))

    await u.click(screen.getByRole('button', { name: /Delete…/ }))
    const dialog = await screen.findByRole('dialog', { name: /Delete .*Temp/ })
    await u.click(within(dialog).getByRole('button', { name: 'Delete' }))

    await waitFor(() => {
      expect(db.last('DELETE', '/collections/:id')).toBeDefined()
    })
    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/collections')
    })
  })
})
