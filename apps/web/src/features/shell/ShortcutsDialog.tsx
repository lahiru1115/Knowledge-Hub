import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Kbd } from '@/components/ui/kbd'

const GROUPS: { title: string; items: [string[], string][] }[] = [
  {
    title: 'Everywhere',
    items: [
      [['N'], 'Add a resource'],
      [['/'], 'Focus search'],
      [['['], 'Collapse or expand the sidebar'],
      [['?'], 'Show this list'],
      [['Esc'], 'Close a dialog or the drawer'],
    ],
  },
  {
    title: 'In the library',
    items: [
      [['J'], 'Next resource'],
      [['K'], 'Previous resource'],
      [['Enter'], 'Open the selected resource'],
      [['X'], 'Select or deselect'],
      [['F'], 'Favourite'],
      [['E'], 'Archive'],
      [['Delete'], 'Delete (you can undo)'],
    ],
  },
  { title: 'In the drawer', items: [[['J', 'K'], 'Next or previous resource']] },
]

/** The shortcut list (`?`). Single-key shortcuts can be switched off in Settings → Appearance (WCAG 2.1.4). */
export function ShortcutsDialog({ onClose }: { onClose: () => void }) {
  return (
    <Dialog
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Keyboard shortcuts</DialogTitle>
          <DialogDescription>
            Single-key shortcuts work when you are not typing. You can turn them off in Settings →
            Appearance.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-4">
          {GROUPS.map((g) => (
            <section key={g.title} aria-label={g.title}>
              <h3 className="mb-1 text-sm font-semibold">{g.title}</h3>
              <dl className="space-y-1 text-sm">
                {g.items.map(([keys, what]) => (
                  <div key={what} className="flex items-center justify-between gap-4">
                    <dt className="text-muted-foreground">{what}</dt>
                    <dd className="flex gap-1">
                      {keys.map((k) => (
                        <Kbd key={k}>{k}</Kbd>
                      ))}
                    </dd>
                  </div>
                ))}
              </dl>
            </section>
          ))}
        </div>
      </DialogContent>
    </Dialog>
  )
}
