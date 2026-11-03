import { cn } from '@/lib/utils'

/** The ✦ mark that flags AI output. It is decorative: the text next to it always says what it is (Doc 03 §9). */
export function AiMark({ className }: { className?: string }) {
  return (
    <span aria-hidden="true" className={cn('text-ai', className)}>
      ✦
    </span>
  )
}
