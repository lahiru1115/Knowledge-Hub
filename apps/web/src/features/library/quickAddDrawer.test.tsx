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

async function openQuickAdd(u: User) {
  await u.click(await screen.findByRole('button', { name: 'Add' }))
  return screen.findByRole('dialog', { name: 'Add a resource' })
}

describe('quick-add', () => {
  it('opens from the Add button and from N, and closes with Escape', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    await openQuickAdd(u)
    await u.keyboard('{Escape}')
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })

    await u.keyboard('n')
    expect(await screen.findByRole('dialog', { name: 'Add a resource' })).toBeInTheDocument()
  })

  it('rejects something that is not an http(s) link and keeps Save disabled', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'javascript:alert(1)')
    await u.tab()

    expect(
      await within(dialog).findByText('Enter a link that starts with http:// or https://'),
    ).toBeInTheDocument()
    expect(within(dialog).getByLabelText('URL')).toHaveAttribute('aria-invalid', 'true')
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled()
  })

  it('saves a link to the Inbox, tells the user where it went, and the new item appears in the list', async () => {
    signedInSession()
    const db = fakeApi({ resources: [makeResource({ title: 'Existing' })] })
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/new-thing')
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))

    expect(await screen.findByText('Saved to Inbox')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Undo' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open' })).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(db.last('POST', '/resources')?.body).toEqual({
      url: 'https://example.org/new-thing',
      tags: [],
      collection_id: null, // no type: the fetched details decide it unless the user picks one
    })
    expect(
      await screen.findByRole('button', { name: 'https://example.org/new-thing' }),
    ).toBeInTheDocument()
  })

  it('Enter saves from the URL field', async () => {
    signedInSession()
    const db = fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/enter{Enter}')

    await waitFor(() => {
      expect(db.last('POST', '/resources')).toBeDefined()
    })
  })

  it('adds tags (typed, suggested from existing ones, and removable), a collection and the optional details', async () => {
    signedInSession()
    const col = makeCollection({ name: 'Databases', icon: '📘' })
    const db = fakeApi({ collections: [col], resources: [makeResource({ tags: ['postgres'] })] })
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/full')
    const tagBox = within(dialog).getByPlaceholderText('Add a tag…')
    await u.type(tagBox, 'Brand New{Enter}')
    await u.type(tagBox, 'postg')
    await u.click(await within(dialog).findByRole('option', { name: '#postgres' }))
    await u.type(tagBox, 'oops{Enter}')
    await u.click(within(dialog).getByRole('button', { name: 'Remove tag oops' }))
    await selectOption(u, within(dialog).getByRole('combobox', { name: 'Collection' }), /Databases/)
    await u.click(within(dialog).getByRole('button', { name: /More details/ }))
    await u.type(within(dialog).getByLabelText('Title'), 'My title')
    await u.type(within(dialog).getByLabelText('Notes'), 'some notes')
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(db.last('POST', '/resources')).toBeDefined()
    })
    expect(db.last('POST', '/resources')?.body).toMatchObject({
      url: 'https://example.org/full',
      title: 'My title',
      notes: 'some notes',
      collection_id: col.id,
      tags: ['brand new', 'postgres'],
    })
    expect(await screen.findByText('Saved to Databases')).toBeInTheDocument()
  })

  it('warns about a duplicate, offers to open it or save anyway, and blocks a plain save', async () => {
    signedInSession()
    const col = makeCollection({ name: 'Databases' })
    const existing = makeResource({
      title: 'Already saved',
      url: 'https://example.org/dupe',
      collection_id: col.id,
    })
    const db = fakeApi({ collections: [col], resources: [existing] })
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/dupe')

    expect(
      await within(dialog).findByText(/You saved this on .* in Databases\./),
    ).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled()
    await u.click(within(dialog).getByRole('button', { name: 'Save anyway' }))
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled()
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(db.last('POST', '/resources')?.body).toMatchObject({ url: 'https://example.org/dupe' })
    })
  })

  it('"Open it" goes to the existing resource', async () => {
    signedInSession()
    const existing = makeResource({ title: 'Already saved', url: 'https://example.org/dupe' })
    fakeApi({ resources: [existing] })
    const { router } = renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/dupe')

    await u.click(await within(dialog).findByRole('button', { name: 'Open it' }))

    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ r: existing.id })
    })
  })

  it('stays open with everything typed when saving fails', async () => {
    signedInSession()
    const db = fakeApi()
    db.failNext('POST', '/resources', 500, 'internal_error')
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/retry')
    await u.type(within(dialog).getByPlaceholderText('Add a tag…'), 'keepme{Enter}')

    await u.click(within(dialog).getByRole('button', { name: 'Save' }))

    expect(
      await screen.findByText('Something went wrong on our side. Try again in a moment.'),
    ).toBeInTheDocument()
    expect(within(dialog).getByLabelText('URL')).toHaveValue('https://example.org/retry')
    expect(within(dialog).getByRole('button', { name: 'Remove tag keepme' })).toBeInTheDocument()
    await u.click(within(dialog).getByRole('button', { name: 'Save' })) // retry works
    await waitFor(() => {
      expect(db.all('POST', '/resources')).toHaveLength(2)
    })
  })

  it('Undo on the toast deletes what was just saved', async () => {
    signedInSession()
    const db = fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/oops{Enter}')

    await u.click(await screen.findByRole('button', { name: 'Undo' }))

    await waitFor(() => {
      expect(db.last('DELETE', '/resources/:id')).toBeDefined()
    })
  })

  it('rejects a bad tag name with a message', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)
    await u.type(within(dialog).getByPlaceholderText('Add a tag…'), 'bad/tag')
    expect(await within(dialog).findByText(/Tags use letters, digits, spaces/)).toBeInTheDocument()
  })
})

describe('resource drawer', () => {
  async function openDrawer(
    resource = makeResource({
      title: 'Deep dive',
      url: 'https://example.org/deep',
      notes: 'Original notes',
      tags: ['one', 'two'],
      type: 'video',
    }),
    extra: Parameters<typeof fakeApi>[0] = {},
  ) {
    signedInSession()
    const db = fakeApi({ resources: [resource], ...extra })
    const view = renderApp(`/library?r=${resource.id}`)
    const drawer = await screen.findByRole('dialog', { name: 'Resource' })
    await within(drawer).findByRole('heading', { name: 'Deep dive' })
    return { db, drawer, resource, ...view, u: userEvent.setup() }
  }

  it('opens from a row, shows the details, and closing removes it from the URL', async () => {
    signedInSession()
    const r = makeResource({ title: 'Open me' })
    fakeApi({ resources: [r] })
    const { router } = renderApp('/library')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Open me' }))
    const drawer = await screen.findByRole('dialog', { name: 'Resource' })
    expect(await within(drawer).findByRole('heading', { name: 'Open me' })).toBeInTheDocument()
    expect(router.state.location.search).toMatchObject({ r: r.id })

    await u.keyboard('{Escape}')
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    })
    expect(router.state.location.search).not.toHaveProperty('r')
  })

  it('shows the link safely, the type, collection and tags', async () => {
    const col = makeCollection({ name: 'Videos' })
    const { drawer } = await openDrawer(
      makeResource({
        title: 'Deep dive',
        url: 'https://example.org/deep',
        tags: ['one', 'two'],
        type: 'video',
        collection_id: col.id,
      }),
      { collections: [col] },
    )

    const link = within(drawer).getByRole('link', { name: 'https://example.org/deep' })
    expect(link).toHaveAttribute('href', 'https://example.org/deep')
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', expect.stringContaining('noopener'))
    expect(within(drawer).getByRole('combobox', { name: 'Type' })).toHaveTextContent('Video')
    expect(within(drawer).getByRole('combobox', { name: 'Collection' })).toHaveTextContent('Videos')
    expect(within(drawer).getByRole('button', { name: 'Remove tag one' })).toBeInTheDocument()
    expect(within(drawer).getByRole('button', { name: 'Remove tag two' })).toBeInTheDocument()
  })

  it('edits the title inline and saves on Enter', async () => {
    const { db, drawer, u } = await openDrawer()
    await u.click(within(drawer).getByRole('button', { name: 'Edit title' }))
    const input = within(drawer).getByLabelText('Title')
    await u.clear(input)
    await u.type(input, 'A better title{Enter}')

    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ title: 'A better title' })
    })
    expect(
      await within(drawer).findByRole('heading', { name: 'A better title' }),
    ).toBeInTheDocument()
  })

  it('Escape while editing the title cancels the edit and leaves the drawer open', async () => {
    const { db, drawer, u } = await openDrawer()
    await u.click(within(drawer).getByRole('button', { name: 'Edit title' }))
    await u.type(within(drawer).getByLabelText('Title'), 'xxx')
    await u.keyboard('{Escape}')

    expect(within(drawer).getByRole('heading', { name: 'Deep dive' })).toBeInTheDocument()
    expect(db.last('PATCH', '/resources/:id')).toBeUndefined()
  })

  it('adds and removes tags, saving the whole set each time', async () => {
    const { db, drawer, u } = await openDrawer()

    await u.click(within(drawer).getByRole('button', { name: 'Remove tag one' }))
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ tags: ['two'] })
    })

    await u.type(within(drawer).getByPlaceholderText('Add a tag…'), 'three{Enter}')
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ tags: ['two', 'three'] })
    })
  })

  it('moves to a collection, changes the type and toggles the favourite', async () => {
    const col = makeCollection({ name: 'Target' })
    const { db, drawer, u } = await openDrawer(undefined, { collections: [col] })

    await selectOption(u, within(drawer).getByRole('combobox', { name: 'Collection' }), /Target/)
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ collection_id: col.id })
    })

    await selectOption(u, within(drawer).getByRole('combobox', { name: 'Type' }), 'PDF')
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ type: 'pdf' })
    })

    await u.click(within(drawer).getByRole('button', { name: 'Add to favourites' }))
    await waitFor(() => {
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ is_favourite: true })
    })
  })

  it('edits the link and shows a field error when the server refuses it', async () => {
    const { db, drawer, u } = await openDrawer()
    db.failNext('PATCH', '/resources/:id', 400, 'validation_failed', {
      errors: { url: ['Enter a link that starts with http:// or https://'] },
    })

    await u.click(within(drawer).getByRole('button', { name: 'Edit link' }))
    const input = within(drawer).getByLabelText('Link')
    await u.clear(input)
    await u.type(input, 'ftp://nope')
    await u.click(within(drawer).getByRole('button', { name: 'Save link' }))

    expect(
      await within(drawer).findByText('Enter a link that starts with http:// or https://'),
    ).toBeInTheDocument()
    expect(input).toHaveAttribute('aria-invalid', 'true')
  })

  describe('notes', () => {
    it('autosave a second after typing stops, with a Saving and Saved status, using the last known update time', async () => {
      const { db, resource, drawer, u } = await openDrawer()
      const notes = within(drawer).getByLabelText('Notes (Markdown)')
      await u.type(notes, ' and more')

      expect(db.last('PATCH', '/resources/:id')).toBeUndefined() // not yet: still typing
      await waitFor(
        () => {
          expect(db.last('PATCH', '/resources/:id')).toBeDefined()
        },
        { timeout: 3000 },
      )
      expect(db.last('PATCH', '/resources/:id')?.body).toEqual({
        notes: 'Original notes and more',
        expected_updated_at: resource.updated_at,
      })
      expect(await within(drawer).findByText('Saved ✓')).toBeInTheDocument()
    })

    it('save at once when focus leaves the box', async () => {
      const { db, drawer, u } = await openDrawer()
      await u.type(within(drawer).getByLabelText('Notes (Markdown)'), '!')
      await u.tab()
      await waitFor(
        () => {
          expect(db.last('PATCH', '/resources/:id')?.body).toMatchObject({
            notes: 'Original notes!',
          })
        },
        { timeout: 500 },
      )
    })

    it('preview renders Markdown but never raw HTML or script links', async () => {
      const evil =
        '# Heading\n\n- item one\n\n**bold** <script>window.hacked=1</script> <img src=x onerror="window.hacked=2"> [click](javascript:alert(1)) [ok](https://example.com)'
      const { drawer, u } = await openDrawer(makeResource({ title: 'Deep dive', notes: evil }))

      await u.click(within(drawer).getByRole('tab', { name: 'Preview' }))

      const preview = await within(drawer).findByRole('heading', { name: 'Heading' })
      expect(preview).toBeInTheDocument()
      expect(within(drawer).getByRole('listitem')).toHaveTextContent('item one')
      expect(drawer.querySelector('script')).toBeNull()
      expect(drawer.querySelector('img')).toBeNull()
      // The raw text is still in the editor box, but no element may carry the attribute.
      expect(drawer.querySelector('[onerror]')).toBeNull()
      const bad = within(drawer).getByText('click')
      expect(bad.getAttribute('href') ?? '').not.toContain('javascript:')
      const ok = within(drawer).getByRole('link', { name: 'ok' })
      expect(ok).toHaveAttribute('rel', expect.stringContaining('noopener'))
      expect((window as unknown as { hacked?: number }).hacked).toBeUndefined()
    })

    it('shows a notice on a conflict with Reload latest and Keep mine', async () => {
      const { db, resource, drawer, u } = await openDrawer()
      db.failNext('PATCH', '/resources/:id', 409, 'edit_conflict')
      await u.type(within(drawer).getByLabelText('Notes (Markdown)'), ' mine')
      await u.tab()

      const alert = await within(drawer).findByRole('alert')
      expect(alert).toHaveTextContent('These notes changed in another tab.')

      await u.click(within(alert).getByRole('button', { name: 'Keep mine' }))
      await waitFor(() => {
        expect(db.all('PATCH', '/resources/:id').length).toBeGreaterThanOrEqual(2)
      })
      expect(db.resources.find((r) => r.id === resource.id)?.notes).toBe('Original notes mine')
    })

    it('Reload latest replaces what was typed with the server version', async () => {
      const { db, resource, drawer, u } = await openDrawer()
      db.failNext('PATCH', '/resources/:id', 409, 'edit_conflict')
      await u.type(within(drawer).getByLabelText('Notes (Markdown)'), ' discard me')
      await u.tab()
      const alert = await within(drawer).findByRole('alert')
      db.resources = db.resources.map((r) =>
        r.id === resource.id
          ? { ...r, notes: 'Changed elsewhere', updated_at: new Date().toISOString() }
          : r,
      )

      await u.click(within(alert).getByRole('button', { name: 'Reload latest' }))

      await waitFor(() => {
        expect(within(drawer).getByLabelText('Notes (Markdown)')).toHaveValue('Changed elsewhere')
      })
    })
  })

  describe('summary', () => {
    it('can be written, then edited and removed with an Undo, and is labelled as the users own', async () => {
      const { db, drawer, u } = await openDrawer()
      expect(within(drawer).getByText('No summary yet.')).toBeInTheDocument()

      await u.click(within(drawer).getByRole('button', { name: 'Write a summary' }))
      await u.type(within(drawer).getByRole('textbox', { name: 'Summary' }), 'My own words.')
      await u.click(within(drawer).getByRole('button', { name: 'Save summary' }))

      await waitFor(() => {
        expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ summary: 'My own words.' })
      })
      expect(await within(drawer).findByText('Written by you')).toBeInTheDocument()

      await u.click(within(drawer).getByRole('button', { name: 'Remove summary' }))
      await waitFor(() => {
        expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ summary: null })
      })
      await u.click(await screen.findByRole('button', { name: 'Undo' }))
      await waitFor(() => {
        expect(db.last('PATCH', '/resources/:id')?.body).toEqual({ summary: 'My own words.' })
      })
    })

    it('labels where an existing summary came from', async () => {
      const { drawer } = await openDrawer(
        makeResource({ title: 'Deep dive', summary: 'Generated text.', summary_source: 'ai' }),
      )
      expect(within(drawer).getByText('✦ AI-generated')).toBeInTheDocument()
      expect(within(drawer).getByText('Generated text.')).toBeInTheDocument()
    })
  })

  it('deletes from the menu, closes, and offers Undo', async () => {
    const { db, drawer, u, router } = await openDrawer()
    await u.click(within(drawer).getByRole('button', { name: 'More actions' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete' }))

    await waitFor(() => {
      expect(db.last('DELETE', '/resources/:id')).toBeDefined()
    })
    expect(await screen.findByText("Deleted 'Deep dive'")).toBeInTheDocument()
    await waitFor(() => {
      expect(router.state.location.search).not.toHaveProperty('r')
    })
  })

  it('explains a resource that does not exist or is not yours', async () => {
    signedInSession()
    fakeApi()
    renderApp('/library?r=someone-elses-id')
    const drawer = await screen.findByRole('dialog', { name: 'Resource' })
    expect(
      await within(drawer).findByText("This resource doesn't exist or was deleted."),
    ).toBeInTheDocument()
    await userEvent
      .setup()
      .click(within(drawer).getAllByRole('button', { name: 'Close' })[0] as HTMLElement)
  })

  it('J and K step to the next and previous resource while it is open', async () => {
    signedInSession()
    const a = makeResource({ title: 'Alpha' })
    const b = makeResource({ title: 'Beta' })
    fakeApi({ resources: [a, b] })
    const { router } = renderApp(`/library?r=${a.id}`)
    const u = userEvent.setup()
    await screen.findByRole('heading', { name: 'Alpha', level: 2 })

    await u.keyboard('j')
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ r: b.id })
    })
    await screen.findByRole('heading', { name: 'Beta', level: 2 })
    await u.keyboard('k')
    await waitFor(() => {
      expect(router.state.location.search).toMatchObject({ r: a.id })
    })
  })
})
