import { useTranslation } from "react-i18next";

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
 * `DialogPrimitive.Content` traps focus while open, and its default `onEscapeKeyDown` calls
 * `onOpenChange(false)` unless prevented. Mapping `onOpenChange(false)` to Cancel covers Escape, an
 * overlay click, and the built-in "X" close button uniformly -- none of those confirm the switch,
 * so all three must cancel it.
 *
 * Issue GH-180 round 2, R3: Radix's own `onCloseAutoFocus` default (`preventDefault()` + focus
 * `context.triggerRef`) does nothing useful here -- this dialog has no `Dialog.Trigger` (it's
 * opened programmatically by `requestPersonaSwitch`, not by clicking a trigger element wired to
 * it), so there is no `triggerRef` for Radix to fall back to, and focus was silently dropping to
 * `<body>` after Cancel or Escape in both jsdom and a real Chromium run -- the PR body's claim
 * that focus is restored to the triggering element was false. `onCloseAutoFocus` below prevents
 * Radix's own (useless) default and explicitly returns focus to the persona picker's native
 * `<select>` (`#persona-picker`, see `persona-picker.tsx`) -- the control every one of these
 * dialogs is opened from -- regardless of which of the three cancel paths closed it.
 */
export default function PersonaSwitchConfirmDialog({ open, personaName, onConfirm, onCancel }: PersonaSwitchConfirmDialogProps) {
    const { t } = useTranslation();
    return (
        <Dialog open={open} onOpenChange={next => !next && onCancel()}>
            <DialogContent
                onCloseAutoFocus={event => {
                    event.preventDefault();
                    document.getElementById("persona-picker")?.focus();
                }}
            >
                <DialogHeader>
                    <DialogTitle>{t("personaSwitch.title")}</DialogTitle>
                </DialogHeader>
                <DialogDescription>{t("personaSwitch.body", { persona: personaName })}</DialogDescription>
                <div className="mt-6 flex justify-end gap-2">
                    <Button type="button" variant="outline" onClick={onCancel}>
                        {t("personaSwitch.cancel")}
                    </Button>
                    <Button type="button" onClick={onConfirm}>
                        {t("personaSwitch.confirm")}
                    </Button>
                </div>
            </DialogContent>
        </Dialog>
    );
}
