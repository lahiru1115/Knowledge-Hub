import AxeBuilder from '@axe-core/playwright'
import { expect, test, type Page } from '@playwright/test'

const password = 'a long unusual passphrase'

/** WCAG 2.2 AA scan (Doc 03 §9.3). */
async function accessibilityViolations(page: Page) {
  const axe = new AxeBuilder({ page })
  const results = await axe.withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze()
  return results.violations.map((v) => `${v.id}: ${v.help}`)
}
const uniqueEmail = () => `e2e-${crypto.randomUUID()}@example.com`

async function register(page: Page, email = uniqueEmail(), name = 'Ada Lovelace') {
  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Your name').fill(name)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()
  await expect(page.getByRole('heading', { name: `Welcome, ${name}` })).toBeVisible()
  return email
}

test('register, stay signed in across a reload, sign out', async ({ page }) => {
  await register(page)

  await page.reload()
  await expect(page.getByRole('heading', { name: 'Welcome, Ada Lovelace' })).toBeVisible()

  await page.getByRole('button', { name: 'Sign out' }).click()
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await expect(page).toHaveURL(/\/login$/)

  // The session is really gone, not just hidden.
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
})

test('sign in with an existing account, and wrong credentials are refused', async ({
  page,
  context,
}) => {
  const email = await register(page)
  await context.clearCookies()

  await page.goto('/login')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill('definitely not the password')
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('alert')).toHaveText(
    "That email and password don't match. Try again.",
  )

  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: 'Welcome, Ada Lovelace' })).toBeVisible()
})

test('an expired access token is refreshed silently, without a sign-in screen', async ({
  page,
  context,
}) => {
  await register(page)
  await context.clearCookies({ name: 'kh_at' }) // what expiry looks like to the browser

  await page.reload()

  await expect(page.getByRole('heading', { name: 'Welcome, Ada Lovelace' })).toBeVisible()
  const names = (await context.cookies()).map((c) => c.name)
  expect(names).toContain('kh_at') // a new access token was issued
})

test('when the refresh token is gone too, the user is sent to sign in', async ({
  page,
  context,
}) => {
  await register(page)
  await context.clearCookies({ name: 'kh_at' })
  await context.clearCookies({ name: 'kh_rt' })

  await page.reload()

  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await expect(page).toHaveURL(/\/login\?next=/)
})

test('session cookies are not readable by scripts; only the CSRF cookie is', async ({
  page,
  context,
}) => {
  await register(page)

  const visible = await page.evaluate(() => document.cookie)
  expect(visible).toContain('kh_csrf=')
  expect(visible).not.toContain('kh_at')
  expect(visible).not.toContain('kh_rt')

  const cookies = await context.cookies()
  for (const name of ['kh_at', 'kh_rt']) {
    const c = cookies.find((x) => x.name === name)
    expect(c, name).toMatchObject({ httpOnly: true, sameSite: 'Lax' })
  }
  expect(cookies.find((x) => x.name === 'kh_rt')?.path).toBe('/api/v1/auth')
})

test('registering with an email that is taken offers sign-in', async ({ page, context }) => {
  const email = await register(page)
  await context.clearCookies()

  await page.goto('/register')
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Your name').fill('Someone Else')
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Create account' }).click()

  await expect(page.getByRole('alert')).toContainText("There's already an account with this email.")
  await expect(page.getByRole('link', { name: 'Sign in instead?' })).toBeVisible()
})

test('a signed-out visit to a deep link returns there after sign-in', async ({ page, context }) => {
  const email = await register(page)
  await context.clearCookies()

  await page.goto('/?utm=1')
  await expect(page).toHaveURL(/\/login\?next=/)
  await page.getByLabel('Email').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByRole('button', { name: 'Sign in' }).click()

  await expect(page.getByRole('heading', { name: 'Welcome, Ada Lovelace' })).toBeVisible()
  await expect(page).toHaveURL(/utm=1/)
})

for (const scheme of ['light', 'dark'] as const) {
  for (const path of ['/login', '/register']) {
    test(`${path} has no accessibility violations (${scheme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme })
      await page.goto(path)
      await expect(page.locator('h1')).toBeVisible()

      expect(await accessibilityViolations(page)).toEqual([])
    })
  }
}

test('the signed-in shell has no accessibility violations (light and dark)', async ({ page }) => {
  await register(page)
  for (const scheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: scheme })
    await page.reload()
    await expect(page.getByRole('heading', { name: 'Welcome, Ada Lovelace' })).toBeVisible()
    expect(await accessibilityViolations(page), scheme).toEqual([])
  }
})
