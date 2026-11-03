import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { fakeApi } from '@/test/fakeApi'
import { renderApp, signedInSession } from '@/test/helpers'

describe('settings: data', () => {
  it('offers JSON and CSV downloads from the export endpoint', async () => {
    signedInSession()
    fakeApi()
    renderApp('/settings/data')
    const json = await screen.findByRole('link', { name: /export json/i })
    const csv = screen.getByRole('link', { name: /export csv/i })
    expect(json).toHaveAttribute('href', '/api/v1/export?format=json')
    expect(json).toHaveAttribute('download')
    expect(csv).toHaveAttribute('href', '/api/v1/export?format=csv')
    expect(screen.getByRole('link', { name: 'Data' })).toHaveAttribute('aria-current', 'page')
  })

  it('offers the same downloads before deleting the account', async () => {
    signedInSession()
    fakeApi()
    renderApp('/settings/account/delete')
    expect(await screen.findByRole('link', { name: /export json/i })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /export csv/i })).toBeInTheDocument()
  })
})
