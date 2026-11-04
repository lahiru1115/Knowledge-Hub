import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'
import { resetLink, waitForMail } from './support/mail-server'

const password = 'a long unusual passphrase'
const newPassword = 'another long unusual passphrase'
const uniqueEmail = () => `e2e-${crypto.randomUUID()}@example.com`

async function accessibilityViolations(page: Page) {
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa'])
    .analyze()
  return results.violations.map((v) => `${v.id}: ${v.help}`)
}

async function register(page: Page, email = uniqueEmail(), name = 'Ada Lovelace') {
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Your name').fill(name)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: `Welcome, ${name}` })).toBeVisible()
  return email
}

async function signIn(page: Page, email: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(pass)
  await page.getByRole('button', { name: 'Sign in' }).click()
}

test('forgot password: the emailed link sets a new password, once', async ({ page, context }) => {
  const email = await register(page)
  await context.clearCookies()

  await page.goto('/login')
  await page.getByRole('link', { name: 'Forgot password?' }).click()
  await page.getByLabel('Email').fill(email)
  await page.getByRole('button', { name: 'Send reset link' }).click()
  await expect(page.getByRole('heading', { name: 'Check your email' })).toBeVisible()

  const link = resetLink(await waitForMail(email))
  await page.goto(link)
  await page.getByLabel('New password', { exact: true }).fill(newPassword)
  await page.getByRole('button', { name: 'Update password' }).click()
  await expect(page.getByRole('heading', { name: 'Password updated' })).toBeVisible()

  await signIn(page, email, password)
  await expect(page.getByRole('alert')).toHaveText(
    "That email and password don't match. Try again.",
  )
  await signIn(page, email, newPassword)
  await expect(page.getByRole('heading', { name: 'Welcome, Ada Lovelace' })).toBeVisible()

  // The same link cannot be used again.
  await page.goto(link)
  await page.getByLabel('New password', { exact: true }).fill('yet another long passphrase')
  await page.getByRole('button', { name: 'Update password' }).click()
  await expect(page.getByRole('heading', { name: "This link can't be used" })).toBeVisible()
})

test('asking for a reset for an unknown email looks the same and sends nothing', async ({
  page,
}) => {
  const unknown = uniqueEmail()
  await page.goto('/forgot-password')
  await page.getByLabel('Email').fill(unknown)
  await page.getByRole('button', { name: 'Send reset link' }).click()

  await expect(page.getByRole('heading', { name: 'Check your email' })).toBeVisible()
  await new Promise((r) => setTimeout(r, 800))
  const res = await fetch(`http://127.0.0.1:8025/messages?to=${encodeURIComponent(unknown)}`)
  expect(await res.json()).toEqual([])
})

test('profile name and theme are saved on the account and follow the user to another browser', async ({
  page,
  browser,
}) => {
  const email = await register(page)

  await page
    .getByRole('navigation', { name: 'Main' })
    .getByRole('link', { name: 'Settings' })
    .click()
  await page.getByLabel('Your name').fill('Grace Hopper')
  await page.getByRole('button', { name: 'Save changes' }).click()
  await expect(page.getByText('Saved')).toBeVisible()
  await page.getByRole('link', { name: 'Appearance' }).click()
  await page.getByText('Dark', { exact: true }).click()
  await expect(page.locator('html')).toHaveClass(/dark/)

  await page.reload()
  await expect(page.locator('html')).toHaveClass(/dark/)
  await expect(page.getByRole('button', { name: 'Account menu for Grace Hopper' })).toBeVisible()

  // A fresh browser has never seen this account's theme.
  const other = await browser.newContext()
  const otherPage = await other.newPage()
  await signIn(otherPage, email, password)
  await expect(otherPage.getByRole('heading', { name: 'Welcome, Grace Hopper' })).toBeVisible()
  await expect(otherPage.locator('html')).toHaveClass(/dark/)
  await other.close()
})

test('changing the password signs the other browser out but not this one', async ({
  page,
  browser,
}) => {
  const email = await register(page)
  const other = await browser.newContext()
  const otherPage = await other.newPage()
  await signIn(otherPage, email, password)
  await expect(otherPage.getByRole('heading', { name: 'Welcome, Ada Lovelace' })).toBeVisible()

  await page.goto('/settings/account')
  await page.getByLabel('Current password', { exact: true }).fill('definitely not my password')
  await page.getByLabel('New password', { exact: true }).fill(newPassword)
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText("That password isn't right. Try again.")).toBeVisible()

  await page.getByLabel('Current password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Change password' }).click()
  await expect(page.getByText(/Password changed/)).toBeVisible()

  await page.reload()
  await expect(page.getByRole('heading', { name: 'Change password' })).toBeVisible() // still signed in
  // The access token was already cached for up to a minute elsewhere, but the refresh token is dead.
  await otherPage.context().clearCookies({ name: 'kh_at' })
  await otherPage.reload()
  await expect(otherPage.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await other.close()
})

test('deleting the account signs out, and the credentials stop working', async ({
  page,
  context,
}) => {
  const email = await register(page)

  await page.goto('/settings/account')
  await page.getByRole('link', { name: 'Delete account…' }).click()
  await page.getByLabel('Password', { exact: true }).fill('wrong wrong wrong')
  await page.getByRole('button', { name: 'Delete my account and data' }).click()
  await expect(page.getByText("That password isn't right. Try again.")).toBeVisible()

  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Delete my account and data' }).click()
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()

  await context.clearCookies()
  await signIn(page, email, password)
  await expect(page.getByRole('alert')).toHaveText(
    "That email and password don't match. Try again.",
  )
})

for (const scheme of ['light', 'dark'] as const) {
  test(`recovery and settings screens have no accessibility violations (${scheme})`, async ({
    page,
  }) => {
    await page.emulateMedia({ colorScheme: scheme })
    for (const path of ['/forgot-password', '/reset-password?token=x']) {
      await page.goto(path)
      await expect(page.locator('h1')).toBeVisible()
      expect(await accessibilityViolations(page), path).toEqual([])
    }

    await register(page)
    for (const path of [
      '/settings/profile',
      '/settings/appearance',
      '/settings/account',
      '/settings/account/delete',
    ]) {
      await page.goto(path)
      await expect(page.getByRole('navigation', { name: 'Settings' })).toBeVisible()
      expect(await accessibilityViolations(page), path).toEqual([])
    }
  })
}
