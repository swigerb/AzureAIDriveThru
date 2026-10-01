import { memo, useCallback, useEffect, useMemo, useState } from "react";
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
    /** issue 165: a pack-declared "value meal" number (e.g. a combo meal's own short id, like "#1") --
     * a string in the pack data (menu.schema.json) since some are sequential digits and the pack
     * author may want non-numeric ids later, but only ever rendered/sorted as a number here.
     * Absent entirely for a pack (or an item) with no such concept -- a pack that doesn't declare
     * this kind of item never sets it, so none of the logic below that keys off it ever fires for
     * that pack's cards/menus. */
    mealNumber?: string;
    /** issue 165: pack-declared calorie count, shown as a "123 Cal" line when present. Optional for
     * the same reason as `mealNumber` -- a pack that doesn't track calories never sets it. */
    calories?: number;
    /** issue 165: which daypart this item belongs to ("breakfast" | "lunch" | "allDay"), declared by
     * a pack that opts into the `features.dayparts` menu-mode feature (persona.schema.json).
     * Absent for any pack that doesn't declare the feature, and for an "allDay" item even within
     * a pack that does -- both cases are simply always visible (`isItemVisible` below). */
    menuPeriod?: "breakfast" | "lunch" | "allDay";
}

interface MenuCategory {
    category: string;
    /** Optional per-category icon (persona pack data, `menu.schema.json`) -- issue 119: this
     * used to be a hardcoded lookup table keyed on one pack's literal category names, which
     * silently fell back to a generic icon for every other pack's categories (and even for a
     * few categories of the pack it was hardcoded for). Packs that don't set one render the same
     * neutral fallback below. */
    icon?: string;
    /** Rick's PR 166 round-1 review, required item 9: a pack-driven override of this category's
     * display name/icon for one or more menu modes -- e.g. the original reference app renamed
     * "Fries, Sides & Drinks" to "Sides & Drinks" (with a ☕ icon) while a dayparts-feature pack's
     * session is bound to breakfast. Absent entirely for any category that never changes name by
     * mode (every category today except that one); see `resolveCategoryDisplay` below for the
     * resolution order. */
    modeDisplay?: Partial<Record<"breakfast" | "lunch" | "allDay", { displayName?: string; icon?: string }>>;
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
 * Rick's PR 166 round-1 review, required item 9: resolves the display name and icon to render for
 * a category, applying its pack-declared `modeDisplay` override for the active `menuMode` (if any)
 * over its base `category`/`icon` fields. A category with no `modeDisplay`, or no entry for the
 * active mode, or an unbound/falsy `menuMode`, renders its base `category`/`icon` exactly as
 * before -- this function is a pure superset of the pre-round-2 (issue 165) behavior, not a new default.
 */
function resolveCategoryDisplay(category: MenuCategory, menuMode?: string): { displayName: string; icon: string } {
    const override = menuMode ? category.modeDisplay?.[menuMode as "breakfast" | "lunch" | "allDay"] : undefined;
    return {
        displayName: override?.displayName ?? category.category,
        icon: override?.icon ?? category.icon ?? DEFAULT_CATEGORY_ICON
    };
}

/**
 * issue 165: whether a pack-declared menu item should be shown for the given session menu mode.
 * Mirrors the original reference app's own rule exactly: an item with no `menuPeriod` at all, or
 * one explicitly marked "allDay", is always visible; otherwise it's visible only when its
 * `menuPeriod` matches the active mode. A pack that never sets `menuPeriod` on any item (every
 * pack but the one that declares `features.dayparts` today) is entirely unaffected by this --
 * every one of its items takes the `!item.menuPeriod` branch regardless of `menuMode`.
 *
 * Rick's PR 166 round-1 review, required item 6: a falsy `menuMode` (the `""` `App.tsx` always
 * passes for a pack with no `features.dayparts`) must ALSO make every item visible regardless of
 * its own `menuPeriod` -- checked first, before the item's own tag -- mirroring both backends'
 * own `item_available_now`/`ItemAvailableNow` (`if active_mode is None: return True`, checked
 * before the item's own field is even read). Without this, a persona that didn't declare
 * `features.dayparts` but still happened to tag an item `"breakfast"`/`"lunch"` would hide that
 * item here while both backends' add-time gate and search filter still allowed it -- the
 * frontend-specific half of the "search vs order semantics differ" split the review flagged.
 */
function isItemVisible(item: MenuItem, menuMode?: string): boolean {
    if (!menuMode) {
        return true;
    }
    return !item.menuPeriod || item.menuPeriod === "allDay" || item.menuPeriod === menuMode;
}

/**
 * Rick's PR 166 round-1 review, required item 1: whether a size's own label (e.g. `"Standard: "`)
 * should render next to its price. Before issue 165 this repo's own `dev` branch hid the label
 * for exactly one lowercase value, `size === "standard"`, regardless of anything else about the
 * item -- every other pre-existing single-size item written that way keeps rendering that way
 * here, unaffected by the menu-fidelity work below. Issue 165 then widened the rule to also cover
 * the original reference app's own (capitalised) `"Standard"`, but tied that widening to
 * `calories` being present, which incidentally re-exposed the label on the 10 lowercase
 * `"standard"` single-size items belonging to the other pre-existing persona pack that never sets
 * `calories` on this item -- a regression, since dev already hid those. The fix: a pack that
 * supplies `calories` on this item (today, only the newly-added breakfast/lunch pack) follows
 * the original app's own rule verbatim (hide for either casing of "standard"); a pack that
 * doesn't keeps dev's exact rule untouched (hide only the lowercase form). A genuinely
 * multi-size item (Small/Medium/Large, etc.) is unaffected either way since none of its size keys
 * ever equal "standard"/"Standard".
 */
function shouldShowSizeLabel(size: string, calories: number | undefined): boolean {
    if (typeof calories === "number") {
        return size !== "standard" && size !== "Standard";
    }
    return size !== "standard";
}

/**
 * issue 165: applies the session menu mode filter and synthesizes the "value meals" category,
 * mirroring the original reference app's own `menu-panel.tsx` algorithm. Every `mealNumber` item
 * across ALL categories that's visible for `menuMode` is pulled out, sorted by meal number, and
 * rendered as one virtual category shown first; every remaining (non-`mealNumber`) item stays in
 * its own data category, filtered by `menuMode`, and a category left with zero items after that
 * filter is dropped entirely (never rendered as an empty shell).
 *
 * A pack with no `mealNumber`/`menuPeriod` items at all (every pack but the one that declares
 * `features.dayparts` today) round-trips through this unchanged: `isItemVisible` is always true
 * for such items (see above), nothing has a `mealNumber` to pull out, so `valueMealItems` is
 * always empty and every category's item list and order survive untouched.
 *
 * Rick's PR 166 round-1 review, required item 9: the synthesized category's display name is now a
 * caller-supplied `valueMealCategoryName` (the active persona's own `t("menu.valueMealsCategory")`
 * string, see the component below) instead of a shared component hardcoding one persona's own copy
 * ("Extra Value Meals") for every brand.
 */
function buildDisplayCategories(categories: MenuCategory[], menuMode: string | undefined, valueMealCategoryName: string): MenuCategory[] {
    const valueMealItems = categories
        .flatMap(category => category.items)
        .filter(item => item.mealNumber && isItemVisible(item, menuMode))
        .sort((a, b) => Number(a.mealNumber) - Number(b.mealNumber));

    const remainingCategories = categories
        .map(category => ({
            ...category,
            items: category.items.filter(item => !item.mealNumber && isItemVisible(item, menuMode))
        }))
        .filter(category => category.items.length > 0);

    if (valueMealItems.length === 0) {
        return remainingCategories;
    }

    return [{ category: valueMealCategoryName, items: valueMealItems }, ...remainingCategories];
}

/**
 * Issue #80 F4: the menu now comes entirely from the active persona's pack (`menuUrl`, a
 * server-computed `/personas/{id}/menu.json?v=<hash>` URL -- design doc §5.2), replacing the old
 * bundled `src/data/menuItems.json` copy. There's no hardcoded fallback menu: `menuUrl` always
 * points at a real, schema-validated file the backend already refused to start without (see
 * `persona_loader.py`'s `menu_path`/`menu.schema.json` validation), so a failed fetch is a genuine
 * runtime/network problem worth surfacing, not a data-shape gap to paper over.
 */
interface MenuPanelProps {
    /** issue 165: the active session's menu mode ("breakfast" | "lunch"), threaded down from
     * `App.tsx`'s own state (see `lib/menuMode.ts`). Optional/falsy for a pack that doesn't
     * declare `features.dayparts` -- `App.tsx` always passes `""` for one, which `isItemVisible`
     * now treats as "ignore `menuPeriod` entirely" (Rick's PR 166 round-1 review, required item
     * 6), matching both backends' own mode-unbound (`None`/`null`) treatment. */
    menuMode?: string;
}

export default memo(function MenuPanel({ menuMode }: MenuPanelProps) {
    const { current } = usePersonaContext();
    const { t } = useTranslation();
    const [menu, setMenu] = useState<MenuCategory[] | null>(null);
    const [error, setError] = useState(false);
    const [expanded, setExpanded] = useState<Set<string>>(() => new Set<string>());

    // Rick's PR 166 round-1 review, required item 9: pack-driven, not hardcoded -- defaults to the
    // shared, brand-neutral `translation.json` copy ("Value Meals") unless the active persona's
    // own `ui.strings` overrides it (one pack does, to a more brand-specific label).
    const valueMealCategoryName = t("menu.valueMealsCategory");
    const displayCategories = useMemo(
        () => (menu ? buildDisplayCategories(menu, menuMode, valueMealCategoryName) : null),
        [menu, menuMode, valueMealCategoryName]
    );

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
                    // All categories expanded by default, same as the previous static menu -- plus
                    // the synthesized value-meals category name from issue 165 (harmless to
                    // include even for a pack that never renders it), so it isn't collapsed the
                    // first time it appears after a later menu-mode toggle.
                    setExpanded(new Set([...data.menuItems.map(c => c.category), valueMealCategoryName]));
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

    if (!displayCategories) {
        return (
            <p aria-live="polite" className="p-4 text-sm text-muted-foreground">
                {t("menu.loading")}
            </p>
        );
    }

    return (
        <div className="space-y-4">
            {displayCategories.map(category => {
                const isOpen = expanded.has(category.category);
                // Rick's PR 166 round-1 review, required item 9: the data-category name stays the
                // stable identity for expand/collapse tracking and the React `key` (it never
                // changes when `menuMode` flips), while only the rendered name/icon below follow
                // the active mode's pack-declared override, if any.
                const { displayName, icon } = resolveCategoryDisplay(category, menuMode);
                return (
                    <div
                        key={category.category}
                        className="rounded-3xl border border-primary/10 bg-white/80 shadow-[0_15px_35px_var(--brand-secondary-veil-08)] dark:border-white/10 dark:bg-brand-surface-dark/95 dark:shadow-[0_25px_55px_rgba(0,0,0,0.65)]"
                    >
                        <button
                            type="button"
                            onClick={() => toggle(category.category)}
                            className="flex w-full cursor-pointer items-center justify-between gap-3 p-4"
                            aria-expanded={isOpen}
                        >
                            <div className="flex items-center gap-2 sm:gap-3">
                                <span className="text-2xl" aria-hidden>
                                    {icon}
                                </span>
                                <h3 className="break-keep text-left font-semibold uppercase tracking-wide text-primary dark:text-primary">
                                    {displayName}
                                </h3>
                            </div>
                            <div className="flex items-center gap-2">
                                <span className="whitespace-nowrap rounded-full bg-brand-secondary/10 px-3 py-1 text-xs font-bold text-brand-secondary dark:bg-brand-surface-dark-alt dark:text-brand-secondary-tint">
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
                                                        {/* issue 165 / Rick's PR 166 round-1 review, required item 2: the meal circle and the
                                                            item name share their own row; the description and calorie line are siblings
                                                            BELOW that row (not nested inside it), matching the original reference card's
                                                            structure exactly -- not a column beside the circle. */}
                                                        <div className="flex items-center gap-2">
                                                            {/* issue 165: the numbered "value meal" circle, shown only for an item the pack
                                                                gave a `mealNumber` -- absent entirely for a pack that never sets it. Bound
                                                                to the shared `primary` token rather than the generic `destructive` one
                                                                (Rick's PR 166 round-1 review, required item 2): the original card's circle
                                                                is always this new pack's own brand red in both themes, the color `primary`
                                                                resolves to once the matching palette swap from the concurrent UX-parity
                                                                PR lands -- so this stays generic shared-component code with no brand hex
                                                                here, at the cost of a transient color mismatch against today's still-gold
                                                                `primary` until that PR merges (the two PRs' own coordination note in
                                                                Rick's review: whoever merges second rebases). */}
                                                            {item.mealNumber && (
                                                                <span
                                                                    aria-hidden
                                                                    className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-primary text-xs font-bold text-primary-foreground"
                                                                >
                                                                    {item.mealNumber}
                                                                </span>
                                                            )}
                                                            <span className="font-semibold text-foreground dark:text-white">{item.name}</span>
                                                        </div>
                                                        <p className="text-sm text-muted-foreground">{item.description}</p>
                                                        {/* issue 165: shown only for an item the pack gave a positive `calories` count --
                                                            absent entirely for a pack that doesn't track calories, and (matching the
                                                            original) for a 0-calorie item rather than rendering a literal "0 Cal". */}
                                                        {typeof item.calories === "number" && item.calories > 0 && (
                                                            <p className="mt-0.5 text-xs text-muted-foreground/70">{item.calories} Cal</p>
                                                        )}
                                                    </div>
                                                    <div className="text-right">
                                                        {item.sizes.map(({ size, price }) => (
                                                            <div key={size} className="font-mono text-sm text-foreground/80 dark:text-white/80">
                                                                {shouldShowSizeLabel(size, item.calories) ? (
                                                                    <span className="capitalize">{`${size}: `}</span>
                                                                ) : null}
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
