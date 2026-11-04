export interface SseEvent {
  event: string
  data: unknown
}

/**
 * Splits server-sent events out of text received so far (Doc 02 §8.5). Returns the complete events and whatever is
 * left over (an event still arriving). Comment lines (": keep-alive") and events whose data is not JSON are skipped.
 */
export function parseSse(buffer: string): { events: SseEvent[]; rest: string } {
  const normalised = buffer.replace(/\r\n/g, '\n')
  const blocks = normalised.split('\n\n')
  const rest = blocks.pop() ?? ''
  const events: SseEvent[] = []
  for (const block of blocks) {
    let event = 'message'
    const data: string[] = []
    for (const line of block.split('\n')) {
      if (line.startsWith(':')) continue
      if (line.startsWith('event:')) event = line.slice(6).trim()
      else if (line.startsWith('data:')) data.push(line.slice(5).replace(/^ /, ''))
    }
    if (data.length === 0) continue
    try {
      events.push({ event, data: JSON.parse(data.join('\n')) as unknown })
    } catch {
      // not an event we understand
    }
  }
  return { events, rest }
}

/** Reads a response body as events, calling `onEvent` for each as it arrives. Resolves when the stream ends. */
export async function readSse(
  body: ReadableStream<Uint8Array>,
  onEvent: (e: SseEvent) => void,
): Promise<void> {
  const reader = body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''
  try {
    for (;;) {
      const { done, value } = await reader.read()
      if (done) break
      buffer += decoder.decode(value, { stream: true })
      const { events, rest } = parseSse(buffer)
      buffer = rest
      events.forEach(onEvent)
    }
    buffer += decoder.decode()
    parseSse(buffer + '\n\n').events.forEach(onEvent)
  } finally {
    reader.releaseLock()
  }
}
