import { memo, useCallback, useEffect, useState } from "react";
import { AnimatePresence, motion } from "framer-motion";
import { ChevronDown } from "lucide-react";
import { useTranslation } from "react-i18next";
import { usePersonaContext } from "@/context/persona-context";

interface Size {
    size: string;
    price: number;
}

interface MenuItem {
    name: string;
    sizes: Size[];
    description: string;
}

interface MenuCategory {
    category: string;
    /** Optional per-category icon (persona pack data, `menu.schema.json`) -- issue 119: this
     * used to be a hardcoded lookup table keyed on one pack's literal category names, which
     * silently fell back to a generic icon for every other pack's categories (and even for a
     * few categories of the pack it was hardcoded for). Packs that don't set one fall through to
     * `current.categoryIcons` (issue 164 E1) and then the shared neutral fallback below. */
    icon?: string;
    items: MenuItem[];
}

interface MenuDocument {
    menuItems: MenuCategory[];
}

/** Shared, brand-neutral fallback for any category a pack didn't give its own `icon` -- matches
 * what every uncovered category already rendered before this became data-driven, so no pack's
 * menu panel changes appearance by this alone. */
const DEFAULT_CATEGORY_ICON = "🍹";

/**
 * Issue #80 F4: the menu now comes entirely from the active persona's pack (`menuUrl`, a
 * server-computed `/personas/{id}/menu.json?v=<hash>` URL -- design doc §5.2), replacing the old
 * bundled `src/data/menuItems.json` copy. There's no hardcoded fallback menu: `menuUrl` always
 * points at a real, schema-validated file the backend already refused to start without (see
 * `persona_loader.py`'s `menu_path`/`menu.schema.json` validation), so a failed fetch is a genuine
 * runtime/network problem worth surfacing, not a data-shape gap to paper over.
 */
export default memo(function MenuPanel() {
    const { current } = usePersonaContext();
    const { t } = useTranslation();
    const [menu, setMenu] = useState<MenuCategory[] | null>(null);
    const [error, setError] = useState(false);
    const [expanded, setExpanded] = useState<Set<string>>(() => new Set<string>());

    useEffect(() => {
        let cancelled = false;
        setMenu(null);
        setError(false);

        (async () => {
            try {
                const response = await fetch(current.menuUrl);
                if (!response.ok) {
                    throw new Error(`menu.json request failed: ${response.status}`);
                }
                const data = (await response.json()) as MenuDocument;
                if (!cancelled) {
                    setMenu(data.menuItems);
                    // All categories expanded by default, same as the previous static menu.
                    setExpanded(new Set(data.menuItems.map(c => c.category)));
                }
            } catch {
                if (!cancelled) {
                    setError(true);
                }
            }
        })();

        return () => {
            cancelled = true;
        };
    }, [current.menuUrl]);

    const toggle = useCallback((category: string) => {
        setExpanded(prev => {
            const next = new Set(prev);
            if (next.has(category)) {
                next.delete(category);
            } else {
                next.add(category);
            }
            return next;
        });
    }, []);

    if (error) {
        return (
            <p role="alert" className="p-4 text-sm text-destructive">
                {t("menu.loadError")}
            </p>
        );
    }

    if (!menu) {
        return (
            <p aria-live="polite" className="p-4 text-sm text-muted-foreground">
                {t("menu.loading")}
            </p>
        );
    }

    return (
        <div className="space-y-4">
            {menu.map(category => {
                const isOpen = expanded.has(category.category);
                return (
                    <div
                        key={category.category}
                        className="rounded-3xl border border-primary/10 bg-white/80 shadow-[0_15px_35px_var(--brand-secondary-veil-08)] dark:border-white/10 dark:bg-brand-surface-dark/95 dark:shadow-[0_25px_55px_rgba(0,0,0,0.65)]"
                    >
                        <button
                            type="button"
                            onClick={() => toggle(category.category)}
                            className="flex w-full cursor-pointer items-center justify-between gap-2 p-4"
                            aria-expanded={isOpen}
                        >
                            <div className="flex items-center gap-2">
                                <span className="text-2xl" aria-hidden>
                                    {category.icon ?? current.categoryIcons?.[category.category] ?? DEFAULT_CATEGORY_ICON}
                                </span>
                                {/* Matches the original apps' own class exactly (`break-keep`, not
                                    `truncate`): a long category name wraps onto a second line rather than
                                    getting cut off with an ellipsis. The surrounding gaps/padding are
                                    trimmed slightly (E3) so a short two-word name like "Signature Lattes"
                                    still fits the chip+chevron on one row, as it does in that pack's
                                    original (non-collapsible) layout, while a longer name is still free
                                    to wrap exactly as it does in the original apps. */}
                                <h3 className="break-keep text-left font-semibold uppercase tracking-wide text-primary dark:text-primary">
                                    {category.category}
                                </h3>
                            </div>
                            <div className="flex items-center gap-1">
                                <span className="whitespace-nowrap rounded-full bg-brand-secondary/10 px-2 py-1 text-xs font-bold text-brand-secondary dark:bg-brand-surface-dark-alt dark:text-brand-secondary-tint">
                                    {/* Non-blocking item from Rick's PR-110 review: correct singular/plural
                                        ("1 item", not "1 items"). */}
                                    {category.items.length} {category.items.length === 1 ? "item" : "items"}
                                </span>
                                <motion.span
                                    animate={{ rotate: isOpen ? 180 : 0 }}
                                    transition={{ duration: 0.2 }}
                                    className="text-primary/60 dark:text-white/50"
                                >
                                    <ChevronDown size={18} />
                                </motion.span>
                            </div>
                        </button>

                        <AnimatePresence initial={false}>
                            {isOpen && (
                                <motion.div
                                    initial={{ height: 0, opacity: 0 }}
                                    animate={{ height: "auto", opacity: 1 }}
                                    exit={{ height: 0, opacity: 0 }}
                                    transition={{ duration: 0.25, ease: "easeInOut" }}
                                    className="overflow-hidden"
                                >
                                    <div className="space-y-4 px-4 pb-4">
                                        {category.items.map(item => (
                                            <div
                                                key={item.name}
                                                className="rounded-2xl border border-dashed border-primary/20 bg-white/70 p-3 transition-colors dark:border-white/10 dark:bg-white/5"
                                            >
                                                <div className="flex flex-wrap items-baseline justify-between gap-2">
                                                    <div className="pr-1">
                                                        <span className="font-semibold text-foreground dark:text-white">{item.name}</span>
                                                        <p className="text-sm text-muted-foreground">{item.description}</p>
                                                    </div>
                                                    <div className="text-right">
                                                        {item.sizes.map(({ size, price }) => (
                                                            <div key={size} className="font-mono text-sm text-foreground/80 dark:text-white/80">
                                                                {size !== "standard" ? <span className="capitalize">{`${size}: `}</span> : null}
                                                                <span>${price.toFixed(2)}</span>
                                                            </div>
                                                        ))}
                                                    </div>
                                                </div>
                                            </div>
                                        ))}
                                    </div>
                                </motion.div>
                            )}
                        </AnimatePresence>
                    </div>
                );
            })}
        </div>
    );
});
