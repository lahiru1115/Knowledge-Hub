import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { fakeApi, makeResource } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'
import { similarTo } from './similar'

function tagRows() {
  return Array.from(screen.getByRole('list', { name: 'Tags' }).children) as HTMLElement[]
}

const seed = () => [
  makeResource({ tags: ['postgres', 'dotnet'] }),
  makeResource({ tags: ['postgres'] }),
  makeResource({ tags: ['postgresql'] }),
  makeResource({ tags: ['react'] }),
]

describe('tags manager', () => {
  it('lists tags with counts, most used first, and filters', async () => {
    signedInSession()
    fakeApi({ resources: seed() })
    renderApp('/tags')
    const u = userEvent.setup()

    expect(await screen.findByRole('heading', { name: 'Tags', level: 1 })).toBeInTheDocument()
    await waitFor(() => {
      expect(tagRows()).toHaveLength(4)
    })
    expect(within(tagRows()[0]!).getByText('#postgres')).toBeInTheDocument()
    expect(within(tagRows()[0]!).getByText('2 resources')).toBeInTheDocument()
    expect(within(tagRows()[0]!).getByRole('link', { name: '#postgres' })).toHaveAttribute(
      'href',
      '/library?tag=postgres',
    )

    await u.type(screen.getByRole('textbox', { name: 'Filter tags' }), 'dot')
    await waitFor(() => {
      expect(tagRows()).toHaveLength(1)
    })
    expect(within(tagRows()[0]!).getByText('#dotnet')).toBeInTheDocument()
  })

  it('sorts A to Z on request', async () => {
    signedInSession()
    fakeApi({ resources: seed() })
    renderApp('/tags')
    const u = userEvent.setup()
    await screen.findByRole('list', { name: 'Tags' })
    await u.click(screen.getByRole('combobox', { name: 'Sort tags' }))
    await u.click(await screen.findByRole('option', { name: 'Sort: A–Z' }))
    expect(within(tagRows()[0]!).getByText('#dotnet')).toBeInTheDocument()
  })

  it('has an empty state', async () => {
    signedInSession()
    fakeApi()
    renderApp('/tags')
    expect(await screen.findByText('No tags yet')).toBeInTheDocument()
    expect(screen.getByText('Tags appear when you add them to resources.')).toBeInTheDocument()
  })

  it('suggests merging look-alike tags, and the Merge button does it', async () => {
    signedInSession()
    const db = fakeApi({ resources: seed() })
    renderApp('/tags')
    const u = userEvent.setup()
    await screen.findByRole('list', { name: 'Tags' })

    const row = tagRows().find((r) => within(r).queryByText('#postgresql'))!
    expect(within(row).getByText(/Similar to #postgres/)).toBeInTheDocument()
    await u.click(within(row).getByRole('button', { name: 'Merge' }))

    await waitFor(() => {
      expect(db.last('POST', '/tags/:id/merge')?.body).toEqual({ target_tag_id: 'tag-postgres' })
    })
    expect(await screen.findByText('Merged #postgresql into #postgres')).toBeInTheDocument()
  })

  it('renames inline', async () => {
    signedInSession()
    const db = fakeApi({ resources: seed() })
    renderApp('/tags')
    const u = userEvent.setup()
    await screen.findByRole('list', { name: 'Tags' })

    const row = tagRows().find((r) => within(r).queryByText('#react'))!
    await u.click(within(row).getByRole('button', { name: 'Rename' }))
    const input = within(row).getByLabelText('New name for #react')
    await u.clear(input)
    await u.type(input, 'React JS{Enter}')

    await waitFor(() => {
      expect(db.last('PATCH', '/tags/:id')?.body).toEqual({ name: 'react js' })
    })
    expect(await screen.findByText('#react js')).toBeInTheDocument()
  })

  it('a rename that clashes offers to merge instead', async () => {
    signedInSession()
    const db = fakeApi({ resources: seed() })
    db.failNext('PATCH', '/tags/:id', 409, 'tag_name_taken', { suggest_merge_tag_id: 'tag-dotnet' })
    renderApp('/tags')
    const u = userEvent.setup()
    await screen.findByRole('list', { name: 'Tags' })
    const row = tagRows().find((r) => within(r).queryByText('#react'))!

    await u.click(within(row).getByRole('button', { name: 'Rename' }))
    const input = within(row).getByLabelText('New name for #react')
    await u.clear(input)
    await u.type(input, 'dotnet{Enter}')

    const alert = await within(row).findByRole('alert')
    expect(alert).toHaveTextContent('You already have #dotnet. Merge the two tags instead?')
    await u.click(within(alert).getByRole('button', { name: 'Merge' }))
    await waitFor(() => {
      expect(db.last('POST', '/tags/:id/merge')?.body).toEqual({ target_tag_id: 'tag-dotnet' })
    })
  })

  it('rejects an invalid new name without calling the server', async () => {
    signedInSession()
    const db = fakeApi({ resources: seed() })
    renderApp('/tags')
    const u = userEvent.setup()
    await screen.findByRole('list', { name: 'Tags' })
    const row = tagRows().find((r) => within(r).queryByText('#react'))!
    await u.click(within(row).getByRole('button', { name: 'Rename' }))
    const input = within(row).getByLabelText('New name for #react')
    await u.clear(input)
    await u.type(input, 'bad/name{Enter}')
    expect(await within(row).findByRole('alert')).toHaveTextContent('Use letters, digits, spaces')
    expect(db.last('PATCH', '/tags/:id')).toBeUndefined()
  })

  it('merges a selection into another tag', async () => {
    signedInSession()
    const db = fakeApi({ resources: seed() })
    renderApp('/tags')
    const u = userEvent.setup()
    await screen.findByRole('list', { name: 'Tags' })

    await u.click(screen.getByRole('checkbox', { name: 'Select #react' }))
    await u.click(screen.getByRole('checkbox', { name: 'Select #dotnet' }))
    expect(screen.getByText('2 selected')).toBeInTheDocument()
    await u.click(screen.getByRole('button', { name: 'Merge into…' }))
    const dialog = await screen.findByRole('dialog', { name: /Merge #/ })
    await u.click(within(dialog).getByRole('button', { name: 'Merge' }))

    await waitFor(() => {
      expect(db.all('POST', '/tags/:id/merge')).toHaveLength(2)
    })
  })

  it('deletes a tag with an Undo that puts it back on the same resources', async () => {
    signedInSession()
    const resources = seed()
    const db = fakeApi({ resources })
    renderApp('/tags')
    const u = userEvent.setup()
    await screen.findByRole('list', { name: 'Tags' })

    await u.click(screen.getByRole('button', { name: 'More actions for #postgres' }))
    await u.click(await screen.findByRole('menuitem', { name: 'Delete' }))

    expect(await screen.findByText('Deleted #postgres')).toBeInTheDocument()
    await waitFor(() => {
      expect(db.resources.some((r) => r.tags.includes('postgres'))).toBe(false)
    })
    await u.click(screen.getByRole('button', { name: 'Undo' }))

    await waitFor(() => {
      expect(db.last('POST', '/resources/bulk')?.body).toMatchObject({
        action: 'add_tags',
        tags: ['postgres'],
      })
    })
    const ids = (db.last('POST', '/resources/bulk')?.body as { ids: string[] }).ids
    expect(ids).toEqual([resources[0]!.id, resources[1]!.id])
    expect(db.resources.filter((r) => r.tags.includes('postgres'))).toHaveLength(2)
  })
})

describe('similarTo', () => {
  const t = (name: string, resource_count = 1) => ({ id: name, name, resource_count })

  it.each([
    ['postgresql', 'postgres'],
    ['postgres', 'postgresql'],
    ['react', 'reacts'],
    ['machine-learning', 'machine learning'],
    ['Node.js', 'nodejs'],
  ])('%s looks like %s', (a, b) => {
    expect(similarTo(t(a), [t(a), t(b)])?.name).toBe(b)
  })

  it.each([
    ['go', 'gos'],
    ['react', 'redux'],
    ['java', 'javascript'],
  ])('%s does not look like %s', (a, b) => {
    expect(similarTo(t(a), [t(a), t(b)])).toBeUndefined()
  })

  it('prefers the more used of several look-alikes', () => {
    expect(
      similarTo(t('postgresql'), [t('postgres', 2), t('postgres-sql', 9), t('postgresql')])?.name,
    ).toBe('postgres-sql')
  })
})
