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
                HandleRemove(itemName, size, quantity);
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
                        result.AbsorbedIntoCombo = true;

                        // Update the bundle item's display to show the absorbed component -- find
                        // a bundle item whose own slots actually include this component.
                        foreach (var comboItem in _items)
                        {
                            if (!_menu.BundleSlots(comboItem.Item).Contains(component))
                            {
                                continue;
                            }
                            var components = new List<string>();
                            if (_absorbedSideDisplay.Length > 0)
                            {
                                components.Add(_absorbedSideDisplay);
                            }
                            if (_absorbedDrinkDisplay.Length > 0)
                            {
                                components.Add(_absorbedDrinkDisplay);
                            }
                            if (component == "sides")
                            {
                                _absorbedSideDisplay = display;
                                if (!components.Contains(display))
                                {
                                    components.Add(display);
                                }
                            }
                            else
                            {
                                _absorbedDrinkDisplay = display;
                                if (!components.Contains(display))
                                {
                                    components.Add(display);
                                }
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
                            comboItem.Display = $"{baseName}{mods} w/ {string.Join(" & ", components)}";
                            break;
                        }

                        if (remaining <= 0)
                        {
                            return;
                        }
                        quantity = remaining;
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
                    _absorbedSideDisplay = existing.Display;
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
                    _absorbedDrinkDisplay = existing.Display;
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
        if (existingIndex == -1)
        {
            // Mirrors Python exactly: this is a no-op here. The `not_in_order` structured
            // rejection for a modify of an item that isn't in the order is checked by the CALLER
            // (Tools.OrderToolExecutor), BEFORE this method is ever invoked -- see
            // docs/persona-architecture.md section 6 and Tools/OrderToolExecutor.cs.
            return;
        }
        var target = _items[existingIndex];
        var oldSize = target.Size;
        target.Size = size;
        target.Price = price;
        target.Display = display;
        result.ModifiedFromSize = oldSize;
        result.ModifiedToSize = size;
    }

    private void HandleRemove(string itemName, string size, int quantity)
    {
        var existingIndex = _items.FindIndex(oi => oi.Item == itemName && oi.Size == size);
        if (existingIndex == -1)
        {
            return;
        }
        if (_items[existingIndex].Quantity > quantity)
        {
            _items[existingIndex].Quantity -= quantity;
        }
        else
        {
            _items.RemoveAt(existingIndex);
        }
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
        UpdateSummary();
    }
}
