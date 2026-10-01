import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";

export interface PersonaSwitchConfirmDialogProps {
    /** True while a switch is awaiting the guest's confirmation. */
    open: boolean;
    /** The persona being switched TO -- `displayName` is the only persona-specific detail the
     * copy below surfaces (issue GH-180: no brand words in this shared-code dialog). */
    personaName: string;
    onConfirm: () => void;
    onCancel: () => void;
}

/**
 * Issue GH-180: the picker used to lock itself outright ("Locked for this order -- start a new
 * order to switch") whenever a session was active or the ticket had items, with no path forward
 * except abandoning the picker entirely. The picker is now always enabled -- this dialog is the
 * new gate: switching mid-order/mid-conversation requires an explicit confirmation instead of a
 * silent block or a silent reset.
 *
 * Accessibility (focus trap, Escape-to-cancel) comes from Radix's `Dialog` primitive for free:
 * `DialogPrimitive.Content` traps focus while open and restores it to the triggering element on
 * close, and its default `onEscapeKeyDown` calls `onOpenChange(false)` unless prevented. Mapping
 * `onOpenChange(false)` to Cancel covers Escape, an overlay click, and the built-in "X" close
 * button uniformly -- none of those confirm the switch, so all three must cancel it.
 */
export default function PersonaSwitchConfirmDialog({ open, personaName, onConfirm, onCancel }: PersonaSwitchConfirmDialogProps) {
    return (
        <Dialog open={open} onOpenChange={next => !next && onCancel()}>
            <DialogContent>
                <DialogHeader>
                    <DialogTitle>Switch persona?</DialogTitle>
                </DialogHeader>
                <DialogDescription>Switching to {personaName} will start a new order. Your current order will be cleared.</DialogDescription>
                <div className="mt-6 flex justify-end gap-2">
                    <Button type="button" variant="outline" onClick={onCancel}>
                        Cancel
                    </Button>
                    <Button type="button" onClick={onConfirm}>
                        Switch
                    </Button>
                </div>
            </DialogContent>
        </Dialog>
    );
}
