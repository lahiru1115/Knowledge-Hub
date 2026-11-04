import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeResource } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'

const fresh = () => new Date().toISOString()

async function openQuickAdd(u: ReturnType<typeof userEvent.setup>) {
  await u.click(await screen.findByRole('button', { name: 'Add' }))
  return screen.findByRole('dialog', { name: 'Add a resource' })
}

describe('link preview in quick-add', () => {
  it('shows what the link looks like after the user stops typing, with one request', async () => {
    signedInSession()
    const db = fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/post')

    const card = await within(dialog).findByRole('group', { name: 'Preview' })
    expect(within(card).getByText('Preview of example.org')).toBeInTheDocument()
    expect(within(card).getByText('A short description of the page.')).toBeInTheDocument()
    expect(within(card).getByText('Article')).toBeInTheDocument()
    expect(db.all('POST', '/metadata/preview')).toHaveLength(1)
    expect(db.last('POST', '/metadata/preview')?.body).toEqual({ url: 'https://example.org/post' })
  })

  it('shows a placeholder while it loads, and does not ask for text that is not a link', async () => {
    signedInSession()
    const db = fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'not a link')
    await new Promise((r) => setTimeout(r, 600))
    expect(db.all('POST', '/metadata/preview')).toHaveLength(0)
    expect(within(dialog).queryByRole('group', { name: 'Preview' })).not.toBeInTheDocument()

    await u.clear(within(dialog).getByLabelText('URL'))
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/x')
    expect(await within(dialog).findByLabelText('Loading a preview')).toHaveAttribute(
      'aria-busy',
      'true',
    )
    await within(dialog).findByRole('group', { name: 'Preview' })
  })

  it('says it could not load a preview, and the link can still be saved', async () => {
    signedInSession()
    const db = fakeApi()
    db.failNext('POST', '/metadata/preview', 500, 'internal_error')
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)

    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/broken')

    expect(
      await within(dialog).findByText("Couldn't load a preview. You can still save it."),
    ).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled()
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(db.last('POST', '/resources')?.body).toMatchObject({
        url: 'https://example.org/broken',
      })
    })
  })

  it('treats a refused address in the preview as "no preview", not as an error', async () => {
    signedInSession()
    const db = fakeApi()
    db.preview = () => ({
      title: null,
      description: null,
      image_url: null,
      favicon_url: null,
      type: null,
      error: 'blocked_url',
    })
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/private')
    expect(await within(dialog).findByText(/Couldn't load a preview/)).toBeInTheDocument()
  })

  it('sends a type only when the user picks one', async () => {
    signedInSession()
    const db = fakeApi()
    renderApp('/library')
    const u = userEvent.setup()
    const dialog = await openQuickAdd(u)
    await u.type(within(dialog).getByLabelText('URL'), 'https://example.org/clip')
    await u.click(within(dialog).getByRole('button', { name: /More details/ }))
    expect(within(dialog).getByLabelText('Type')).toHaveTextContent('Detect automatically')
    await u.click(within(dialog).getByLabelText('Type'))
    await u.click(await screen.findByRole('option', { name: 'Video' }))
    await u.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => {
      expect(db.last('POST', '/resources')?.body).toMatchObject({ type: 'video' })
    })
  })
})

describe('fetching details', () => {
  it('shows a pending link as fetching, then fills it in when the details arrive', async () => {
    signedInSession()
    const r = makeResource({ title: null, url: 'https://example.org/new', created_at: fresh() })
    const db = fakeApi({ resources: [r] })
    renderApp('/library')

    expect(await screen.findByText('Fetching details…')).toBeInTheDocument()

    db.finishMetadata(r.id, { title: 'The fetched title', description: 'About it' })

    expect(
      await screen.findByRole('button', { name: 'The fetched title' }, { timeout: 6000 }),
    ).toBeInTheDocument()
    expect(screen.getByText('About it')).toBeInTheDocument()
    expect(screen.queryByText('Fetching details…')).not.toBeInTheDocument()
  }, 10_000)

  it('does not keep asking about a link that has been pending for a long time', async () => {
    signedInSession()
    const db = fakeApi({ resources: [makeResource({ title: 'Old one' })] }) // saved minutes ago, still pending
    renderApp('/library')
    expect(await screen.findByText('Fetching details…')).toBeInTheDocument()
    const before = db.all('GET', '/resources').length
    await new Promise((r) => setTimeout(r, 2600))
    expect(db.all('GET', '/resources')).toHaveLength(before)
  }, 10_000)

  it('shows a failure with Retry, which shows fetching at once and then the result', async () => {
    signedInSession()
    const r = makeResource({
      title: 'Flaky site',
      metadata_status: 'failed',
      metadata_error: 'timeout',
    })
    const db = fakeApi({ resources: [r] })
    renderApp('/library')
    const u = userEvent.setup()

    expect(await screen.findByText(/Couldn't fetch details/)).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Retry fetching details for Flaky site' }))

    expect(await screen.findByText('Fetching details…')).toBeInTheDocument()
    expect(db.all('POST', '/resources/:id/metadata/refresh')).toHaveLength(1)

    db.finishMetadata(r.id)
    await waitFor(
      () => {
        expect(screen.queryByText('Fetching details…')).not.toBeInTheDocument()
      },
      { timeout: 6000 },
    )
    expect(screen.queryByText(/Couldn't fetch details/)).not.toBeInTheDocument()
  }, 12_000)

  it('does not offer Retry when trying again cannot help', async () => {
    signedInSession()
    fakeApi({
      resources: [
        makeResource({
          title: 'Internal',
          metadata_status: 'failed',
          metadata_error: 'blocked_url',
        }),
      ],
    })
    renderApp('/library')
    expect(await screen.findByText(/Couldn't fetch details/)).toBeInTheDocument()
    expect(screen.getByText(/This address can't be fetched/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Retry fetching/ })).not.toBeInTheDocument()
  })

  it('puts the same status and Retry in the drawer', async () => {
    signedInSession()
    const r = makeResource({
      title: 'In drawer',
      metadata_status: 'failed',
      metadata_error: 'unreachable',
    })
    const db = fakeApi({ resources: [r] })
    renderApp(`/library?r=${r.id}`)
    const u = userEvent.setup()
    const drawer = await screen.findByRole('dialog', { name: 'Resource' })
    await within(drawer).findByRole('heading', { name: 'In drawer' })

    await u.click(within(drawer).getByRole('button', { name: /Retry fetching details/ }))

    expect(await within(drawer).findByText('Fetching details…')).toBeInTheDocument()
    expect(db.all('POST', '/resources/:id/metadata/refresh')).toHaveLength(1)
  })

  it('reports a failed retry and leaves the failure showing', async () => {
    signedInSession()
    const r = makeResource({
      title: 'Stubborn',
      metadata_status: 'failed',
      metadata_error: 'http_error',
    })
    const db = fakeApi({ resources: [r] })
    db.failNext('POST', '/resources/:id/metadata/refresh', 500, 'internal_error')
    renderApp('/library')
    const u = userEvent.setup()
    await u.click(
      await screen.findByRole('button', { name: 'Retry fetching details for Stubborn' }),
    )
    expect(await screen.findByText(/Something went wrong/)).toBeInTheDocument()
    expect(await screen.findByText(/Couldn't fetch details/)).toBeInTheDocument()
  })
})
