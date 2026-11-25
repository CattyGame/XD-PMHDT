
import { useEffect, useRef } from 'react'
import type { ReactNode } from 'react'
import './AppModal.css'
type AppModalProps = {
  isOpen: boolean
  title: string
  children: ReactNode
  onClose: () => void
  role?: 'alertdialog'
  describedBy?: string
  className?: string
  actions?: ReactNode
}

export default function AppModal({
  isOpen,
  title,
  children,
  onClose,
  role,
  describedBy,
  className,
  actions,
}: AppModalProps) {
  const dialogRef = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = dialogRef.current

    if (!dialog) return

    if (isOpen && !dialog.open) {
      dialog.showModal()
    }

    if (!isOpen && dialog.open) {
      dialog.close()
    }
  }, [isOpen])

  return (
    <dialog
      ref={dialogRef}
      onClose={isOpen ? onClose : undefined}
      role={role}
      aria-label={title}
      aria-describedby={describedBy}
      aria-modal={isOpen || undefined}
      className={className ?? 'aura-modal'}
    >
      <h2>{title}</h2>

      <div className="aura-modal-content">
        {children}
      </div>

      <div className="aura-modal-actions">
        <button type="button" onClick={() => dialogRef.current?.close()}>Đóng</button>
        {actions}
      </div>
    </dialog>
  )
}
