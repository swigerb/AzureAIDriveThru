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
    public decimal ComboComponentUpcharge { get; set; }
    public string? ComboComponentUpchargeDisplay { get; set; }

    // PR #184 round 3 (Rick's review, item D/M4): set whenever a "wholeBundleSize" pack rejected
    // a component resize outright because it has no whole-meal price at the requested size --
    // the slot (and the bundle) are left exactly as they were. Mirrors order_state.py's
    // result_info["combo_component_resize_rejected"].
    public string? ComboComponentResizeRejected { get; set; }
    // PR #184 round 4 (Rick's review, item 3): the rejected bundle's own item name/current size,
    // so Tools.OrderToolExecutor can build an accurate rejection message naming the actual meal
    // instead of only the component that was asked to resize. Mirrors order_state.py's
    // result_info["combo_component_resize_rejected_bundle"/"_bundle_size"].
    public string? ComboComponentResizeRejectedBundle { get; set; }
    public string? ComboComponentResizeRejectedBundleSize { get; set; }

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

    /// <summary>The happy-hour note tools/get_order should append for this session. It is present
    /// only when this persona announces happy hour, the window is active, and at least one current
    /// raw order line is actually being multiplied by the happy-hour price. Bundle components are
    /// not raw order lines, matching <c>UpdateSummary</c>'s pricing rule.</summary>
    public string HappyHourBanner
    {
        get
        {
            if (!_happyHourAnnounce)
            {
                return "";
            }

            var discountedLines = HappyHourDiscountedLineDisplays();
            if (discountedLines.Count == 0)
            {
                return "";
            }

            return $" {_happyHourBanner} [HAPPY HOUR DISCOUNT APPLIED TO: {string.Join(", ", discountedLines)}]";
        }
    }

    private List<string> HappyHourDiscountedLineDisplays()
    {
        if (!IsHappyHour())
        {
            return [];
        }

        var discounted = new List<string>();
        foreach (var item in _items)
        {
            if (!_menu.IsHappyHourDiscounted(item.Item))
            {
                continue;
            }

            var display = item.Display.Length > 0 ? item.Display : item.Item;
            discounted.Add(item.Quantity > 1 ? $"{item.Quantity} x {display}" : display);
        }

        return discounted;
    }

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

        // ── Post-bundle absorption: side/drink fills an incomplete bundle's slot(s) ──
        if (!isBundle)
        {
            var component = _menu.InferComboComponent(itemName);
            if (component is "sides" or "drinks")
            {
                // PR #184 round 2 (Rick's review, item 2): repeatedly ask FindBundleSlot for the
                // next vacant slot (across every instance and every physical unit) and fill it,
                // until either *quantity* is exhausted or no vacant slot remains -- no
                // capacity-minus-filled arithmetic left to drift out of sync across two combos
                // or a quantity-N line.
                var absorbedCount = 0;
                var anyResize = false;
                OrderItem? lastComboItem = null;
                var remaining = quantity;
                while (remaining > 0)
                {
                    var found = FindBundleSlot(component);
                    if (found is not { } slotFound)
                    {
                        break;
                    }
                    var (comboItem, idx) = slotFound;
                    var (accepted, isResize, filledComboItem) = FillBundleComponent(comboItem, idx, component, itemName, size, display);
                    if (!accepted)
                    {
                        // Rejected (item D/M4): this pack has no whole-meal price at *size* --
                        // the slot is still vacant, never silently retry the SAME vacant slot
                        // forever. Stop absorbing; any remaining quantity falls through to a
                        // genuine standalone add below.
                        break;
                    }
                    comboItem = filledComboItem;
                    lastComboItem = comboItem;
                    anyResize = anyResize || isResize;
                    absorbedCount++;
                    remaining--;
                }
                if (lastComboItem is not null)
                {
                    if (anyResize)
                    {
                        result.ResizedComboComponent = component;
                        result.ComboComponentResizedToSize = size;
                        result.ComboDisplay = lastComboItem.Display;
                    }
                    else
                    {
                        result.AbsorbedIntoCombo = true;
                    }
                    SetComponentUpchargeResult(result, itemName, size);
                }
                if (remaining <= 0 && absorbedCount > 0)
                {
                    return;
                }
                if (absorbedCount == 0)
                {
                    // No vacant slot anywhere -- check whether some slot is already full with
                    // THIS SAME item at a DIFFERENT size (e.g. the combo's drink is a Medium
                    // cola and the model calls `add` for a Large cola). Resolve this as
                    // an in-place RESIZE of the slot instead of falling through to "Regular add"
                    // and creating a silent duplicate standalone line. Only ONE unit of
                    // *quantity* is ever a resize (there is only one matching slot); any
                    // remainder still becomes a genuine standalone add.
                    var found = FindBundleSlot(component, itemName, targetSize: size);
                    if (found is { } slotFound)
                    {
                        var (comboItem, idx) = slotFound;
                        var slot = SyncBundleSlotList(comboItem, component)[idx];
                        var currentSize = slot.Size;
                        var currentItem = slot.Item;
                        if (currentSize.Length > 0 && currentSize != size && currentItem.Length > 0 &&
                            MenuKeyValidator.MenuKey(currentItem) == MenuKeyValidator.MenuKey(itemName))
                        {
                            var (accepted, _, filledComboItem) = FillBundleComponent(comboItem, idx, component, itemName, size, display);
                            if (accepted)
                            {
                                comboItem = filledComboItem;
                                result.ResizedComboComponent = component;
                                result.ComboComponentResizedFromSize = currentSize;
                                result.ComboComponentResizedToSize = size;
                                result.ComboDisplay = comboItem.Display;
                                SetComponentUpchargeResult(result, itemName, size);
                                remaining = quantity - 1;
                                if (remaining <= 0)
                                {
                                    return;
                                }
                            }
                        }
                    }
                }
                quantity = remaining;
            }
        }

        // ── Regular add / bundle-instance creation ──
        var existingIndex = _items.FindIndex(oi => oi.Item == itemName && oi.Size == size);
        OrderItem bundleItemRef;
        int newUnits;
        if (existingIndex != -1)
        {
            _items[existingIndex].Quantity += quantity;
            bundleItemRef = _items[existingIndex];
            newUnits = quantity;
        }
        else
        {
            bundleItemRef = new OrderItem { Item = itemName, Size = size, Quantity = quantity, Price = price, Display = display };
            _items.Add(bundleItemRef);
            newUnits = quantity;
        }

        if (!isBundle)
        {
            return;
        }

        // PR #184 round 2 (Rick's review, item 2 -- "quantity-2 combos handled correctly"): grow
        // each own component's slot list to the new quantity (fresh empty records for the newly
        // added units) before the pivot/autofill below fills them.
        foreach (var component in ownBundleSlots)
        {
            SyncBundleSlotList(bundleItemRef, component);
        }

        // ── Bundle pivot: absorb standalone sides/drinks into the NEWLY ADDED unit(s) ──
        for (var unit = 0; unit < newUnits; unit++)
        {
            var absorbedSide = false;
            var absorbedDrink = false;
            var itemsToRemove = new List<int>();
            for (var i = 0; i < _items.Count; i++)
            {
                var existing = _items[i];
                if (ReferenceEquals(existing, bundleItemRef))
                {
                    continue; // skip the bundle itself
                }
                var component = _menu.InferComboComponent(existing.Item);
                // Only absorb a component this bundle's OWN slots actually include -- e.g. a
                // drinks-only combo must never free-absorb a pre-existing standalone side.
                if (component == "sides" && ownBundleSlots.Contains("sides") && !absorbedSide)
                {
                    var slotIdx = FirstVacantSlotIndex(bundleItemRef, "sides");
                    if (slotIdx is { } vacantIdx)
                    {
                        bundleItemRef.Components.Add(existing.Display);
                        FillBundleComponent(bundleItemRef, vacantIdx, "sides", existing.Item, existing.Size, existing.Display);
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
                }
                else if (component == "drinks" && ownBundleSlots.Contains("drinks") && !absorbedDrink)
                {
                    var slotIdx = FirstVacantSlotIndex(bundleItemRef, "drinks");
                    if (slotIdx is { } vacantIdx)
                    {
                        bundleItemRef.Components.Add(existing.Display);
                        FillBundleComponent(bundleItemRef, vacantIdx, "drinks", existing.Item, existing.Size, existing.Display);
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
            }
            foreach (var idx in itemsToRemove.OrderByDescending(i => i))
            {
                _items.RemoveAt(idx);
            }

            // ── #77: bundle-slot auto-fill -- a slot this item's OWN pack opts into filling by
            // default that WASN'T just absorbed above from a pre-existing standalone item gets
            // its default filler the instant the bundle is added.
            var sizeLabel = _menu.NormalizeSize(size);
            var autofill = _menu.BundleAutoFill(itemName, sizeLabel);
            // PR #184 round 3 (Rick's review, item E): the slot's Item must be the BASE,
            // on-menu item name, not the size-baked-in template text autofill itself holds --
            // see BundleAutoFillNames and ApplyWholeBundleResize's own autofill re-derivation
            // for the same fix.
            var autofillBase = _menu.BundleAutoFillNames(itemName);
            if (autofill.TryGetValue("sides", out var sideFillerDisplay) && !absorbedSide)
            {
                var slotIdx = FirstVacantSlotIndex(bundleItemRef, "sides");
                if (slotIdx is { } vacantIdx)
                {
                    var sideFillerItem = autofillBase.TryGetValue("sides", out var sb) ? sb : sideFillerDisplay;
                    bundleItemRef.Components.Add(sideFillerDisplay);
                    // PR #184 round 3 (Rick's review, item D): pass the RAW size -- the same
                    // literal value comboItem.Size holds (never the resolved/canonical
                    // sizeLabel, used only above for computing the filler display text) -- so
                    // FillBundleComponent's "size != comboItem.Size" feasibility check compares
                    // like with like, instead of spuriously treating a same-size initial autofill
                    // as a resize purely due to casing. Mirrors order_state.py's exact choice to
                    // pass `size`, not `size_label`, here.
                    FillBundleComponent(bundleItemRef, vacantIdx, "sides", sideFillerItem, size, sideFillerDisplay, autofill: true);
                    result.Autofilled.Add(sideFillerDisplay);
                }
            }
            if (autofill.TryGetValue("drinks", out var drinkFillerDisplay) && !absorbedDrink)
            {
                var slotIdx = FirstVacantSlotIndex(bundleItemRef, "drinks");
                if (slotIdx is { } vacantIdx)
                {
                    var drinkFillerItem = autofillBase.TryGetValue("drinks", out var db) ? db : drinkFillerDisplay;
                    bundleItemRef.Components.Add(drinkFillerDisplay);
                    // Same reasoning as the "sides" branch above: raw size, not sizeLabel.
                    FillBundleComponent(bundleItemRef, vacantIdx, "drinks", drinkFillerItem, size, drinkFillerDisplay, autofill: true);
                    result.Autofilled.Add(drinkFillerDisplay);
                }
            }
        }
    }

    /// <summary>The first (lowest-index) vacant slot within *comboItem*'s OWN *component* slot
    /// list -- used by the bundle-pivot/autofill loop above, which only ever fills slots on the
    /// bundle instance it just created/grew, never across other instances (unlike
    /// <see cref="FindBundleSlot"/>, which searches every instance for a cross-combo
    /// resize/vacate target).</summary>
    private int? FirstVacantSlotIndex(OrderItem comboItem, string component)
    {
        var slots = SyncBundleSlotList(comboItem, component);
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].Item.Length == 0)
            {
                return i;
            }
        }
        return null;
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

            // PR #184 round 2 (Rick's review, item 1 -- e.g. "make it a large meal"): directly
            // modifying a bundle's OWN line on a "wholeBundleSize" pack resizes the WHOLE bundle
            // -- its own size/price AND every filled slot's display together -- via
            // ApplyWholeBundleResize, never a bare size/price/display overwrite (which would
            // silently drop the already-absorbed "w/ <side> & <drink>" suffix).
            if (_menu.BundleResizeRule == "wholeBundleSize" && _menu.BundleSlots(target.Item).Count > 0)
            {
                if (ApplyWholeBundleResize(target, size))
                {
                    result.ModifiedFromSize = oldSize;
                    result.ModifiedToSize = size;
                    result.ComboDisplay = target.Display;
                }
                return;
            }

            target.Size = size;
            target.Price = price;
            target.Display = display;
            if (target.BundleSlots.Count > 0)
            {
                // Re-derive the "w/ <side> & <drink>" suffix the plain display assignment above
                // just overwrote -- resizing a non-"wholeBundleSize" bundle's own line doesn't
                // change what's filling its slots.
                RebuildBundleDisplay(target);
                RepriceBundleFromComponents(target);
            }
            result.ModifiedFromSize = oldSize;
            result.ModifiedToSize = size;
            return;
        }

        // #179/PR #184 round 2: itemName isn't a raw order line, but it may still be a combo's
        // side or drink filling a slot via absorption -- the guest saying "make that a large"
        // about the drink that came WITH their combo. Resize that slot in place with the same
        // pure-function pricing the explicit remove-then-add and resize-via-add paths use,
        // instead of rejecting a perfectly resizable, real part of the order as `not_in_order`
        // just because it has no raw line. (The `not_in_order` structured rejection for an item
        // that is genuinely neither a raw line nor an absorbed component is checked by the
        // CALLER -- Tools.OrderToolExecutor, via IsAbsorbedComponent -- BEFORE this method is
        // ever invoked.)
        foreach (var component in new[] { "sides", "drinks" })
        {
            var found = FindBundleSlot(component, itemName, targetSize: size);
            if (found is not { } slotFound)
            {
                continue;
            }
            var (comboItem, idx) = slotFound;
            var slot = SyncBundleSlotList(comboItem, component)[idx];
            if (slot.Item.Length == 0 || MenuKeyValidator.MenuKey(slot.Item) != MenuKeyValidator.MenuKey(itemName))
            {
                continue;
            }
            var oldComponentSize = slot.Size;
            var (accepted, _, filledComboItem) = FillBundleComponent(comboItem, idx, component, itemName, size, display);
            if (!accepted)
            {
                // Item D/M4: this "wholeBundleSize" pack has no whole-meal price at *size* --
                // leave the slot and the bundle exactly as they were and report a clean
                // rejection instead of a silent no-op or a mixed-size bundle.
                // PR #184 round 4 (Rick's review, item 3): also carry the bundle's own
                // name/current size so Tools.OrderToolExecutor can build an accurate, non-
                // misleading rejection message instead of reading only the component name.
                result.ComboComponentResizeRejected = component;
                result.ComboComponentResizeRejectedBundle = comboItem.Item;
                result.ComboComponentResizeRejectedBundleSize = comboItem.Size;
                break;
            }
            result.ResizedComboComponent = component;
            result.ComboComponentResizedFromSize = oldComponentSize;
            result.ComboComponentResizedToSize = size;
            result.ComboDisplay = filledComboItem.Display;
            SetComponentUpchargeResult(result, itemName, size);
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

        // #179/PR #184 round 2: itemName may be the exact size (the raw-line lookup just failed
        // above) -- or, like the live bug report, currently filling a combo's side/drink slot via
        // absorption, which has no raw line to match AT ALL regardless of size (the model's own
        // "remove cola Medium" call). Vacate that INSTANCE's slot (determinism-aware -- two
        // combos vacate independently) instead of silently no-op'ing, so that combo goes back to
        // incomplete and a follow-up add refills it (reprising as a resize -- see
        // FillBundleComponent) rather than creating a duplicate standalone line.
        foreach (var component in new[] { "sides", "drinks" })
        {
            var vacateInfo = VacateBundleComponent(component, itemName);
            if (vacateInfo is { } info)
            {
                result.VacatedComboComponent = info.Component;
                result.VacatedDisplay = info.VacatedDisplay;
                result.ComboDisplay = info.ComboDisplay;
                break;
            }
        }
    }

    /// <summary>#77: empty records for a brand-new physical unit's slot -- mirrors
    /// order_state.py's <c>_empty_bundle_slot</c>.</summary>
    private static BundleSlot EmptyBundleSlot() => new();

    /// <summary>PR #184 round 2 (Rick's review, item 2 -- "quantity-2 combos handled correctly"):
    /// each bundle INSTANCE's own <c>BundleSlots[component]</c> is a LIST with exactly
    /// <paramref name="comboItem"/>'s own <see cref="OrderItem.Quantity"/> entries, one per
    /// physical unit this single order line represents -- a "2 Big Mac Meals" line has 2
    /// independent side slots and 2 independent drink slots, not one shared slot (the old
    /// design's bug: a session-wide counter couldn't tell which of several physical units a given
    /// fill belonged to). Grows with fresh empty slot records when quantity increases; truncates
    /// from the END when quantity decreases -- called lazily on every read/write so there is
    /// exactly one place this invariant is enforced. Mirrors order_state.py's
    /// <c>_sync_bundle_slot_list</c>.</summary>
    private List<BundleSlot> SyncBundleSlotList(OrderItem comboItem, string component)
    {
        if (!comboItem.BundleSlots.TryGetValue(component, out var slots))
        {
            slots = [];
            comboItem.BundleSlots[component] = slots;
        }
        while (slots.Count < comboItem.Quantity)
        {
            slots.Add(EmptyBundleSlot());
        }
        if (slots.Count > comboItem.Quantity)
        {
            slots.RemoveRange(comboItem.Quantity, slots.Count - comboItem.Quantity);
        }
        return slots;
    }

    private decimal ComponentUpcharge(string itemName, string size)
    {
        if (_menu.BundleResizeRule != "componentUpcharge" || _menu.BundleIncludedSize.Length == 0 || itemName.Length == 0)
        {
            return 0m;
        }
        var actualPrice = _menu.PriceFor(itemName, size);
        var includedPrice = _menu.PriceFor(itemName, _menu.BundleIncludedSize);
        if (actualPrice is not { } actual || includedPrice is not { } included)
        {
            return 0m;
        }
        var delta = actual - included;
        return delta > 0m ? delta : 0m;
    }

    private void SetComponentUpchargeResult(OrderUpdateResult result, string itemName, string size)
    {
        var upcharge = ComponentUpcharge(itemName, size);
        if (upcharge > 0m)
        {
            result.ComboComponentUpcharge = upcharge;
            result.ComboComponentUpchargeDisplay = Money.Format(upcharge);
        }
    }

    private decimal BundleUnitUpcharge(OrderItem comboItem, int unitIndex, (string Component, string Item, string Size)? replacement = null)
    {
        var total = 0m;
        foreach (var component in _menu.BundleSlots(comboItem.Item))
        {
            var slots = SyncBundleSlotList(comboItem, component);
            var slot = unitIndex < slots.Count ? slots[unitIndex] : EmptyBundleSlot();
            var item = slot.Item;
            var size = slot.Size;
            if (replacement is { } repl && repl.Component == component)
            {
                item = repl.Item;
                size = repl.Size;
            }
            total += ComponentUpcharge(item, size);
        }
        return total;
    }

    private void RepriceBundleFromComponents(OrderItem comboItem)
    {
        var ownPrice = _menu.PriceFor(comboItem.Item, comboItem.Size);
        if (ownPrice is not { } price)
        {
            return;
        }
        if (_menu.BundleResizeRule == "componentUpcharge" && comboItem.Quantity > 0)
        {
            price += BundleUnitUpcharge(comboItem, 0);
        }
        comboItem.Price = price;
    }

    /// <summary>PR #184 round 2 (Rick's review, item 2): which (bundle instance, slot index) a
    /// slot-fill/resize/vacate targets, now that slot state is tracked per PHYSICAL UNIT of a
    /// combo INSTANCE rather than per session (the old design's root cause for two combos -- or a
    /// combo removed without a full <see cref="ResetOrder"/> -- bleeding slot state into each
    /// other, and for a quantity-2 combo line never telling which unit a fill belonged to).
    /// Candidates are every (order line, slot index) pair whose line's OWN bundle slots
    /// (menu.BundleSlots) include <paramref name="component"/>, across every physical unit of
    /// every such line.
    ///
    /// <para>If <paramref name="itemName"/> is given, the MOST RECENT matching instance (last in
    /// <see cref="Items"/>, i.e. the one added or merged-into most recently) whose slot is
    /// CURRENTLY filled by that exact item wins -- "resize the drink of whichever combo actually
    /// has that drink" (two combos; the guest says "make the Coke large" and only one of them
    /// currently has a Coke). If <paramref name="targetSize"/> is also given, a matching slot that
    /// is NOT already that size wins first; repeated "make the Cherry Limeade large" calls on a
    /// split quantity-2 combo then advance to the remaining medium unit instead of no-oping on the
    /// already-large unit. Otherwise (or when no instance's slot holds that item), the MOST RECENT
    /// instance with a VACANT slot for <paramref name="component"/> wins (lowest vacant index
    /// within that instance) -- a fresh absorption lands on whichever instance still needs
    /// filling, preferring the one most recently touched. Returns <c>null</c> when no slot
    /// matches <paramref name="itemName"/> (if given) and no slot anywhere is vacant -- callers
    /// must never be handed an already-FULL, non-matching slot to silently overwrite.</para>
    /// Mirrors order_state.py's <c>_find_bundle_slot</c>.</summary>
    private (OrderItem Item, int Index)? FindBundleSlot(string component, string? itemName = null, string? targetSize = null)
    {
        var candidates = _items.Where(it => _menu.BundleSlots(it.Item).Contains(component)).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        if (itemName is not null)
        {
            var key = MenuKeyValidator.MenuKey(itemName);
            if (targetSize is not null)
            {
                for (var i = candidates.Count - 1; i >= 0; i--)
                {
                    var comboItem = candidates[i];
                    var slots = SyncBundleSlotList(comboItem, component);
                    for (var idx = 0; idx < slots.Count; idx++)
                    {
                        var slot = slots[idx];
                        if (slot.Item.Length > 0 && MenuKeyValidator.MenuKey(slot.Item) == key && slot.Size != targetSize)
                        {
                            return (comboItem, idx);
                        }
                    }
                }
            }
            for (var i = candidates.Count - 1; i >= 0; i--)
            {
                var comboItem = candidates[i];
                var slots = SyncBundleSlotList(comboItem, component);
                for (var idx = 0; idx < slots.Count; idx++)
                {
                    var slot = slots[idx];
                    if (slot.Item.Length > 0 && MenuKeyValidator.MenuKey(slot.Item) == key)
                    {
                        return (comboItem, idx);
                    }
                }
            }
        }
        for (var i = candidates.Count - 1; i >= 0; i--)
        {
            var comboItem = candidates[i];
            var slots = SyncBundleSlotList(comboItem, component);
            for (var idx = 0; idx < slots.Count; idx++)
            {
                if (slots[idx].Item.Length == 0)
                {
                    return (comboItem, idx);
                }
            }
        }
        return null;
    }

    /// <summary>Rebuilds <paramref name="comboItem"/>'s display string from whichever of its OWN
    /// (per-instance, PR #184 round 2) <see cref="OrderItem.BundleSlots"/> are currently filled,
    /// across every physical unit this line represents. The single place that " w/ &lt;side&gt;
    /// &amp; &lt;drink&gt;" suffix is assembled, so every caller that changes what's filling a
    /// slot (first absorption, an in-place resize, or a vacate) renders identically -- including
    /// reverting to the bare bundle name (no " w/ ..." suffix at all) once every slot is empty
    /// again. A multi-quantity line whose units hold different items lists every filled unit's
    /// display, comma-separated, within its component's slot of the "w/ ... &amp; ..." suffix --
    /// the common case (quantity 1, or several identical units) collapses to the same single
    /// label as before. Mirrors order_state.py's <c>_rebuild_bundle_display</c>.</summary>
    private void RebuildBundleDisplay(OrderItem comboItem)
    {
        var displayComponents = new List<string>();
        var wireComponents = new List<string>();
        var wireUpcharges = new List<decimal>();
        foreach (var component in new[] { "sides", "drinks" })
        {
            if (!comboItem.BundleSlots.TryGetValue(component, out var slots))
            {
                continue;
            }
            var filled = new List<string>();
            foreach (var slot in slots.Where(s => s.Display.Length > 0))
            {
                slot.Upcharge = ComponentUpcharge(slot.Item, slot.Size);
                filled.Add(slot.Display);
                wireUpcharges.Add(slot.Upcharge);
            }
            if (filled.Count > 0)
            {
                displayComponents.Add(string.Join(", ", filled));
                wireComponents.AddRange(filled);
            }
        }
        comboItem.Components = wireComponents;
        comboItem.ComponentUpcharges = wireUpcharges;

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

        comboItem.Display = displayComponents.Count > 0
            ? $"{baseName}{mods} w/ {string.Join(" & ", displayComponents)}"
            : $"{baseName}{mods}".Trim();
    }

    /// <summary>PR #184 round 2 (Rick's review, item 1 -- "wholeBundleSize" packs): resize the
    /// ENTIRE bundle instance (every physical unit of this order line) to <paramref
    /// name="newSize"/> -- its own size/price change TOGETHER, and every slot currently filling
    /// it is relabeled (never re-priced; a slot item is never separately priced on this persona's
    /// own rule -- see <see cref="FillBundleComponent"/>) to match, same as the original app this
    /// pack's pricing was ported from ("the side and drink sizes will automatically update to
    /// match"). An autofilled slot (<see cref="BundleSlot.Autofill"/>, e.g. the default side no
    /// explicit `add` ever named) re-derives its filler text fresh from menu.BundleAutoFill at
    /// the new size, rather than naively re-prefixing its already-size-baked-in template string;
    /// every other slot holds a real item name and is simply re-prefixed with the new size label.
    ///
    /// Returns <c>false</c> (no-op; caller falls back to a plain size/price assignment) if
    /// <paramref name="newSize"/> is already this bundle's own current size, or if this persona's
    /// own menu has no price for the bundle's own item at <paramref name="newSize"/>. Mirrors
    /// order_state.py's <c>_apply_whole_bundle_resize</c>.</summary>
    private bool ApplyWholeBundleResize(OrderItem comboItem, string newSize)
    {
        if (newSize == comboItem.Size)
        {
            return false;
        }
        var newPrice = _menu.PriceFor(comboItem.Item, newSize);
        if (newPrice is not { } price)
        {
            return false;
        }
        comboItem.Size = newSize;
        comboItem.Price = price;
        var resolved = _menu.NormalizeSize(newSize);
        var sizePrefix = resolved.Length > 0 ? $"{resolved} " : "";
        foreach (var (component, slots) in comboItem.BundleSlots)
        {
            foreach (var slot in slots)
            {
                if (slot.Item.Length == 0)
                {
                    continue;
                }
                if (slot.Autofill)
                {
                    var fresh = _menu.BundleAutoFill(comboItem.Item, resolved);
                    if (fresh.TryGetValue(component, out var fillerDisplay) && fillerDisplay.Length > 0)
                    {
                        // PR #184 round 3 (Rick's review, item E): slot.Item must stay the BASE,
                        // on-menu item name (e.g. "World Famous Fries®"), never the size-baked-in
                        // template text -- the same invariant every non-autofill slot already
                        // holds (Display carries the size prefix; Item never does). Otherwise a
                        // later `modify`/`remove` naming the real menu item (what the guest
                        // actually says, and what OrderToolExecutor resolves against the menu)
                        // can never match this slot via MenuKeyValidator.MenuKey and is wrongly
                        // rejected as not_in_order.
                        var freshBase = _menu.BundleAutoFillNames(comboItem.Item);
                        var fillerItem = freshBase.TryGetValue(component, out var baseName) ? baseName : fillerDisplay;
                        slot.Item = fillerItem;
                        slot.Display = fillerDisplay;
                        slot.LastItem = fillerItem;
                        slot.LastSize = newSize;
                    }
                }
                else
                {
                    slot.Display = $"{sizePrefix}{slot.Item}".Trim();
                    slot.LastSize = newSize;
                }
                slot.Size = newSize;
            }
        }
        // Rebuild the bundle's own "w/ <side> & <drink>" display from the relabeled slots above
        // -- every caller (the direct bundle-line `modify` path, and FillBundleComponent's own
        // call for a "wholeBundleSize" slot-fill) gets a correctly rendered display with no
        // separate, easy-to-forget rebuild step of its own.
        RebuildBundleDisplay(comboItem);
        return true;
    }

    /// <summary>PR #184 round 3 (Rick's review, item F -- quantity-2+ "wholeBundleSize" lines): a
    /// component resize under "wholeBundleSize" is really a whole-MEAL resize (see
    /// <see cref="ApplyWholeBundleResize"/>) -- on a quantity>1 line, naively applying that to
    /// <paramref name="comboItem"/> would silently resize (and reprice) EVERY physical unit
    /// sharing this one line's single Price/Size fields, even though the guest/model only ever
    /// named ONE unit's component. Split the physical unit at <paramref name="unitIndex"/> off
    /// into its own new, independent quantity=1 <see cref="OrderItem"/> -- carrying that unit's
    /// own current side/drink slot contents with it -- so the resize that follows in
    /// <see cref="FillBundleComponent"/> applies only to that one unit; the original line shrinks
    /// by one and keeps its old size/price for its remaining units, untouched.
    ///
    /// Returns the new split-off <see cref="OrderItem"/>, inserted directly after <paramref
    /// name="comboItem"/> so read-back order stays stable. Mirrors order_state.py's
    /// <c>_split_bundle_unit</c>.</summary>
    private OrderItem SplitBundleUnit(OrderItem comboItem, int unitIndex)
    {
        var splitItem = new OrderItem
        {
            Item = comboItem.Item,
            Size = comboItem.Size,
            Quantity = 1,
            Price = comboItem.Price,
            Display = comboItem.Display,
            Components = [.. comboItem.Components],
            ComponentUpcharges = [.. comboItem.ComponentUpcharges],
        };
        foreach (var component in _menu.BundleSlots(comboItem.Item))
        {
            var slots = SyncBundleSlotList(comboItem, component);
            BundleSlot taken;
            if (unitIndex < slots.Count)
            {
                taken = slots[unitIndex];
                slots.RemoveAt(unitIndex);
            }
            else
            {
                taken = new BundleSlot();
            }
            splitItem.BundleSlots[component] = [taken];
        }
        comboItem.Quantity--;
        var insertAt = _items.FindIndex(oi => ReferenceEquals(oi, comboItem)) + 1;
        _items.Insert(insertAt, splitItem);
        RepriceBundleFromComponents(comboItem);
        RepriceBundleFromComponents(splitItem);
        RebuildBundleDisplay(comboItem);
        RebuildBundleDisplay(splitItem);
        return splitItem;
    }

    /// <summary>PR #184 round 2 (Rick's review, item 1): the ONE place a bundle's side/drink slot
    /// gets (re)filled -- a first-time absorption, a bundle-slot autofill, a refill after a
    /// `remove`-vacate, an explicit `modify`/resize of the slot, or an `add` of the same item at
    /// a different size while the slot is already full all route through here. <paramref
    /// name="slotIndex"/> identifies WHICH physical unit of <paramref name="comboItem"/> (a
    /// quantity-N bundle line has N independent slots per component) this fill targets -- callers
    /// resolve it via <see cref="FindBundleSlot"/> (cross-instance resize/vacate targets) or
    /// directly (filling a specific just-created/just-grown unit during the bundle pivot).
    ///
    /// <para>Pricing is now a PURE function of the bundle instance's OWN current state -- never a
    /// stateful delta/"free reference" computation (the root cause of the #179-round-1 path
    /// dependence Rick's PR #184 review flagged). "includedAnySize" packs (default) always reset
    /// the bundle's own price to its own flat menu price, no matter what size fills a slot -- a
    /// side or drink is included AT ANY SIZE, so there is never a credit or an upcharge to
    /// compute, and ordering a size up front vs. resizing into it afterward always totals the
    /// same. "wholeBundleSize" packs instead resize the WHOLE bundle
    /// (<see cref="ApplyWholeBundleResize"/>) whenever a genuine RESIZE (<c>isResize</c>, same
    /// item already filling this slot) targets a size different from the bundle's own current
    /// size -- PR #184 round 3 (Rick's review, item D/M4): ONLY when that resize can actually
    /// happen (this pack prices the bundle's own item at the requested size); otherwise the fill
    /// is rejected outright, so a slot is never relabeled to a size the bundle itself didn't (and
    /// won't) move to. PR #184 round 4 (Rick's review, items 1/2): this cascade/rejection gate
    /// requires <c>isResize</c> precisely so a slot's first-ever fill (an absorption, autofill,
    /// or bundle-pivot absorption) NEVER cascades or rejects, no matter how many priced sizes the
    /// bundle has -- fixing both a Standard-only bundle's regression (it couldn't absorb its
    /// first S/M/L drink) and the path-dependent totals Rick's review flagged (first-fill order
    /// no longer matters). Round 3 item F: a feasible resize on a quantity>1 line first splits
    /// the targeted unit off (<see cref="SplitBundleUnit"/>) so only that ONE unit is
    /// affected. "componentUpcharge" packs keep the bundle's own menu price plus the sum of
    /// positive per-component deltas over the pack-declared included size. A quantity>1 line also
    /// splits the targeted physical unit whenever that unit's repriced component-upcharge total
    /// would differ from its siblings, so <see cref="OrderItem.Price"/> remains a per-unit price
    /// rather than an averaged line price.</para>
    ///
    /// Returns <c>(accepted, isResize, comboItem)</c>: <c>accepted</c> is <c>false</c> (slot left
    /// untouched) when a "wholeBundleSize" RESIZE was requested but this pack has no price for the
    /// bundle's own item at that size -- callers must treat this as a clean rejection (no state
    /// changed at all), never as a successful fill. When <c>accepted</c> is <c>true</c>,
    /// <c>isResize</c> is whether <paramref name="itemName"/> is the SAME item that last filled
    /// (or still fills) this slot on this bundle instance -- a genuine RESIZE, not a fresh fill of
    /// a different item -- so callers can report "resized" vs. "included with your combo" wording.
    /// The returned <see cref="OrderItem"/> is returned because a quantity>1 resize/upcharge
    /// change may have split <paramref name="comboItem"/> into a new line -- callers must use the
    /// returned instance for any further reads (e.g. Display), not the one they passed in. Mirrors
    /// order_state.py's <c>_fill_bundle_component</c>.</summary>
    private (bool Accepted, bool IsResize, OrderItem ComboItem) FillBundleComponent(
        OrderItem comboItem, int slotIndex, string component, string itemName, string size, string display,
        bool autofill = false)
    {
        var slots = SyncBundleSlotList(comboItem, component);
        var slot = slots[slotIndex];
        var isResize = slot.LastItem.Length > 0 && MenuKeyValidator.MenuKey(slot.LastItem) == MenuKeyValidator.MenuKey(itemName);

        // PR #184 round 4 (Rick's review, items 1/2): the "wholeBundleSize" cascade/rejection
        // below must only ever fire for a genuine RESIZE of an item that's already filling this
        // slot (isResize) -- NEVER for the slot's first-ever fill (a fresh absorption, autofill,
        // or bundle-pivot absorption always computes isResize=false here, since LastItem is still
        // empty or holds a different item). Two round-3 bugs traced to the same root cause, both
        // fixed by this one gate:
        //   Item 1 (Standard-only bundle regression): a Standard-only bundle (one priced size,
        //   e.g. a single-size meal) absorbing its first S/M/L drink used to hit this same
        //   size-mismatch branch and get rejected outright, even though nothing is being resized
        //   -- the drink is simply filling an empty slot for the first time.
        //   Item 2 (path dependence): "add meal, then add a differently-sized drink" vs. "add
        //   drink, then add the meal" must total the same -- but the old code could cascade/
        //   reject on ONE of those orderings (whichever call happened to run with isResize
        //   still true from stale pre-split state) and not the other.
        // Evidence (Rick's review item 2 -- coordinator decision): the original app
        // (swigerb/McDonalds_AI_DriveThru)'s absorption path never cascades, rejects, or
        // relabels a mismatched-size component at all -- only an EXPLICIT modify of the meal's
        // own line resizes every slot. This gate reproduces that: first-time absorption always
        // falls through to the "own flat price" branch below (same as includedAnySize), and an
        // explicit resize (isResize=true, computed from the slot's own prior fill) still cascades
        // or cleanly rejects exactly as round 3 intended. Rick's own suggested "more than one
        // priced size" condition was deliberately NOT added here instead: it would also have
        // broken the still-desired rejection for an EXPLICIT resize of a Standard-only bundle's
        // component, which must stay a rejection regardless of how many price tiers exist.
        if (_menu.BundleResizeRule == "wholeBundleSize" && isResize && size != comboItem.Size)
        {
            if (_menu.PriceFor(comboItem.Item, size) is null)
            {
                return (false, false, comboItem);
            }
            if (comboItem.Quantity > 1)
            {
                comboItem = SplitBundleUnit(comboItem, slotIndex);
                slotIndex = 0;
                // SplitBundleUnit moves the SAME BundleSlot object (a class, not a struct) onto
                // the new split-off OrderItem's own slot list -- `slot` still refers to it, no
                // re-fetch needed.
            }
        }

        if (_menu.BundleResizeRule == "componentUpcharge" && comboItem.Quantity > 1)
        {
            // #205: component upcharges are per physical meal. If this fill/resize would make the
            // targeted unit's upcharge differ from its siblings, split it first so OrderItem.Price
            // remains a per-unit price, never an averaged line total.
            var prospective = BundleUnitUpcharge(comboItem, slotIndex, (component, itemName, size));
            var differs = Enumerable.Range(0, comboItem.Quantity)
                .Where(i => i != slotIndex)
                .Any(i => BundleUnitUpcharge(comboItem, i) != prospective);
            if (differs)
            {
                comboItem = SplitBundleUnit(comboItem, slotIndex);
            }
        }

        slot.Item = itemName;
        slot.Size = size;
        slot.Display = display;
        slot.LastItem = itemName;
        slot.LastSize = size;
        slot.Autofill = autofill;
        slot.Upcharge = ComponentUpcharge(itemName, size);

        if (_menu.BundleResizeRule == "wholeBundleSize" && isResize)
        {
            ApplyWholeBundleResize(comboItem, size);
        }
        else
        {
            RepriceBundleFromComponents(comboItem);
        }
        RebuildBundleDisplay(comboItem);
        return (true, isResize, comboItem);
    }

    /// <summary>PR #184 round 2: a `remove` targeting the item CURRENTLY filling a bundle's
    /// side/drink slot must vacate THAT instance's THAT unit's slot (via <paramref
    /// name="itemName"/>-aware lookup, so two combos -- or two units of one quantity-N combo --
    /// vacate independently) -- there's no raw <see cref="OrderItem"/> for an absorbed component,
    /// so the ordinary remove-by-line lookup never finds one. Clears the slot's CURRENT
    /// item/size/display (so <see cref="GetComboRequirements"/> flags this unit incomplete
    /// again) but deliberately leaves <see cref="BundleSlot.LastItem"/>/<see
    /// cref="BundleSlot.LastSize"/> alone (see <see cref="FillBundleComponent"/>) so a follow-up
    /// `add` of that SAME item reports as a resize, not a second fresh absorption. Never changes
    /// the bundle's own price/size -- vacating a slot doesn't un-resize a "wholeBundleSize"
    /// bundle, and an "includedAnySize" bundle's price was never affected by what filled the
    /// slot.
    ///
    /// Returns <c>null</c> if this slot isn't actually filled by <paramref name="itemName"/>
    /// right now (should not happen -- callers only invoke this once they've matched it against
    /// an already-filled slot -- defensive all the same). Mirrors order_state.py's
    /// <c>_vacate_bundle_component</c>.</summary>
    private (string Component, string VacatedDisplay, string ComboDisplay)? VacateBundleComponent(string component, string itemName)
    {
        var found = FindBundleSlot(component, itemName);
        if (found is not { } slotFound)
        {
            return null;
        }
        var (comboItem, idx) = slotFound;
        var slots = SyncBundleSlotList(comboItem, component);
        var slot = slots[idx];
        // Guard against FindBundleSlot's vacant-search branch handing back a slot that does NOT
        // actually hold itemName right now (e.g. this component is filled elsewhere by a
        // different item) -- only a genuine holder match may be vacated.
        if (slot.Item.Length == 0 || MenuKeyValidator.MenuKey(slot.Item) != MenuKeyValidator.MenuKey(itemName))
        {
            return null;
        }
        var vacatedDisplay = slot.Display;
        slot.Item = "";
        slot.Size = "";
        slot.Display = "";
        slot.Autofill = false;
        slot.Upcharge = 0m;
        RepriceBundleFromComponents(comboItem);
        RebuildBundleDisplay(comboItem);
        return (component, vacatedDisplay, comboItem.Display);
    }

    /// <summary>PR #184 round 2: whether <paramref name="itemName"/> (any size) currently fills
    /// ANY combo instance's side or drink slot via absorption, across the whole order -- it has
    /// no raw <see cref="OrderItem"/> of its own, so the ordinary <see cref="Items"/>-based line
    /// match (what `remove`/`modify` used to rely on exclusively) will never find it, even though
    /// it's a real, resizable part of the order. <c>Tools.OrderToolExecutor</c>'s `modify`
    /// on-menu/in-order gate calls this as a second chance before rejecting with `not_in_order`.
    /// Mirrors order_state.py's <c>is_absorbed_component</c>.</summary>
    public bool IsAbsorbedComponent(string itemName)
    {
        var key = MenuKeyValidator.MenuKey(itemName);
        foreach (var comboItem in _items)
        {
            foreach (var component in new[] { "sides", "drinks" })
            {
                if (!comboItem.BundleSlots.TryGetValue(component, out var slots))
                {
                    continue;
                }
                if (slots.Any(slot => slot.Item.Length > 0 && MenuKeyValidator.MenuKey(slot.Item) == key))
                {
                    return true;
                }
            }
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
        Summary = OrderSummary.Build(_items.ToList(), total, tax, finalTotal, ComposeSpokenReadBack(_items, finalTotal));
    }

    /// <summary>Ports order_state.py's <c>get_combo_requirements</c>: scans the order for bundles
    /// and returns what's still missing to complete them, in this persona's own wording
    /// (<c>bundle.missingPartText</c>).
    ///
    /// <para>PR #184 round 2 (Rick's review, item 2): completeness is now determined PER BUNDLE
    /// INSTANCE and PER PHYSICAL UNIT from its own <see cref="OrderItem.BundleSlots"/> state (a
    /// quantity-N line has N independent slots per component -- see
    /// <see cref="SyncBundleSlotList"/>), not a session-wide capacity/fill counter -- two combos
    /// (one fully built, one still missing its drink), or two units of one quantity-2 line,
    /// report independently instead of netting out against each other's counts.</para></summary>
    public ComboRequirements GetComboRequirements()
    {
        var missingComponents = new HashSet<string>();
        foreach (var item in _items)
        {
            foreach (var component in _menu.BundleSlots(item.Item))
            {
                var slots = SyncBundleSlotList(item, component);
                if (slots.Any(slot => slot.Item.Length == 0))
                {
                    missingComponents.Add(component);
                }
            }
        }

        var missing = new List<string>();
        if (missingComponents.Contains("sides"))
        {
            missing.Add(_menu.BundleMissingPartText.GetValueOrDefault("sides", "a side"));
        }
        if (missingComponents.Contains("drinks"))
        {
            missing.Add(_menu.BundleMissingPartText.GetValueOrDefault("drinks", "a drink"));
        }

        var promptHint = missing.Count > 0 ? $"Ask the guest for {string.Join(", and ", missing)} to finish their combo." : "";
        return new ComboRequirements(missing.Count == 0, missing, promptHint);
    }

    /// <summary>Ports order_state.py's <c>get_grouped_order_for_readback</c> / the new
    /// <c>_compose_spoken_readback</c> extraction (issue #304): groups items with the same display
    /// name (spoken via this persona's own <c>sizes.spokenAs</c> + per-item <c>spokenName</c>) for
    /// a natural voice read-back, ending with the already-computed <paramref
    /// name="finalTotalDisplay"/> -- never re-derived here. Cached once per <see cref="UpdateSummary"/>
    /// call onto <see cref="OrderSummary.SpokenReadBack"/> so it can never drift from the tool
    /// response that serializes the same <see cref="OrderSummary"/>.</summary>
    private string ComposeSpokenReadBack(IReadOnlyList<OrderItem> items, decimal finalTotal)
    {
        if (items.Count == 0)
        {
            return "Your order is currently empty.";
        }

        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var item in items)
        {
            var cleanName = _menu.Spoken(item.Display);
            if (cleanName.Contains('(') && cleanName.Contains(')'))
            {
                cleanName = cleanName.Replace("(", "with ").Replace(")", "");
            }
            var positiveUpcharges = item.ComponentUpcharges.Where(upcharge => upcharge > 0m).ToList();
            if (positiveUpcharges.Count > 0)
            {
                var upchargeTotal = positiveUpcharges.Sum();
                cleanName = positiveUpcharges.Count == 1
                    ? $"{cleanName} with a {Money.FormatMoneySpoken(upchargeTotal)} upcharge"
                    : $"{cleanName} with {Money.FormatMoneySpoken(upchargeTotal)} in component upcharges";
            }
            if (!counts.ContainsKey(cleanName))
            {
                order.Add(cleanName);
            }
            counts[cleanName] = counts.GetValueOrDefault(cleanName) + item.Quantity;
        }

        // #313 (Rick's review, item 2): a bare digit quantity read next to a count-based size
        // (e.g. "3 10 Count Glazed Munch-kins Donut Hole Treats") is ambiguous -- spelling the
        // quantity out as a word removes it.
        var parts = order.Select(display => $"{Money.NumberToWords(counts[display])} {display}").ToList();
        var summaryStr = parts.Count > 1
            ? string.Join(", ", parts[..^1]) + $", and {parts[^1]}"
            : parts[0];

        return $"I have {summaryStr}. Your total is {Money.FormatMoneySpoken(finalTotal)}. ";
    }

    /// <summary>Ports order_state.py's <c>get_grouped_order_for_readback</c>: a one-line read of the
    /// already-cached <see cref="OrderSummary.SpokenReadBack"/> (issue #304) -- guarantees this can
    /// never drift from the <see cref="OrderSummary"/> the get_order tool actually returns.</summary>
    public string GetGroupedOrderForReadback() => Summary.SpokenReadBack;

    /// <summary>Ports order_state.py's <c>reset_order</c> (#41): clears the order lines. PR #184
    /// round 2 (Rick's review, item 3 -- "removing the combo clears its slot state"): all
    /// bundle-slot state now lives ON each <see cref="OrderItem"/> instance itself (see
    /// <see cref="OrderItem.BundleSlots"/>), not in separate session-level bookkeeping fields, so
    /// clearing <see cref="_items"/> is the whole reset -- there is no longer any stale
    /// session-wide absorption state that could leak a previous order's free reference or
    /// filled-slot display into a fresh combo.</summary>
    public void ResetOrder()
    {
        _items.Clear();
        UpdateSummary();
    }
}
