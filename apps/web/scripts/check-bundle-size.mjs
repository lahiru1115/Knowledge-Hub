// Fails when the JavaScript the browser needs on first load exceeds the budget (Doc 03 §12.7, NFR-PERF-09).
// "Initial" means the entry chunk plus the chunks it imports statically (not lazy route chunks).
import { readFileSync } from 'node:fs'
import { gzipSync } from 'node:zlib'

const BUDGET_BYTES = 250 * 1024
const dist = new URL('../dist/', import.meta.url)
const html = readFileSync(new URL('index.html', dist), 'utf8')

const seen = new Set()
function collect(file) {
  if (seen.has(file)) return
  seen.add(file)
  const code = readFileSync(new URL(file, dist), 'utf8')
  // Static imports in Vite's output look like: import{...}from"./name.js" or import"./name.js"
  for (const m of code.matchAll(/(?:from|import)\s*"\.\/([^"]+\.js)"/g)) collect(`assets/${m[1]}`)
}
for (const m of html.matchAll(
  /<(?:script[^>]*src|link[^>]*rel="modulepreload"[^>]*href)="\/([^"]+\.js)"/g,
))
  collect(m[1])

let total = 0
for (const f of seen) total += gzipSync(readFileSync(new URL(f, dist))).length
const kb = (total / 1024).toFixed(1)
console.log(
  `Initial JS: ${kb} KB gzipped across ${seen.size} file(s); budget ${BUDGET_BYTES / 1024} KB`,
)
if (total > BUDGET_BYTES) {
  console.error('Over budget.')
  process.exit(1)
}
