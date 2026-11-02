import type { components } from '@/api/schema'

export type Resource = components['schemas']['Resource']
export type ResourcePage = components['schemas']['ResourcePage']
export type ResourceType = components['schemas']['ResourceType']
export type Collection = components['schemas']['Collection']
export type Tag = components['schemas']['Tag']
export type ResourceCreate = components['schemas']['ResourceCreate']
export type ResourceUpdate = components['schemas']['ResourceUpdate']
export type BulkRequest = components['schemas']['BulkRequest']

export const RESOURCE_TYPES: { value: ResourceType; label: string }[] = [
  { value: 'article', label: 'Article' },
  { value: 'video', label: 'Video' },
  { value: 'pdf', label: 'PDF' },
  { value: 'course', label: 'Course' },
  { value: 'repo', label: 'Repository' },
  { value: 'website', label: 'Website' },
  { value: 'other', label: 'Other' },
]

export const typeLabel = (t: ResourceType) => RESOURCE_TYPES.find((x) => x.value === t)?.label ?? t

export const SORTS = [
  { value: 'relevance', label: 'Relevance' },
  { value: 'newest', label: 'Newest' },
  { value: 'oldest', label: 'Oldest' },
  { value: 'title', label: 'Title A–Z' },
  { value: 'updated', label: 'Recently updated' },
] as const
export type Sort = (typeof SORTS)[number]['value']

/** Collection colour names from the design system (FR-COL-02). */
export const COLLECTION_COLORS = [
  'slate',
  'teal',
  'blue',
  'violet',
  'rose',
  'amber',
  'green',
  'orange',
] as const
