using Backend.Personas;

namespace Backend.Ordering;

/// <summary>Per-call bookkeeping <see cref="OrderState.HandleOrderUpdate"/> returns alongside the
/// mutated order (mirrors order_state.py's <c>result_info</c> dict) -- read by
/// <c>Tools.OrderToolExecutor</c> to build the right delta text (combo-absorption/conversion
/// wording) without re-deriving what happened from the order lines themselves.</summary>
public sealed class OrderUpdateResult
{
    public bool AbsorbedIntoCombo { get; set; }
    public string? ComboConvertedFrom { get; set; }
    public string? ModsCarried { get; set; }
    public string? ModifiedFromSize { get; set; }
    public string? ModifiedToSize { get; set; }
    public List<string> Autofilled { get; } = [];

    // #179: set whenever a combo's side/drink slot ("sides"/"drinks") was (re)sized in place --
    // via an explicit `modify` targeting an absorbed component, an `add` of the same item at a
    // different size while the slot was already full, or a refill of a slot just vacated by a
    // `remove` of that same item. Mirrors order_state.py's result_info["resized_combo_component"].
    public string? ResizedComboComponent { get; set; }
    public string? ComboComponentResizedFromSize { get; set; }
    public string? ComboComponentResizedToSize { get; set; }
    public string? ComboDisplay { get; set; }

    // #179: set by HandleRemove when itemName was vacating a combo slot via absorption rather
    // than removing a raw order line.
    public string? VacatedComboComponent { get; set; }
    public string? VacatedDisplay { get; set; }
}

/// <summary>Result of <see cref="OrderState.GetComboRequirements"/> -- mirrors order_state.py's
/// <c>get_combo_requirements</c> return dict.</summary>
public sealed record ComboRequirements(bool IsComplete, IReadOnlyList<string> MissingItems, string PromptHint);

/// <summary>
/// Port of app/backend/order_state.py's <c>OrderState</c> class (docs/dotnet_mapping.md, issues
/// #14/#40/#41/#46/#47/#74/#77/#97/#104/#113). Owns ONE session's order lines plus its
/// combo-absorption bookkeeping.
///
/// <para><b>Confinement design (agreed on #14, differs intentionally from Python):</b> Python's
/// <c>OrderState</c> is a process-wide singleton dict keyed by <c>session_id</c>, guarded by an
/// explicit <c>_check_owner</c> OS-thread-identity assertion (issue #97) because every session's
/// event-loop task can run on the same underlying OS thread. In C#, each session already has its
/// own dedicated actor (<c>SessionActor</c>, a single-reader <c>Channel&lt;SessionEvent&gt;</c>
/// loop) -- so instead of a second, independent thread-identity check bolted onto a shared
/// dictionary, THIS class is simply a plain, mutable, per-session instance with no session-id
/// parameter anywhere and no static/singleton state at all. It is constructed once per session
/// (by whatever wires up that session's <c>SessionActor</c>) and is exclusively owned/mutated by
/// that actor's single-reader loop -- the same "confined to one owner, never touched concurrently"
/// guarantee Python's <c>_check_owner</c> enforces at runtime, but enforced structurally instead
/// (there is no second actor that could ever hold a reference to call it from). This is
/// deliberately NOT a thread-safe type; do not share one instance across actors.</para>
/// </summary>
public sealed class OrderState
{
    private readonly List<OrderItem> _items = [];
    private readonly MenuCatalog _menu;
    private readonly TimeZoneInfo _timeZone;
    private readonly (int StartHour, int EndHour)? _happyHourWindow;
    private readonly decimal _happyHourDiscount;
    private readonly decimal _taxRate;
    private readonly bool _happyHourAnnounce;
    private readonly string _happyHourBanner;

    private int _absorbedSides;
    private int _absorbedDrinks;
    private string _absorbedSideDisplay = "";
    private string _absorbedDrinkDisplay = "";

    // #179: per-slot "what's CURRENTLY filling it" / "what established its free-pricing anchor"
    // bookkeeping -- see FillBundleComponent's own doc comment for the full stateful "free
    // reference" pricing design this ports from order_state.py's `absorbed_{side,drink}_*`
    // session fields. _absorbedSideItem/_absorbedDrinkItem is "" when the slot is vacant;
    // _absorbedSideFreeItem/_absorbedDrinkFreeItem deliberately SURVIVES a vacate of the same
    // item so a later refill reprices as a resize instead of granting a second free upsize.
    private string _absorbedSideItem = "";
    private string _absorbedSideSize = "";
    private string _absorbedSideFreeItem = "";
    private string _absorbedSideFreeSize = "";
    private decimal _absorbedSideUpcharge;
    private string _absorbedDrinkItem = "";
    private string _absorbedDrinkSize = "";
    private string _absorbedDrinkFreeItem = "";
    private string _absorbedDrinkFreeSize = "";
    private decimal _absorbedDrinkUpcharge;

    public OrderSummary Summary { get; private set; } = OrderSummary.Empty();

    /// <param name="menu">This session's own bound persona's <see cref="MenuCatalog"/> (#74).</param>
    /// <param name="timeZone">This session's own bound persona's store timezone
    /// (<c>persona.json</c>'s <c>store.timezone</c>).</param>
    /// <param name="taxRate">This session's own bound persona's <c>pricing.taxRate</c>.</param>
    /// <param name="happyHourWindow">This session's own bound persona's
    /// <c>pricing.happyHour.(startHour,endHour)</c>, or <c>null</c> for a pack with
    /// <c>happyHour: null</c> (decision 5) -- happy hour can never be active for such a
    /// persona.</param>
    /// <param name="happyHourDiscount">This session's own bound persona's
    /// <c>pricing.happyHour.discount</c> (ignored when <paramref name="happyHourWindow"/> is
    /// <c>null</c>).</param>
    /// <param name="happyHourAnnounce">This session's own bound persona's
    /// <c>pricing.happyHour.announce</c> (#113) -- whether the banner should ever be appended,
    /// independent of whether happy hour is currently active.</param>
    /// <param name="happyHourBanner">This session's own bound persona's own
    /// <c>pricing.happyHour.banner</c> text (#113) -- never hardcoded here.</param>
    public OrderState(
        MenuCatalog menu,
        TimeZoneInfo timeZone,
        decimal taxRate,
        (int StartHour, int EndHour)? happyHourWindow,
        decimal happyHourDiscount,
        bool happyHourAnnounce,
        string happyHourBanner)
    {
        _menu = menu;
        _timeZone = timeZone;
        _taxRate = taxRate;
        _happyHourWindow = happyHourWindow;
        _happyHourDiscount = happyHourDiscount;
        _happyHourAnnounce = happyHourAnnounce;
        _happyHourBanner = happyHourBanner;
        UpdateSummary();
    }

    /// <summary>Raw order lines -- avoids building a defensive copy for hot validation checks
    /// (mirrors order_state.py's <c>get_order_items</c>). Callers must not mutate this list
    /// directly; go through <see cref="HandleOrderUpdate"/>.</summary>
    public IReadOnlyList<OrderItem> Items => _items;

    /// <summary>Whether *now* (this session's own bound persona's store-local time) falls in this
    /// session's own happy-hour window. Mirrors order_state.py's module-level <c>is_happy_hour</c>
    /// applied to this session, and its own <c>_is_happy_hour_for</c>/<c>is_happy_hour_for_session</c>
    /// wrappers -- there is exactly one implementation of the window/timezone check here, just as
    /// in Python.</summary>
    public bool IsHappyHour()
    {
        if (_happyHourWindow is not { } window)
        {
            return false;
        }
        var now = ConformanceHooks.Now(_timeZone);
        return window.StartHour <= now.Hour && now.Hour < window.EndHour;
    }

    /// <summary>#113: the banner text (<c>" " + banner</c>, matching Python's leading-space
    /// positioning) tools/get_order should append to their result for this session -- "" unless
    /// this persona's own <c>announce</c> flag is set AND happy hour is currently active. Mirrors
    /// order_state.py's <c>get_happy_hour_banner_for_session</c>.</summary>
    public string HappyHourBanner => _happyHourAnnounce && IsHappyHour() ? $" {_happyHourBanner}" : "";

    /// <summary>Ports order_state.py's <c>handle_order_update</c> verbatim (algorithm captured in
    /// full in the class/method comments below) -- the combo-name-marker conversion, post-bundle
    /// absorption, regular add/merge, bundle-pivot absorption, bundle autoFill, modify-in-place,
    /// and remove/decrement branches, always re-pricing from <paramref name="menu"/>
    /// (#104) and never trusting <paramref name="callerPrice"/> except as a fallback when the menu
    /// has no price on file at all, and always ending by recomputing <see cref="Summary"/>.</summary>
    public OrderUpdateResult HandleOrderUpdate(
        string action, string itemName, string size, int quantity, decimal? callerPrice)
    {
        var result = new OrderUpdateResult();

        // #40: canonicalize the size to a single alias-resolved key BEFORE any matching/merging so
        // different spellings of the same physical size collapse onto one order line.
        size = _menu.CanonicalSizeKey(size);
        var resolved = _menu.NormalizeSize(size);
        var formattedSize = resolved.Length > 0 ? $"{resolved} " : "";
        var display = $"{formattedSize}{itemName}".Trim();

        decimal price = callerPrice ?? 0m;
        if (action is "add" or "modify")
        {
            // #104: the unit price charged is ALWAYS this persona's own menu price for
            // (item_name, size) -- the tool call's own price is only ever used as a debug-log
            // comparison, or (when the menu genuinely has no price on file) as a last-resort
            // fallback so an on-menu item somehow reached here without a price. See
            // docs/persona-architecture.md section 6.
            var menuPrice = _menu.PriceFor(itemName, size);
            price = menuPrice ?? price;
        }

        switch (action)
        {
            case "add":
                HandleAdd(itemName, size, quantity, formattedSize, display, price, result);
                break;
            case "modify":
                HandleModify(itemName, size, price, display, result);
                break;
            case "remove":
                HandleRemove(itemName, size, quantity, result);
                break;
        }

        UpdateSummary();
        return result;
    }

    private void HandleAdd(
        string itemName, string size, int quantity, string formattedSize, string display,
        decimal price, OrderUpdateResult result)
    {
        // #77 (shared bundle engine): whether this item's own name carries one of this persona's
        // bundle.nameMarkers -- name-based, used ONLY for the combo-conversion (auto-removing a
        // matching standalone entree) below.
        var nameMarkers = _menu.BundleNameMarkers;
        var lowerName = itemName.ToLowerInvariant();
        var isCombo = _menu.BundleConvertStandalone && nameMarkers.Any(marker => lowerName.Contains(marker));

        // The item's OWN bundle slots (data-driven, not name-based) -- everything about how many
        // slots a bundle item actually absorbs goes through this, never "is_combo" above.
        var ownBundleSlots = _menu.BundleSlots(itemName);
        var isBundle = ownBundleSlots.Count > 0;

        // ── Combo conversion: auto-remove matching standalone entree ──
        if (isCombo)
        {
            var comboBase = MenuKeyValidator.MenuKey(itemName);
            foreach (var marker in nameMarkers)
            {
                comboBase = comboBase.Replace($" {marker}", "");
            }
            comboBase = comboBase.Trim();

            for (var i = 0; i < _items.Count; i++)
            {
                var existing = _items[i];
                if (_menu.BundleSlots(existing.Item).Count > 0)
                {
                    continue; // skip other bundle items, not just other "combo"-named ones
                }
                if (MenuKeyValidator.MenuKey(existing.Item) != comboBase)
                {
                    continue;
                }
                if (existing.Item.Contains('('))
                {
                    var mods = existing.Item[existing.Item.IndexOf('(')..];
                    itemName = $"{itemName} {mods}";
                    display = $"{formattedSize}{itemName}".Trim();
                    result.ModsCarried = mods;
                }
                result.ComboConvertedFrom = existing.Item;
                if (existing.Quantity > 1)
                {
                    existing.Quantity--;
                }
                else
                {
                    _items.RemoveAt(i);
                }
                break;
            }
        }

        // ── Post-bundle absorption: side/drink fills an incomplete bundle's slot ──
        if (!isBundle)
        {
            var component = _menu.InferComboComponent(itemName);
            if (component is "sides" or "drinks")
            {
                var bundleCapacity = _items.Where(it => _menu.BundleSlots(it.Item).Contains(component)).Sum(it => it.Quantity);
                if (bundleCapacity > 0)
                {
                    int filled;
                    if (component == "sides")
                    {
                        filled = _items.Where(it => _menu.InferComboComponent(it.Item) == "sides").Sum(it => it.Quantity);
                        filled += _absorbedSides;
                    }
                    else
                    {
                        filled = _items.Where(it => _menu.InferComboComponent(it.Item) == "drinks").Sum(it => it.Quantity);
                        filled += _absorbedDrinks;
                    }

                    var slotsAvailable = bundleCapacity - filled;
                    var currentItem = component == "sides" ? _absorbedSideItem : _absorbedDrinkItem;
                    var currentSize = component == "sides" ? _absorbedSideSize : _absorbedDrinkSize;

                    if (slotsAvailable > 0)
                    {
                        var toAbsorb = Math.Min(quantity, slotsAvailable);
                        if (component == "sides")
                        {
                            _absorbedSides += toAbsorb;
                        }
                        else
                        {
                            _absorbedDrinks += toAbsorb;
                        }
                        var remaining = quantity - toAbsorb;

                        // #179: FillBundleComponent owns the display rebuild AND the
                        // free-reference/resize pricing bookkeeping. This slot is "available"
                        // (just vacated by a remove, or never filled), but its free reference may
                        // still be set from BEFORE that vacate (see VacateBundleComponent) --
                        // refilling it with that SAME item is really a resize, priced and
                        // reported exactly like the explicit resize/modify path, never the free
                        // "included with your combo" wording a genuinely fresh fill gets.
                        var comboItem = FindBundleItemForComponent(component);
                        if (comboItem is not null)
                        {
                            FillBundleComponent(comboItem, component, itemName, size, price, display);
                            var upcharge = component == "sides" ? _absorbedSideUpcharge : _absorbedDrinkUpcharge;
                            if (upcharge != 0m)
                            {
                                result.ResizedComboComponent = component;
                                result.ComboComponentResizedFromSize = currentSize;
                                result.ComboComponentResizedToSize = size;
                                result.ComboDisplay = comboItem.Display;
                            }
                            else
                            {
                                result.AbsorbedIntoCombo = true;
                            }
                        }
                        else
                        {
                            result.AbsorbedIntoCombo = true;
                        }

                        if (remaining <= 0)
                        {
                            return;
                        }
                        quantity = remaining;
                    }
                    else if (currentItem.Length > 0 &&
                             MenuKeyValidator.MenuKey(currentItem) == MenuKeyValidator.MenuKey(itemName) &&
                             currentSize != size)
                    {
                        // #179: the slot is already full with THIS SAME item at a DIFFERENT
                        // size -- e.g. the combo's drink is a Medium Diet Coke and the model
                        // calls `add` for a Large Diet Coke. Resolve this as an in-place RESIZE
                        // of the slot (identical pricing to the explicit `modify` action below)
                        // instead of falling through to "Regular add" and creating a silent
                        // duplicate standalone line -- the exact #179 bug. Only ONE unit of
                        // quantity is ever a resize (there is only one slot); any remainder
                        // still becomes a genuine standalone add.
                        var comboItem = FindBundleItemForComponent(component);
                        if (comboItem is not null)
                        {
                            FillBundleComponent(comboItem, component, itemName, size, price, display);
                            result.ResizedComboComponent = component;
                            result.ComboComponentResizedFromSize = currentSize;
                            result.ComboComponentResizedToSize = size;
                            result.ComboDisplay = comboItem.Display;
                            var remaining = quantity - 1;
                            if (remaining <= 0)
                            {
                                return;
                            }
                            quantity = remaining;
                        }
                    }
                }
            }
        }

        // ── Regular add/merge ──
        var existingIndex = _items.FindIndex(oi => oi.Item == itemName && oi.Size == size);
        OrderItem bundleItemRef;
        if (existingIndex != -1)
        {
            _items[existingIndex].Quantity += quantity;
            bundleItemRef = _items[existingIndex];
        }
        else
        {
            bundleItemRef = new OrderItem { Item = itemName, Size = size, Quantity = quantity, Price = price, Display = display };
            _items.Add(bundleItemRef);
        }

        // ── Bundle pivot: absorb standalone sides/drinks into a newly added bundle ──
        if (isBundle)
        {
            var absorbedSide = false;
            var absorbedDrink = false;
            var itemsToRemove = new List<int>();
            for (var i = 0; i < _items.Count; i++)
            {
                var existing = _items[i];
                if (existing.Item == itemName)
                {
                    continue; // skip the bundle itself
                }
                var component = _menu.InferComboComponent(existing.Item);
                if (component == "sides" && ownBundleSlots.Contains("sides") && !absorbedSide)
                {
                    bundleItemRef.Components.Add(existing.Display);
                    // #179: shared fill/pricing bookkeeping (free-reference + resize pricing, see
                    // FillBundleComponent) -- unaffected here since this is always a first-time
                    // absorption into a brand-new bundle line (free, same as before this change)
                    // unless this session's slot already carries a free reference for this exact
                    // item from an earlier combo instance.
                    var existingSidePrice = _menu.PriceFor(existing.Item, existing.Size) ?? existing.Price;
                    FillBundleComponent(bundleItemRef, "sides", existing.Item, existing.Size, existingSidePrice, existing.Display);
                    if (existing.Quantity > 1)
                    {
                        existing.Quantity--;
                    }
                    else
                    {
                        itemsToRemove.Add(i);
                    }
                    absorbedSide = true;
                }
                else if (component == "drinks" && ownBundleSlots.Contains("drinks") && !absorbedDrink)
                {
                    bundleItemRef.Components.Add(existing.Display);
                    var existingDrinkPrice = _menu.PriceFor(existing.Item, existing.Size) ?? existing.Price;
                    FillBundleComponent(bundleItemRef, "drinks", existing.Item, existing.Size, existingDrinkPrice, existing.Display);
                    if (existing.Quantity > 1)
                    {
                        existing.Quantity--;
                    }
                    else
                    {
                        itemsToRemove.Add(i);
                    }
                    absorbedDrink = true;
                }
            }
            foreach (var idx in itemsToRemove.OrderByDescending(i => i))
            {
                _items.RemoveAt(idx);
            }
            if (absorbedSide)
            {
                _absorbedSides++;
            }
            if (absorbedDrink)
            {
                _absorbedDrinks++;
            }

            // ── #77: bundle-slot auto-fill -- a slot this item's own pack opts into filling by
            // default that WASN'T just absorbed above gets its default filler the instant the
            // bundle is added.
            var sizeLabel = _menu.NormalizeSize(size);
            var autofill = _menu.BundleAutoFill(itemName, sizeLabel);
            if (autofill.TryGetValue("sides", out var sideFiller) && !absorbedSide)
            {
                bundleItemRef.Components.Add(sideFiller);
                _absorbedSideDisplay = sideFiller;
                _absorbedSides++;
                result.Autofilled.Add(sideFiller);
            }
            if (autofill.TryGetValue("drinks", out var drinkFiller) && !absorbedDrink)
            {
                bundleItemRef.Components.Add(drinkFiller);
                _absorbedDrinkDisplay = drinkFiller;
                _absorbedDrinks++;
                result.Autofilled.Add(drinkFiller);
            }
        }
    }

    private void HandleModify(string itemName, string size, decimal price, string display, OrderUpdateResult result)
    {
        // #77: change an existing line's SIZE in place. Finds the first existing line for
        // itemName at ANY size (the guest doesn't repeat the old size out loud), re-prices it at
        // the new size from the menu (never the caller's own price), and re-prefixes its display.
        // Its own already-absorbed Components carry over unchanged.
        var existingIndex = _items.FindIndex(oi => oi.Item == itemName);
        if (existingIndex != -1)
        {
            var target = _items[existingIndex];
            var oldSize = target.Size;
            target.Size = size;
            target.Price = price;
            target.Display = display;
            result.ModifiedFromSize = oldSize;
            result.ModifiedToSize = size;
            return;
        }

        // #179: itemName isn't a raw order line, but it may still be a combo's side or drink
        // filling a slot via absorption -- the guest saying "make that a large" about the drink
        // that came WITH their combo. Resize that slot in place with the same free-reference
        // pricing the explicit remove-then-add and resize-via-add paths use, instead of treating
        // a perfectly resizable, real part of the order as a no-op just because it has no raw
        // line. (The `not_in_order` structured rejection for an item that is genuinely neither a
        // raw line nor an absorbed component is checked by the CALLER -- Tools.OrderToolExecutor,
        // via IsAbsorbedComponent -- BEFORE this method is ever invoked.)
        foreach (var component in new[] { "sides", "drinks" })
        {
            var currentItem = component == "sides" ? _absorbedSideItem : _absorbedDrinkItem;
            if (currentItem.Length == 0 || MenuKeyValidator.MenuKey(currentItem) != MenuKeyValidator.MenuKey(itemName))
            {
                continue;
            }
            var comboItem = FindBundleItemForComponent(component);
            if (comboItem is not null)
            {
                var oldComponentSize = component == "sides" ? _absorbedSideSize : _absorbedDrinkSize;
                FillBundleComponent(comboItem, component, itemName, size, price, display);
                result.ResizedComboComponent = component;
                result.ComboComponentResizedFromSize = oldComponentSize;
                result.ComboComponentResizedToSize = size;
                result.ComboDisplay = comboItem.Display;
            }
            break;
        }
    }

    private void HandleRemove(string itemName, string size, int quantity, OrderUpdateResult result)
    {
        var existingIndex = _items.FindIndex(oi => oi.Item == itemName && oi.Size == size);
        if (existingIndex != -1)
        {
            if (_items[existingIndex].Quantity > quantity)
            {
                _items[existingIndex].Quantity -= quantity;
            }
            else
            {
                _items.RemoveAt(existingIndex);
            }
            return;
        }

        // #179: itemName may currently be filling a combo's side/drink slot via absorption --
        // which has no raw line to match AT ALL regardless of size (the model's own "remove Diet
        // Coke Medium" call). Vacate that slot instead of silently no-op'ing, so the combo goes
        // back to incomplete and a follow-up add refills it (reprising as a resize -- see
        // FillBundleComponent) rather than creating a duplicate standalone line.
        foreach (var component in new[] { "sides", "drinks" })
        {
            var currentItem = component == "sides" ? _absorbedSideItem : _absorbedDrinkItem;
            if (currentItem.Length == 0 || MenuKeyValidator.MenuKey(currentItem) != MenuKeyValidator.MenuKey(itemName))
            {
                continue;
            }
            var vacateInfo = VacateBundleComponent(component);
            if (vacateInfo is { } info)
            {
                result.VacatedComboComponent = info.Component;
                result.VacatedDisplay = info.VacatedDisplay;
                result.ComboDisplay = info.ComboDisplay;
            }
            break;
        }
    }

    /// <summary>#179: the first order line whose OWN bundle slots (menu.BundleSlots) include
    /// <paramref name="component"/> ("sides"/"drinks") -- the same "first bundle item that
    /// actually owns this slot" rule the absorption display-rebuild always used, now shared by
    /// every slot-resize/vacate helper below too. Mirrors order_state.py's
    /// <c>_find_bundle_item_for_component</c>.</summary>
    private OrderItem? FindBundleItemForComponent(string component) =>
        _items.FirstOrDefault(item => _menu.BundleSlots(item.Item).Contains(component));

    /// <summary>Rebuilds <paramref name="comboItem"/>'s display string from whichever of
    /// <see cref="_absorbedSideDisplay"/>/<see cref="_absorbedDrinkDisplay"/> are currently
    /// non-empty. The single place that " w/ &lt;side&gt; &amp; &lt;drink&gt;" suffix is
    /// assembled, so every caller that changes what's filling a slot (first absorption, an
    /// in-place resize, or a #179 vacate) renders identically -- including reverting to the bare
    /// combo name (no " w/ ..." suffix at all) once every slot is empty again. Mirrors
    /// order_state.py's <c>_rebuild_bundle_display</c>.</summary>
    private void RebuildBundleDisplay(OrderItem comboItem)
    {
        var components = new List<string>();
        if (_absorbedSideDisplay.Length > 0)
        {
            components.Add(_absorbedSideDisplay);
        }
        if (_absorbedDrinkDisplay.Length > 0)
        {
            components.Add(_absorbedDrinkDisplay);
        }

        string baseName;
        string mods;
        var rawName = comboItem.Item;
        var parenIndex = rawName.IndexOf('(');
        if (parenIndex >= 0)
        {
            baseName = rawName[..parenIndex].Trim();
            mods = " " + rawName[parenIndex..];
        }
        else
        {
            baseName = rawName;
            mods = "";
        }

        comboItem.Display = components.Count > 0
            ? $"{baseName}{mods} w/ {string.Join(" & ", components)}"
            : $"{baseName}{mods}".Trim();
    }

    /// <summary>#179: the ONE place a bundle's side/drink slot gets (re)filled -- a first-time
    /// absorption, a refill after a `remove`-vacate, an explicit `modify`/resize of the slot, or
    /// an `add` of the same item at a different size while the slot is already full all route
    /// through here, so all four price identically.
    ///
    /// <para>Pricing (derived fresh from each pack's own per-size menu prices -- the original
    /// pre-persona app never had a resize path at all, so there was no prior behavior to carry
    /// forward): the FIRST time anything fills a slot in this combo instance is free, no matter
    /// its size (unchanged, pre-existing, intentional -- the absorbed component's own baseline
    /// zero-price behavior); that item+size becomes the slot's "free reference". Filling the SAME
    /// slot again with the SAME item (whether via an explicit resize, a same-item different-size
    /// `add` while full, or a `remove`-then-`add` of that same item after a vacate --
    /// _absorbedSideFreeItem/_absorbedDrinkFreeItem deliberately survive a vacate) charges the
    /// real menu-price delta between the new size and that original free reference -- the
    /// guest's actual incremental upsize cost, never an invented flat fee -- and is always
    /// computed fresh from the free reference (not cumulatively), so repeated resizes net out
    /// correctly. Swapping to a genuinely DIFFERENT item re-establishes a brand-new free
    /// reference (changing your mind about WHICH drink stays free; only resizing the SAME one
    /// costs extra).</para> Mirrors order_state.py's <c>_fill_bundle_component</c>.</summary>
    private void FillBundleComponent(
        OrderItem comboItem, string component, string itemName, string size, decimal price, string display)
    {
        var newKey = MenuKeyValidator.MenuKey(itemName);
        var freeItem = component == "sides" ? _absorbedSideFreeItem : _absorbedDrinkFreeItem;
        var freeSize = component == "sides" ? _absorbedSideFreeSize : _absorbedDrinkFreeSize;
        var oldUpcharge = component == "sides" ? _absorbedSideUpcharge : _absorbedDrinkUpcharge;
        decimal newUpcharge;

        if (freeItem.Length > 0 && MenuKeyValidator.MenuKey(freeItem) == newKey)
        {
            var freePrice = _menu.PriceFor(freeItem, freeSize) ?? price;
            newUpcharge = price - freePrice;
        }
        else
        {
            freeItem = itemName;
            freeSize = size;
            newUpcharge = 0m;
        }

        if (newUpcharge != oldUpcharge)
        {
            comboItem.Price = comboItem.Price - oldUpcharge + newUpcharge;
        }

        if (component == "sides")
        {
            _absorbedSideFreeItem = freeItem;
            _absorbedSideFreeSize = freeSize;
            _absorbedSideUpcharge = newUpcharge;
            _absorbedSideItem = itemName;
            _absorbedSideSize = size;
            _absorbedSideDisplay = display;
        }
        else
        {
            _absorbedDrinkFreeItem = freeItem;
            _absorbedDrinkFreeSize = freeSize;
            _absorbedDrinkUpcharge = newUpcharge;
            _absorbedDrinkItem = itemName;
            _absorbedDrinkSize = size;
            _absorbedDrinkDisplay = display;
        }

        RebuildBundleDisplay(comboItem);
    }

    /// <summary>#179: a `remove` targeting the item CURRENTLY filling a bundle's side/drink slot
    /// must vacate that slot -- there's no raw <see cref="OrderItem"/> for an absorbed component,
    /// so the ordinary remove-by-line lookup never finds one, which is why this used to be a
    /// silent no-op. Reverts whatever upcharge a prior resize added to the bundle line's own
    /// price (the guest isn't paying for a resized size that's no longer filling anything),
    /// decrements the slot's fill count so <see cref="GetComboRequirements"/> flags the combo
    /// incomplete again, and clears the slot's CURRENT item/size/display -- but deliberately
    /// leaves the free-reference fields alone (see <see cref="FillBundleComponent"/>) so a
    /// follow-up `add` of that SAME item reprices as a resize, exactly like the explicit resize
    /// action, instead of granting a second free upsize.
    ///
    /// Returns <c>null</c> if this slot isn't actually filled by a real bundle item right now
    /// (should not happen -- callers only invoke this once they've matched itemName against an
    /// already-set absorbed-component item -- defensive all the same). Mirrors order_state.py's
    /// <c>_vacate_bundle_component</c>.</summary>
    private (string Component, string VacatedDisplay, string ComboDisplay)? VacateBundleComponent(string component)
    {
        var comboItem = FindBundleItemForComponent(component);
        if (comboItem is null)
        {
            return null;
        }

        var upcharge = component == "sides" ? _absorbedSideUpcharge : _absorbedDrinkUpcharge;
        if (upcharge != 0m)
        {
            comboItem.Price -= upcharge;
        }

        string vacatedDisplay;
        if (component == "sides")
        {
            vacatedDisplay = _absorbedSideDisplay;
            _absorbedSides = Math.Max(0, _absorbedSides - 1);
            _absorbedSideItem = "";
            _absorbedSideSize = "";
            _absorbedSideDisplay = "";
            _absorbedSideUpcharge = 0m;
        }
        else
        {
            vacatedDisplay = _absorbedDrinkDisplay;
            _absorbedDrinks = Math.Max(0, _absorbedDrinks - 1);
            _absorbedDrinkItem = "";
            _absorbedDrinkSize = "";
            _absorbedDrinkDisplay = "";
            _absorbedDrinkUpcharge = 0m;
        }

        RebuildBundleDisplay(comboItem);
        return (component, vacatedDisplay, comboItem.Display);
    }

    /// <summary>#179: whether <paramref name="itemName"/> (any size) currently fills a bundle's
    /// side or drink slot via absorption -- it has no raw <see cref="OrderItem"/> of its own, so
    /// the ordinary <see cref="Items"/>-based line match (what `remove`/`modify` used to rely on
    /// exclusively) will never find it, even though it's a real, resizable part of the order.
    /// <c>Tools.OrderToolExecutor</c>'s `modify` on-menu/in-order gate calls this as a second
    /// chance before rejecting with `not_in_order`. Mirrors order_state.py's
    /// <c>is_absorbed_component</c>.</summary>
    public bool IsAbsorbedComponent(string itemName)
    {
        var key = MenuKeyValidator.MenuKey(itemName);
        if (_absorbedSideItem.Length > 0 && MenuKeyValidator.MenuKey(_absorbedSideItem) == key)
        {
            return true;
        }
        if (_absorbedDrinkItem.Length > 0 && MenuKeyValidator.MenuKey(_absorbedDrinkItem) == key)
        {
            return true;
        }
        return false;
    }


    /// <summary>Ports order_state.py's <c>_update_summary</c>: Decimal accumulation with NO
    /// intermediate rounding -- <see cref="Money.Format"/> is applied exactly once per display
    /// string, at the very end.</summary>
    private void UpdateSummary()
    {
        var happyHour = IsHappyHour();
        var total = 0m;
        foreach (var item in _items)
        {
            var itemTotal = item.Price * item.Quantity;
            if (happyHour && _menu.IsHappyHourDiscounted(item.Item))
            {
                itemTotal *= _happyHourDiscount;
            }
            total += itemTotal;
        }
        var tax = total * _taxRate;
        var finalTotal = total + tax;
        Summary = OrderSummary.Build(_items.ToList(), total, tax, finalTotal);
    }

    /// <summary>Ports order_state.py's <c>get_combo_requirements</c>: scans the order for bundles
    /// and returns what's still missing to complete them, in this persona's own wording
    /// (<c>bundle.missingPartText</c>).</summary>
    public ComboRequirements GetComboRequirements()
    {
        var sideCapacity = _items.Where(item => _menu.BundleSlots(item.Item).Contains("sides")).Sum(item => item.Quantity);
        var drinkCapacity = _items.Where(item => _menu.BundleSlots(item.Item).Contains("drinks")).Sum(item => item.Quantity);
        var sideCount = _items.Where(item => _menu.InferComboComponent(item.Item) == "sides").Sum(item => item.Quantity) + _absorbedSides;
        var drinkCount = _items.Where(item => _menu.InferComboComponent(item.Item) == "drinks").Sum(item => item.Quantity) + _absorbedDrinks;

        var missing = new List<string>();
        if (sideCount < sideCapacity)
        {
            missing.Add(_menu.BundleMissingPartText.GetValueOrDefault("sides", "a side"));
        }
        if (drinkCount < drinkCapacity)
        {
            missing.Add(_menu.BundleMissingPartText.GetValueOrDefault("drinks", "a drink"));
        }

        var promptHint = missing.Count > 0 ? $"Ask the guest for {string.Join(", and ", missing)} to finish their combo." : "";
        return new ComboRequirements(missing.Count == 0, missing, promptHint);
    }

    /// <summary>Ports order_state.py's <c>get_grouped_order_for_readback</c>: groups items with the
    /// same display name (spoken via this persona's own <c>sizes.spokenAs</c>) for a natural voice
    /// read-back, ending with the already-computed <see cref="OrderSummary.FinalTotalDisplay"/> --
    /// never re-derived here.</summary>
    public string GetGroupedOrderForReadback()
    {
        if (_items.Count == 0)
        {
            return "Your order is currently empty.";
        }

        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var item in _items)
        {
            var cleanName = _menu.Spoken(item.Display);
            if (cleanName.Contains('(') && cleanName.Contains(')'))
            {
                cleanName = cleanName.Replace("(", "with ").Replace(")", "");
            }
            if (!counts.ContainsKey(cleanName))
            {
                order.Add(cleanName);
            }
            counts[cleanName] = counts.GetValueOrDefault(cleanName) + item.Quantity;
        }

        var parts = order.Select(display => (counts[display] > 1 ? $"{counts[display]} " : "one ") + display).ToList();
        var summaryStr = parts.Count > 1
            ? string.Join(", ", parts[..^1]) + $", and {parts[^1]}"
            : parts[0];

        return $"I have {summaryStr}. Your total is {Summary.FinalTotalDisplay}. ";
    }

    /// <summary>Ports order_state.py's <c>reset_order</c> (#41): clears the order lines AND every
    /// combo-absorption bookkeeping field (counts and display strings) so a fresh combo after a
    /// reset never shows a stale absorbed component name from the previous order.</summary>
    public void ResetOrder()
    {
        _items.Clear();
        _absorbedSides = 0;
        _absorbedDrinks = 0;
        _absorbedSideDisplay = "";
        _absorbedDrinkDisplay = "";
        // #179: per-slot resize/vacate bookkeeping -- unlike VacateBundleComponent (which
        // deliberately keeps the free-reference fields alive across a single remove), a genuine
        // reset must clear everything, including the free-reference anchors, so a fresh combo
        // after a reset never carries over a stale free reference from the previous order.
        _absorbedSideItem = "";
        _absorbedSideSize = "";
        _absorbedSideFreeItem = "";
        _absorbedSideFreeSize = "";
        _absorbedSideUpcharge = 0m;
        _absorbedDrinkItem = "";
        _absorbedDrinkSize = "";
        _absorbedDrinkFreeItem = "";
        _absorbedDrinkFreeSize = "";
        _absorbedDrinkUpcharge = 0m;
        UpdateSummary();
    }
}
