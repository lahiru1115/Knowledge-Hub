import { createServer as createHttpServer, type Server as HttpServer } from 'node:http'
import { createServer as createSmtpServer, type Server as SmtpServer } from 'node:net'

export interface CapturedMail {
  to: string
  subject: string
  text: string
}

/** Undoes quoted-printable encoding (soft line breaks and =XX bytes), which MailKit may use for text bodies. */
function decodeQuotedPrintable(s: string): string {
  return s
    .replace(/=\r?\n/g, '')
    .replace(/=([0-9A-F]{2})/g, (_, h: string) => String.fromCharCode(parseInt(h, 16)))
}

function parse(raw: string): CapturedMail {
  const [head = '', ...rest] = raw.split(/\r?\n\r?\n/)
  const header = (name: string) =>
    new RegExp(`^${name}:\\s*(.+)$`, 'im').exec(head)?.[1]?.trim() ?? ''
  const body = rest.join('\n\n')
  const encoded = /content-transfer-encoding:\s*quoted-printable/i.test(head)
  return {
    to: /<([^>]+)>/.exec(header('To'))?.[1] ?? header('To'),
    subject: header('Subject'),
    text: encoded ? decodeQuotedPrintable(body) : body,
  }
}

/**
 * A stand-in for Mailpit, for browser tests: accepts SMTP on 1025 and lists what arrived over HTTP on 8025
 * (`GET /messages?to=address`). It speaks only as much SMTP as the Core API's sender needs.
 */
export function startMailServer(
  smtpPort = 1025,
  httpPort = 8025,
): Promise<{ stop: () => Promise<void> }> {
  const messages: CapturedMail[] = []

  const smtp: SmtpServer = createSmtpServer((socket) => {
    socket.setEncoding('utf8')
    socket.write('220 test-mail ESMTP\r\n')
    let buffer = ''
    let data: string[] | undefined
    socket.on('data', (chunk: string) => {
      buffer += chunk
      let index
      while ((index = buffer.indexOf('\r\n')) >= 0) {
        const line = buffer.slice(0, index)
        buffer = buffer.slice(index + 2)
        if (data) {
          if (line === '.') {
            messages.push(parse(data.join('\r\n')))
            data = undefined
            socket.write('250 queued\r\n')
          } else {
            data.push(line.startsWith('..') ? line.slice(1) : line)
          }
          continue
        }
        const verb = line.split(' ')[0]?.toUpperCase()
        if (verb === 'EHLO' || verb === 'HELO') socket.write('250 test-mail\r\n')
        else if (verb === 'DATA') {
          data = []
          socket.write('354 go ahead\r\n')
        } else if (verb === 'QUIT') {
          socket.end('221 bye\r\n')
        } else socket.write('250 ok\r\n')
      }
    })
    socket.on('error', () => undefined)
  })

  const http: HttpServer = createHttpServer((req, res) => {
    const url = new URL(req.url ?? '/', 'http://localhost')
    const to = url.searchParams.get('to')?.toLowerCase()
    const found = messages.filter((m) => !to || m.to.toLowerCase() === to)
    res.setHeader('Content-Type', 'application/json')
    res.end(JSON.stringify(found))
  })

  const listen = (server: SmtpServer | HttpServer, port: number) =>
    new Promise<void>((resolve, reject) => {
      server.once('error', reject)
      server.listen(port, '127.0.0.1', () => {
        resolve()
      })
    })

  const close = (server: SmtpServer | HttpServer) =>
    new Promise<void>((resolve) => {
      server.close(() => {
        resolve()
      })
    })

  return Promise.all([listen(smtp, smtpPort), listen(http, httpPort)]).then(() => ({
    stop: async () => {
      await Promise.all([close(smtp), close(http)])
    },
  }))
}

/** Waits for a message to an address and returns the first one that arrives. */
export async function waitForMail(
  to: string,
  timeoutMs = 10_000,
  httpPort = 8025,
): Promise<CapturedMail> {
  const deadline = Date.now() + timeoutMs
  for (;;) {
    const res = await fetch(
      `http://127.0.0.1:${httpPort.toString()}/messages?to=${encodeURIComponent(to)}`,
    )
    const list = (await res.json()) as CapturedMail[]
    const last = list.at(-1)
    if (last) return last
    if (Date.now() > deadline)
      throw new Error(`No email arrived for ${to} within ${timeoutMs.toString()} ms`)
    await new Promise((r) => setTimeout(r, 200))
  }
}

/** The reset link inside an email. */
export function resetLink(mail: CapturedMail): string {
  const match = /(https?:\/\/\S+\/reset-password\?token=\S+)/.exec(mail.text)
  if (!match?.[1]) throw new Error('No reset link in the email')
  return match[1]
}
