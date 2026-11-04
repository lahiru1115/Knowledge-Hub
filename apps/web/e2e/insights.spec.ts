import { expect, test, type Page } from '@playwright/test'
import {
  DESCRIPTION,
  api,
  password,
  register,
  savedResource,
  uniqueEmail,
  violations,
} from './support/helpers'

/** The API under test must treat this address as the administrator: `Auth__AdminEmail`. */
const ADMIN_EMAIL = process.env.E2E_ADMIN_EMAIL ?? 'e2e-admin@example.com'

async function signIn(page: Page, email: string) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

/** The administrator account is shared between runs: sign in, and register it the first time. */
async function signInAsAdmin(page: Page) {
  const welcome = page.getByRole('heading', { name: /^Welcome,/ })
  await signIn(page, ADMIN_EMAIL)
  const signedIn = await welcome.waitFor({ timeout: 5000 }).then(
    () => true,
    () => false,
  )
  if (!signedIn) {
    await page.goto('/register')
    await page.getByLabel('Email').fill(ADMIN_EMAIL)
    await page.getByLabel('Your name').fill('Admin')
    await page.getByLabel('Password', { exact: true }).fill(password)
    await page.getByRole('button', { name: 'Create account' }).click()
  }
  await expect(welcome).toBeVisible()
}

test.describe('home dashboard', () => {
  test('starts empty with a checklist, then shows the numbers and ticks the steps', async ({
    page,
  }) => {
    await register(page)
    await expect(page.getByText('Your library is empty.')).toBeVisible()
    const checklist = page.getByRole('region', { name: /Get started/ })
    await expect(checklist.getByText('Save your first resource (not done yet)')).toBeVisible()
    expect(await violations(page), 'empty home').toEqual([])

    await savedResource(page, 'Pooling guide')
    await api(page, 'POST', '/collections', { name: 'Databases' })
    await page.reload()

    const numbers = page.getByRole('region', { name: 'Your library in numbers' })
    await expect(numbers).toContainText(/1\s*resource(?!s)/)
    await expect(numbers).toContainText(/1\s*collection(?!s)/)
    await expect(page.getByRole('link', { name: 'Pooling guide' }).first()).toBeVisible()
    await expect(checklist.getByText('Save your first resource (done)')).toBeVisible()
    await expect(checklist.getByText('Create a collection (done)')).toBeVisible()
    await expect(checklist.getByText('Try a search (not done yet)')).toBeVisible()
    expect(await violations(page), 'home with data').toEqual([])
  })

  test('a search ticks its step and dismissing the checklist keeps it away', async ({ page }) => {
    await register(page)
    await savedResource(page)
    await page.goto('/library?q=pooling')
    await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible()
    await page.goto('/')
    await expect(page.getByText('Try a search (done)')).toBeVisible()

    await page.getByRole('button', { name: 'Dismiss' }).click()
    await expect(page.getByRole('heading', { name: /Get started/ })).toBeHidden()
    await page.reload()
    await expect(page.getByRole('heading', { name: /^Welcome,/ })).toBeVisible()
    await expect(page.getByRole('heading', { name: /Get started/ })).toBeHidden()
  })
})

test.describe('export', () => {
  test('downloads the library as JSON and as CSV from Settings', async ({ page }) => {
    await register(page)
    await savedResource(page, 'Pooling, "quoted" guide')
    await page.goto('/settings/data')
    await expect(page.getByRole('heading', { name: 'Your data' })).toBeVisible()
    expect(await violations(page), 'settings data').toEqual([])

    const [json] = await Promise.all([
      page.waitForEvent('download'),
      page.getByRole('link', { name: 'Export JSON' }).click(),
    ])
    expect(json.suggestedFilename()).toMatch(/^knowledge-hub-\d{4}-\d{2}-\d{2}\.json$/)
    const parsed = JSON.parse(await readDownload(json)) as {
      schema_version: number
      resources: { title: string; description: string }[]
    }
    expect(parsed.schema_version).toBe(1)
    expect(parsed.resources).toEqual([
      expect.objectContaining({ title: 'Pooling, "quoted" guide', description: DESCRIPTION }),
    ])

    const [csv] = await Promise.all([
      page.waitForEvent('download'),
      page.getByRole('link', { name: 'Export CSV' }).click(),
    ])
    expect(csv.suggestedFilename()).toMatch(/\.csv$/)
    const text = await readDownload(csv)
    expect(text.startsWith('﻿title,url,type,collection,tags,')).toBe(true)
    expect(text).toContain('"Pooling, ""quoted"" guide"')
  })

  test('the delete-account page offers a copy first', async ({ page }) => {
    await register(page)
    await page.goto('/settings/account/delete')
    await expect(page.getByRole('link', { name: 'Export JSON' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Export CSV' })).toBeVisible()
  })
})

async function readDownload(download: import('@playwright/test').Download): Promise<string> {
  const stream = await download.createReadStream()
  const chunks: Buffer[] = []
  for await (const chunk of stream) chunks.push(chunk as Buffer)
  return Buffer.concat(chunks).toString('utf8')
}

test.describe('admin', () => {
  // They share one administrator account, which the first of them creates.
  test.describe.configure({ mode: 'serial' })

  test('a member sees no Admin link and is told they have no access', async ({ page }) => {
    await register(page)
    await expect(page.getByRole('link', { name: 'Admin', exact: true })).toHaveCount(0)
    await page.goto('/admin/users')
    await expect(page.getByRole('heading', { name: "You don't have access to this" })).toBeVisible()
  })

  test('an admin finds a user, disables them, and they can no longer sign in', async ({
    page,
    browser,
  }) => {
    // The member, in a browser of their own.
    const memberContext = await browser.newContext()
    const memberPage = await memberContext.newPage()
    const memberEmail = uniqueEmail()
    await memberPage.goto('/register')
    await memberPage.getByLabel('Email').fill(memberEmail)
    await memberPage.getByLabel('Your name').fill('Grace Hopper')
    await memberPage.getByLabel('Password', { exact: true }).fill(password)
    await memberPage.getByRole('button', { name: 'Create account' }).click()
    await expect(memberPage.getByRole('heading', { name: 'Welcome, Grace Hopper' })).toBeVisible()

    await signInAsAdmin(page)
    await page
      .getByRole('navigation', { name: 'Main' })
      .getByRole('link', { name: 'Admin' })
      .click()
    await expect(page).toHaveURL(/\/admin\/users$/)
    await page.getByRole('searchbox', { name: 'Search users by email' }).fill(memberEmail)
    const row = page.getByRole('row', { name: new RegExp(memberEmail) })
    await expect(row).toBeVisible()
    await expect(row.getByText('Active')).toBeVisible()
    expect(await violations(page), 'admin users').toEqual([])

    await row.getByRole('button', { name: `Disable ${memberEmail}` }).click()
    const dialog = page.getByRole('alertdialog')
    await expect(dialog).toContainText('signed out everywhere')
    expect(await violations(page), 'disable dialog').toEqual([])
    await dialog.getByRole('button', { name: 'Disable' }).click()
    await expect(row.getByText('Disabled')).toBeVisible()

    // The member is out at once and cannot come back.
    await memberPage.goto('/library')
    await expect(memberPage).toHaveURL(/\/login/)
    await signIn(memberPage, memberEmail)
    await expect(memberPage.getByRole('alert')).toContainText(/disabled/i)

    await row.getByRole('button', { name: `Enable ${memberEmail}` }).click()
    await page.getByRole('alertdialog').getByRole('button', { name: 'Enable' }).click()
    await expect(row.getByText('Active')).toBeVisible()
    await signIn(memberPage, memberEmail)
    await expect(memberPage.getByRole('heading', { name: 'Welcome, Grace Hopper' })).toBeVisible()
    await memberContext.close()
  })

  test('the admin has no switch for their own row, and AI usage says when the AI service is off', async ({
    page,
  }) => {
    await signInAsAdmin(page)
    await page.goto('/admin/users')
    const own = page.getByRole('row', { name: new RegExp(ADMIN_EMAIL) })
    await page.getByRole('searchbox', { name: 'Search users by email' }).fill(ADMIN_EMAIL)
    await expect(own.getByText('You')).toBeVisible()
    await expect(own.getByRole('button')).toHaveCount(0)

    await page.getByRole('link', { name: 'AI usage' }).click()
    if (process.env.E2E_AI !== '1') {
      await expect(page.getByRole('alert')).toContainText("isn't answering")
    } else {
      await expect(page.getByRole('img', { name: /last 30 days/ })).toBeVisible()
    }
    expect(await violations(page), 'admin ai usage').toEqual([])
  })
})
