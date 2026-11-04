import type { Resource } from './types'

/** How long the app keeps checking for a new link's details (Doc 03 §5.1: poll every 2 s, up to 30 s). */
export const POLL_MS = 2000
const WINDOW_MS = 30_000

type MetadataError = NonNullable<Resource['metadata_error']>

const watching = new Map<string, number>()

/** Starts a 30-second window of polling for this resource: after saving it, or after Retry. */
export function watchMetadata(id: string): void {
  watching.set(id, Date.now() + WINDOW_MS)
}

/** True while details are still being fetched and the app should keep asking. A link saved a while ago stops being polled. */
export function isFetching(r: Resource): boolean {
  if (r.metadata_status !== 'pending') return false
  return Date.parse(r.created_at) + WINDOW_MS > Date.now() || (watching.get(r.id) ?? 0) > Date.now()
}

export const fetchPollInterval = (items: Resource[] | undefined): number | false =>
  items?.some(isFetching) ? POLL_MS : false

/** Plain-language reasons (Doc 03 §10). Retrying will not help when the address itself is refused. */
export const METADATA_ERRORS: Record<MetadataError, { message: string; retry: boolean }> = {
  blocked_url: { message: "This address can't be fetched.", retry: false },
  timeout: { message: 'The site took too long to answer.', retry: true },
  unreachable: { message: "Couldn't reach the site.", retry: true },
  http_error: { message: 'The site returned an error.', retry: true },
  not_html: { message: "This isn't a web page.", retry: false },
  too_large: { message: 'The page is too large.', retry: false },
  too_many_redirects: { message: 'The link redirects too many times.', retry: false },
}
