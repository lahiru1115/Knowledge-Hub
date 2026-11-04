import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'

// Vitest runs from apps/web.
const read = (f: string) => readFileSync(resolve(process.cwd(), f), 'utf8')

interface Header {
  key: string
  value: string
}
interface VercelConfig {
  rewrites: { source: string; destination: string }[]
  headers: { headers: Header[] }[]
}
const config = JSON.parse(read('vercel.json')) as VercelConfig
const header = (name: string) => config.headers[0]?.headers.find((h) => h.key === name)?.value ?? ''

describe('vercel.json', () => {
  it('allows exactly the inline theme script in index.html (hash must be updated if the script changes)', () => {
    const script = /<script>([\s\S]*?)<\/script>/.exec(read('index.html'))?.[1]
    expect(script).toBeDefined()
    const hash = createHash('sha256')
      .update(script ?? '')
      .digest('base64')

    expect(header('Content-Security-Policy')).toContain(`'sha256-${hash}'`)
  })

  it('has a CSP without unsafe-inline scripts and with frame-ancestors none (NFR-SEC-11)', () => {
    const csp = header('Content-Security-Policy')
    expect(csp).toContain("frame-ancestors 'none'")
    expect(csp).not.toMatch(/script-src[^;]*'unsafe-inline'/)
    expect(csp).not.toMatch(/script-src[^;]*'unsafe-eval'/)
  })

  it('sets the other security headers from Doc 02 §9.5', () => {
    expect(header('Strict-Transport-Security')).toBe('max-age=31536000; includeSubDomains')
    expect(header('X-Content-Type-Options')).toBe('nosniff')
    expect(header('Referrer-Policy')).toBe('strict-origin-when-cross-origin')
    expect(header('Permissions-Policy')).not.toBe('')
  })

  it('rewrites /api/* to the Core API and everything else to the SPA, so cookies stay first-party', () => {
    expect(config.rewrites[0]?.source).toBe('/api/:path*')
    expect(config.rewrites[0]?.destination).toMatch(/\/api\/:path\*$/)
    expect(config.rewrites[1]?.destination).toBe('/index.html')
  })
})
