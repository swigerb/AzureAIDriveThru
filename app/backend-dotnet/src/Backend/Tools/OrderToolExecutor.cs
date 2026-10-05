using System.Text.Json;
using Backend.Personas;
using Backend.Prompts;

namespace Backend.Tools;

/// <summary>
/// Port of app/backend/tools.py's <c>update_order</c>/<c>get_order</c>/<c>reset_order</c> (docs/
/// persona-architecture.md section 6; issues #14/#36/#73/#74/#77/#104/#113/#125). One instance is
/// owned by exactly one session's <c>SessionActor</c> -- see <see cref="IToolExecutor"/>'s own
/// design note -- closed over that session's own <see cref="Ordering.OrderState"/>,
/// <see cref="MenuCatalog"/>, and (optional) <see cref="PromptLoader"/>. Does not implement
/// "search" -- see <c>SearchTool</c>/whatever composes both into one <see cref="IToolExecutor"/>
/// for a session's full tool set.
/// </summary>
public sealed class OrderToolExecutor : IToolExecutor
{
    private readonly Ordering.OrderState _order;
    private readonly MenuCatalog _menu;
    private readonly PromptLoader? _promptLoader;
    private readonly int _maxItemQuantity;
    private readonly int _maxOrderItems;
    // Issue 165: this session's own bound daypart ("breakfast"/"lunch"), resolved once at
    // connect time (Program.cs's /realtime handler) -- null for a persona with no
    // features.dayparts. See CheckAddOrModifyGates's item_out_of_mode gate, the only reader.
    private readonly string? _menuMode;

    public OrderToolExecutor(
        Ordering.OrderState order,
        MenuCatalog menu,
        PromptLoader? promptLoader,
        int maxItemQuantity,
        int maxOrderItems,
        string? menuMode = null)
    {
        _order = order;
        _menu = menu;
        _promptLoader = promptLoader;
        _maxItemQuantity = maxItemQuantity;
        _maxOrderItems = maxOrderItems;
        _menuMode = menuMode;
    }

    public IReadOnlyList<string> ToolNames { get; } = ["update_order", "get_order", "reset_order"];

    /// <summary>Issue #14, Rick's PR #149 R4 review: the current order summary, serialized the
    /// same way <c>get_order</c>/<c>reset_order</c> already do (<see cref="OrderSummaryJson"/>),
    /// so <see cref="Sessions.RealtimeProcessor"/> can push a fresh ticket to the browser after a
    /// genuine tool exception -- mirrors app/backend/rtmt.py's post-exception
    /// <c>order_state_singleton.get_order_summary_json(session_id)</c> best-effort read.</summary>
    public string CurrentOrderSummaryJson => OrderSummaryJson.Serialize(_order.Summary);

    public Task<ToolResult> ExecuteAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(toolName switch
        {
            "update_order" => UpdateOrder(arguments),
            "get_order" => GetOrder(),
            "reset_order" => ResetOrder(),
            _ => throw new KeyNotFoundException(
                $"'{toolName}' is not a tool this executor can dispatch. Known tools: {string.Join(", ", ToolNames)}."),
        });

    private ToolResult UpdateOrder(JsonElement args)
    {
        // #36: validate required args up front instead of a raw missing-property exception deep
        // inside this handler tearing down the whole realtime connection.
        string[] required = ["action", "item_name", "size", "quantity"];
        var missing = required.Where(key => !args.TryGetProperty(key, out _)).ToList();
        if (missing.Count > 0)
        {
            var err = _promptLoader?.RenderError("tool_execution_failed")
                ?? "I'm sorry, something went wrong with that. Could you try again?";
            return new ToolResult(err, ToolResultDirection.ToServer);
        }

        var action = args.GetProperty("action").GetString() ?? "";
        var itemName = args.GetProperty("item_name").GetString() ?? "";
        var size = args.GetProperty("size").GetString() ?? "";
        var quantity = args.GetProperty("quantity").GetInt32();
        var callerPrice = TryGetPrice(args);

        if (action is "add" or "modify")
        {
            var rejection = CheckAddOrModifyGates(action, itemName, ref size);
            if (rejection is not null)
            {
                return rejection;
            }
        }

        if (action == "add")
        {
            var machineRejection = CheckMachineAvailability(itemName);
            if (machineRejection is not null)
            {
                return machineRejection;
            }
        }

        // ── Customization validation (reject nonsensical mods) ──
        if (itemName.Contains('('))
        {
            var open = itemName.IndexOf('(');
            var close = itemName.IndexOf(')');
            if (close > open)
            {
                var modsContent = itemName[(open + 1)..close];
                var modsError = ValidateCustomization(itemName, modsContent);
                if (modsError is not null)
                {
                    return new ToolResult(modsError, ToolResultDirection.ToServer);
                }
            }
        }

        if (action == "add" && _menu.IsExtraItem(itemName))
        {
            var extrasRejection = CheckExtrasGate(itemName);
            if (extrasRejection is not null)
            {
                return extrasRejection;
            }
        }

        if (action == "add")
        {
            var limitRejection = CheckQuantityLimits(itemName, size, quantity);
            if (limitRejection is not null)
            {
                return limitRejection;
            }
        }

        var resultInfo = _order.HandleOrderUpdate(action, itemName, size, quantity, callerPrice);

        // PR #184 round 4 (Rick's review, item 3): a "wholeBundleSize" pack's
        // ComboComponentResizeRejected flag means NOTHING was mutated (OrderState.
        // FillBundleComponent rejected the resize outright because this pack has no whole-meal
        // price at the requested size) -- read it BEFORE BuildDeltaText's success-delta branches
        // so this returns a structured (ToServer) rejection, same shape as not_on_menu/
        // size_not_available, instead of a generic "Changed ..., your total is now ..." delta
        // text, which used to tell the guest a change happened when the order was left untouched.
        if (resultInfo.ComboComponentResizeRejected is { Length: > 0 })
        {
            var bundleName = resultInfo.ComboComponentResizeRejectedBundle ?? "";
            var bundleSize = resultInfo.ComboComponentResizeRejectedBundleSize ?? "";
            var bundleSizeLabel = bundleSize.Length > 0 && !string.Equals(bundleSize, "standard", StringComparison.OrdinalIgnoreCase)
                ? Capitalize(bundleSize) : "";
            var rejectMessage = _promptLoader?.RenderError(
                "combo_component_resize_rejected",
                Vars(("item_name", itemName), ("bundle_name", bundleName), ("bundle_size_label", bundleSizeLabel)))
                ?? $"I'm sorry, {itemName} comes with the {bundleName} at its own size, so it can't be " +
                   "resized by itself. Would you like to make the whole meal that size instead?";
            return new ToolResult(
                new Dictionary<string, object?>
                {
                    ["status"] = "rejected",
                    ["item_added"] = false,
                    ["reason"] = "combo_component_resize_rejected",
                    ["item_name"] = itemName,
                    ["message"] = rejectMessage,
                },
                ToolResultDirection.ToServer);
        }

        var summary = _order.Summary;
        var jsonOrderSummary = OrderSummaryJson.Serialize(summary);

        var deltaText = BuildDeltaText(action, itemName, size, quantity, resultInfo, summary);

        // ── Combo validation: flag missing components ──
        var validation = _order.GetComboRequirements();
        if (!validation.IsComplete)
        {
            deltaText += $"\n\n[SYSTEM HINT: {validation.PromptHint}]";
        }
        else if (action == "add" && !resultInfo.AbsorbedIntoCombo)
        {
            // #168 follow-up (Rick's PR #217 review): an "extra" (Flavor Add-In, Add Bacon,
            // Whipped Topping, Sweet Cream, Jalapeños, etc.) shares its base item's own category
            // (e.g. this persona's own "Extras & Sides", alongside genuine stand-alone sides like
            // Cheese Tots) but is its own order line, not a side the guest is choosing instead of/along
            // with a combo -- a category-specific hint written for that category's REAL items
            // (e.g. "add a refreshing Drink or Slush to complete their meal!") reads as non-
            // sequitur stacked right after the item it modifies. Pass "" instead of the real
            // category so GetUpsellHint/FallbackUpsellHint always miss every bucket and fall
            // through to generic, matching exactly what an extra got before hints.yaml's
            // "extras & sides" mapping was added in #168 (back then "extras & sides" didn't
            // match ANY bucket either).
            var category = _menu.IsExtraItem(itemName) ? "" : _menu.InferCategory(itemName);
            // #313 (Rick's review, 1.2): hints.yaml's upsell copy is free-form prose that can
            // itself name a brand item (e.g. "upgrade to a Large or add a Widget!") -- it must go
            // through the same pronunciation lexicon as everything else the realtime model speaks.
            var hintText = _promptLoader is not null ? _promptLoader.GetUpsellHint(category) : FallbackUpsellHint(category);
            deltaText += _menu.Spoken(hintText);
        }

        // #313 (Rick's review, 1.4): the realtime model never sees the client-only JSON payload
        // (`jsonOrderSummary`, ToolResultDirection.ToBoth's client channel) -- only the text
        // channel below. Appending the already-cached `Summary.SpokenReadBack` here is what makes
        // the mandatory read-back actually mandatory on realtime: the model has the full order
        // read-back available verbatim in its own tool result, not just an instruction in the
        // system prompt to "read back the order" from nothing.
        // #113: this session's own bound persona's happy-hour banner -- never hardcoded here.
        var happyHourNote = _order.HappyHourBanner;
        return new ToolResult(
            deltaText + happyHourNote + "\n\n" + summary.SpokenReadBack, ToolResultDirection.ToBoth, clientText: jsonOrderSummary);
    }

    private ToolResult GetOrder()
    {
        var readback = _order.GetGroupedOrderForReadback();
        var jsonSummary = OrderSummaryJson.Serialize(_order.Summary);
        var happyHourNote = _order.HappyHourBanner;
        return new ToolResult(readback + happyHourNote, ToolResultDirection.ToBoth, clientText: jsonSummary);
    }

    private ToolResult ResetOrder()
    {
        _order.ResetOrder();
        var jsonSummary = OrderSummaryJson.Serialize(_order.Summary);
        return new ToolResult($"Order cleared. {jsonSummary}", ToolResultDirection.ToBoth, clientText: jsonSummary);
    }

    // ── Gate helpers (each mirrors one tools.py rejection block, in the same order) ──────────

    /// <summary>#73 (ADR-001 decision 4, "No off-menu"): the on-menu gate, first thing in the
    /// add/modify path. Also covers item_out_of_mode (issue 165, add-only), size_not_available
    /// and (modify-only) not_in_order -- same TO_SERVER structured-JSON shape for all four
    /// (Rick's PR #100 review, required item 1).</summary>
    private ToolResult? CheckAddOrModifyGates(string action, string itemName, ref string size)
    {
        var menuItem = _menu.ResolveMenuItem(itemName);
        if (menuItem is null)
        {
            var message = _promptLoader?.RenderError("item_not_on_menu", Vars(("item_name", itemName)))
                ?? $"I'm sorry, {itemName} isn't on our menu. Would you like to try something else instead? " +
                   "Use the search tool with the guest's words and offer the closest real menu item by its exact name.";
            var rejection = new Dictionary<string, object?>
            {
                ["status"] = "rejected",
                ["item_added"] = false,
                ["reason"] = "not_on_menu",
                ["item_name"] = itemName,
                ["message"] = message,
            };
            // #77 (shared extras engine, extras.splitCombinedNames): offer two real `add` calls
            // instead of a flat dead end when the name is really a known base + a known extra.
            var split = _menu.TrySplitCombinedName(itemName);
            if (split is { } s)
            {
                rejection["suggested_calls"] = new object[]
                {
                    new Dictionary<string, object?> { ["action"] = "add", ["item_name"] = s.BaseName },
                    new Dictionary<string, object?> { ["action"] = "add", ["item_name"] = s.ExtraName },
                };
            }
            return new ToolResult(rejection, ToolResultDirection.ToServer);
        }

        // Issue 165: this session's own bound menu-mode gate -- an item that's real and on the
        // menu, but not offered in the active daypart (e.g. a breakfast-only item add while the
        // session is bound to "lunch"). Runs for "add" only: a "modify" target is already IN the
        // order, which means it passed this same gate at add time and the mode never changes
        // mid-session (no mid-conversation ?mode= switching, exactly like persona/model), so
        // gating it again here would be a no-op at best and a spurious reject at worst. A
        // persona with no features.dayparts always resolves menuMode to null, and
        // MenuCatalog.ItemAvailableNow is a no-op (returns true) for a null active mode -- so
        // this block never rejects anything for those packs.
        if (action == "add" && !_menu.ItemAvailableNow(itemName, _menuMode))
        {
            var message = _promptLoader?.RenderError(
                "item_out_of_mode", Vars(("item_name", menuItem.Name), ("mode_label", menuItem.MenuPeriod ?? "")))
                ?? $"I'm sorry, {menuItem.Name} isn't on our menu right now. Would you like to try something else instead?";
            return new ToolResult(
                new Dictionary<string, object?>
                {
                    ["status"] = "rejected",
                    ["item_added"] = false,
                    ["reason"] = "item_out_of_mode",
                    ["item_name"] = menuItem.Name,
                    ["message"] = message,
                },
                ToolResultDirection.ToServer);
        }

        var requestedSize = _menu.CanonicalSizeKey(size);
        if (!menuItem.Sizes.Contains(requestedSize) && (requestedSize is "" or "standard"))
        {
            // PR #184 round 4 (Rick's review, item 4): a bundle meal ordered with no size at all,
            // or a bare "standard"/"regular" ask, on a pack whose own Sizes list is S/M/L ONLY
            // (no "standard" tier -- e.g. a numbered-meal pack with S/M/L sizing) isn't actually
            // an invalid size; it's the guest not naming one. Map it onto the bundle's own
            // configured bundle.defaultSize (the original app's _get_default_side default)
            // instead of rejecting a perfectly normal numbered-meal order. An item with NO
            // bundle data, or no configured default size, falls through unchanged to the
            // rejection below exactly as before; so does any OTHER explicitly-named size that
            // the pack doesn't price (e.g. an explicit "large" on a true Standard-only item).
            var defaultSize = _menu.BundleDefaultSize(itemName);
            var defaultKey = string.IsNullOrEmpty(defaultSize) ? "" : _menu.CanonicalSizeKey(defaultSize);
            if (!string.IsNullOrEmpty(defaultKey) && menuItem.Sizes.Contains(defaultKey))
            {
                requestedSize = defaultKey;
                size = defaultSize;
            }
        }
        if (!menuItem.Sizes.Contains(requestedSize))
        {
            var sizeMap = _menu.SizeMap;
            var availableSizes = menuItem.Sizes.Select(s => sizeMap.GetValueOrDefault(s, Capitalize(s))).ToList();
            var message = _promptLoader?.RenderError(
                "size_not_available",
                Vars(("item_name", menuItem.Name), ("available_sizes", string.Join(", ", availableSizes))))
                ?? $"I'm sorry, {menuItem.Name} isn't available in that size. " +
                   $"We have it in {string.Join(", ", availableSizes)} -- would you like one of those?";
            return new ToolResult(
                new Dictionary<string, object?>
                {
                    ["status"] = "rejected",
                    ["item_added"] = false,
                    ["reason"] = "size_not_available",
                    ["item_name"] = menuItem.Name,
                    ["message"] = message,
                    ["available_sizes"] = availableSizes,
                },
                ToolResultDirection.ToServer);
        }

        // #77: `modify` resizes an existing line -- an on-menu item that isn't in the order has
        // nothing to resize. Same line-matching rule as OrderState.HandleOrderUpdate's modify
        // branch (any-size match on item name).
        // #179: a combo's side/drink filling a slot via absorption is ALSO a real, resizable part
        // of the order even though it has no raw OrderItem line of its own -- the guest saying
        // "make that a large" about the drink that came with their combo. IsAbsorbedComponent is
        // the second chance before this rejects it.
        if (action == "modify" && !_order.Items.Any(item => item.Item == itemName) && !_order.IsAbsorbedComponent(itemName))
        {
            var message = _promptLoader?.RenderError("item_not_in_order", Vars(("item_name", menuItem.Name)))
                ?? $"{menuItem.Name} isn't in the order, so nothing was changed. " +
                   "Ask the guest whether they'd like to add it.";
            return new ToolResult(
                new Dictionary<string, object?>
                {
                    ["status"] = "rejected",
                    ["item_added"] = false,
                    ["reason"] = "not_in_order",
                    ["item_name"] = menuItem.Name,
                    ["message"] = message,
                },
                ToolResultDirection.ToServer);
        }

        return null;
    }

    /// <summary>#77: add-time <c>machine_unavailable</c> -- an on-menu item that requires a
    /// machine this persona's OWN <c>machines.&lt;key&gt;.status</c> currently reports "down".
    /// <c>modify</c> never reaches this check.</summary>
    private ToolResult? CheckMachineAvailability(string itemName)
    {
        var machine = _menu.RequiresMachine(itemName);
        if (string.IsNullOrEmpty(machine) || _menu.MachineStatus(machine) != "down")
        {
            return null;
        }
        var machineLabel = _menu.MachineLabel(machine);
        var message = _promptLoader?.RenderError(
            "machine_unavailable", Vars(("item_name", itemName), ("machine_label", machineLabel)))
            ?? $"I'm sorry, {itemName} isn't available right now -- {machineLabel}. " +
               "Would you like to try something else instead?";
        return new ToolResult(
            new Dictionary<string, object?>
            {
                ["status"] = "rejected",
                ["item_added"] = false,
                ["reason"] = "machine_unavailable",
                ["item_name"] = itemName,
                ["message"] = message,
            },
            ToolResultDirection.ToServer);
    }

    /// <summary>#77 (shared extras engine): an <c>isExtra</c> item add-on needs a real, currently-
    /// present, non-blocked base item already in the order.</summary>
    private ToolResult? CheckExtrasGate(string itemName)
    {
        var hasAllowedBase = false;
        var hasBlockedBase = false;
        foreach (var orderItem in _order.Items)
        {
            var category = _menu.InferCategory(orderItem.Item);
            if (_menu.AllowedExtraCategories.Contains(category))
            {
                hasAllowedBase = true;
            }
            if (_menu.BlockedExtraCategories.Contains(category))
            {
                hasBlockedBase = true;
            }
        }

        if (hasAllowedBase)
        {
            return null;
        }

        string reason;
        string message;
        if (hasBlockedBase)
        {
            reason = "extras_blocked_category";
            message = _promptLoader?.RenderError("extras_blocked_category")
                ?? "I can add extras to drinks, slushes, shakes, or combos, " +
                   "but I can't add them to sides or hot dogs on their own.";
        }
        else
        {
            reason = "extras_no_base_item";
            message = _promptLoader?.RenderError("extras_no_base_item")
                ?? "I can add extras to drinks, slushes, shakes, or combos, " +
                   "but not to sides or hot dogs on their own.";
        }
        return new ToolResult(
            new Dictionary<string, object?>
            {
                ["status"] = "rejected",
                ["item_added"] = false,
                ["reason"] = reason,
                ["item_name"] = itemName,
                ["message"] = message,
            },
            ToolResultDirection.ToServer);
    }

    /// <summary>Per-item and total-order quantity limits (add only) -- config.yaml's
    /// <c>business_rules.max_item_quantity</c>/<c>max_order_items</c>, a deployment-wide dial
    /// shared by every persona (unlike tax/happy-hour, which are per-persona; see
    /// <c>Configuration.BusinessRulesConfig</c>'s own doc comment).</summary>
    private ToolResult? CheckQuantityLimits(string itemName, string size, int quantity)
    {
        var existingQty = _order.Items.FirstOrDefault(oi => oi.Item == itemName && oi.Size == size)?.Quantity ?? 0;
        var newItemQty = existingQty + quantity;
        if (newItemQty > _maxItemQuantity)
        {
            var allowed = _maxItemQuantity - existingQty;
            string msg;
            if (allowed <= 0)
            {
                msg = _promptLoader?.RenderError("per_item_limit_maxed", Vars(
                    ("item_name", itemName), ("max_per_item", _maxItemQuantity), ("existing_qty", existingQty)))
                    ?? $"That's a lot of {itemName}! Our drive-thru can handle up to " +
                       $"{_maxItemQuantity} of any item. You already have {existingQty} — " +
                       $"would you like to keep it at {existingQty}?";
            }
            else
            {
                msg = _promptLoader?.RenderError("per_item_limit_partial", Vars(
                    ("item_name", itemName), ("max_per_item", _maxItemQuantity), ("allowed", allowed)))
                    ?? $"That's a lot of {itemName}! Our drive-thru can handle up to " +
                       $"{_maxItemQuantity} of any item. I can add {allowed} more — " +
                       $"would you like me to do that?";
            }
            return new ToolResult(msg, ToolResultDirection.ToServer);
        }

        var totalQty = _order.Items.Sum(oi => oi.Quantity) + quantity;
        if (totalQty > _maxOrderItems)
        {
            var remaining = _maxOrderItems - _order.Items.Sum(oi => oi.Quantity);
            string msg;
            if (remaining <= 0)
            {
                msg = _promptLoader?.RenderError("total_order_limit_maxed", Vars(("max_total", _maxOrderItems)))
                    ?? $"Wow, that's a big order! Our drive-thru tops out at " +
                       $"{_maxOrderItems} items total so we can keep things moving. " +
                       "You're already at the max — would you like to swap anything out?";
            }
            else
            {
                msg = _promptLoader?.RenderError("total_order_limit_partial", Vars(
                    ("max_total", _maxOrderItems), ("remaining", remaining)))
                    ?? $"Wow, that's a big order! Our drive-thru tops out at " +
                       $"{_maxOrderItems} items total so we can keep things moving. " +
                       $"I can add {remaining} more — would you like me to do that?";
            }
            return new ToolResult(msg, ToolResultDirection.ToServer);
        }

        return null;
    }

    /// <summary>Port of tools.py's <c>validate_customization</c>: rejects a modifier this
    /// category's own <c>extras.invalidModifiers</c> forbids (e.g. "extra cheese" on a plain
    /// side).</summary>
    private string? ValidateCustomization(string itemName, string modsContent)
    {
        var baseName = MenuKeyValidator.StripModifiers(itemName);
        var category = _menu.InferCategory(baseName);
        var modsLower = modsContent.ToLowerInvariant();
        foreach (var (catKey, forbiddenList) in _menu.InvalidModifiers)
        {
            if (!category.ToLowerInvariant().Contains(catKey))
            {
                continue;
            }
            foreach (var forbidden in forbiddenList)
            {
                if (modsLower.Contains(forbidden))
                {
                    return _promptLoader?.RenderError(
                        "invalid_mod", Vars(("forbidden_item", forbidden), ("base_name", baseName)))
                        ?? $"I can't add {forbidden} to a {baseName} — that's a new one! Want to try a different topping?";
                }
            }
        }
        return null;
    }

    private string BuildDeltaText(
        string action, string itemName, string size, int quantity, Ordering.OrderUpdateResult resultInfo,
        Ordering.OrderSummary summary)
    {
        var displaySize = string.IsNullOrEmpty(size) ||
            (size.ToLowerInvariant() is "" or "standard" or "n/a" or "na" or "none" or "n.a.")
            ? ""
            : size;
        var displayName = $"{(displaySize.Length > 0 ? Capitalize(displaySize) + " " : "")}{itemName}";
        var spokenItemName = _menu.Spoken(itemName);
        var spokenDisplayName = _menu.Spoken(displayName);

        if (resultInfo.AbsorbedIntoCombo)
        {
            return resultInfo.ComboComponentUpchargeDisplay is { Length: > 0 } upcharge
                ? $"{spokenDisplayName} included with your combo with a {upcharge} upcharge — your total is {summary.FinalTotalSpoken}"
                : $"{spokenDisplayName} included with your combo — your total is {summary.FinalTotalSpoken}";
        }
        if (resultInfo.ComboConvertedFrom is not null && action == "add")
        {
            var comboDisplay = spokenDisplayName;
            var mods = resultInfo.ModsCarried ?? "";
            if (mods.Length > 0)
            {
                comboDisplay = $"{spokenDisplayName} {mods}";
            }
            return $"Upgraded to {comboDisplay} — your total is now {summary.FinalTotalSpoken}";
        }
        // #179: set by OrderState.HandleOrderUpdate whenever a combo's side/drink slot was
        // (re)sized in place -- via an `add` of the same item at a different size while the slot
        // was already full, or an explicit `modify` targeting an absorbed component. Checked
        // before the generic promptLoader/action-keyed branches below so both paths confirm the
        // resize, never a duplicate-add or rejected-modify message. (HandleRemove also sets
        // VacatedComboComponent when itemName was vacating a combo slot rather than removing a
        // raw order line -- the existing generic "Removed ..." wording below is already accurate
        // for that case, so no separate branch reads it here.)
        if (resultInfo.ResizedComboComponent is not null)
        {
            return resultInfo.ComboComponentUpchargeDisplay is { Length: > 0 } upcharge
                ? $"Changed {spokenDisplayName} with a {upcharge} upcharge, your total is now {summary.FinalTotalSpoken}"
                : $"Changed {spokenDisplayName}, your total is now {summary.FinalTotalSpoken}";
        }
        // #313 (Rick's review, 1.7): "Upgraded" implies the new size is always bigger, but a
        // guest can resize down too (Brian's exact bug report: "25 to 10 count" shrank, yet the
        // realtime model said "I've upgraded you"). "Changed" is accurate either direction.
        if (resultInfo.ModifiedFromSize is { Length: > 0 } fromSize
            && resultInfo.ModifiedToSize is { Length: > 0 } toSize
            && fromSize != toSize)
        {
            return $"Changed {spokenItemName} from {Capitalize(fromSize)} to {Capitalize(toSize)}, your total is now {summary.FinalTotalSpoken}";
        }
        if (_promptLoader is { } pl)
        {
            var tpl = pl.GetDeltaTemplate(action);
            return pl.RenderTemplate(tpl, Vars(
                ("quantity", Ordering.Money.NumberToWords(quantity)), ("display_name", spokenDisplayName), ("total", summary.FinalTotalSpoken)));
        }
        return action switch
        {
            "add" => $"Added {Ordering.Money.NumberToWords(quantity)} {spokenDisplayName} — your total is now {summary.FinalTotalSpoken}",
            "modify" => $"Changed {spokenDisplayName} — your total is now {summary.FinalTotalSpoken}",
            _ => $"Removed {Ordering.Money.NumberToWords(quantity)} {spokenDisplayName} — your total is now {summary.FinalTotalSpoken}",
        };
    }

    private static string FallbackUpsellHint(string category) => category switch
    {
        "combos" => " (UPSELL HINT: Combos are a great base! Ask if they want to upgrade to a Large size, or add a delicious Shake or Dessert!)",
        "burgers" or "burgers & sandwiches" => " (UPSELL HINT: Perfect choice! Ask if they want to make it a combo meal with Tots or Fries and a refreshing Drink!)",
        "drinks" or "slushes" => " (UPSELL HINT: Great drink choice! Ask if they want to add a Flavor Add-In to customize it, or pair it with a tasty side!)",
        "shakes" or "desserts" or "shakes & ice cream" => " (UPSELL HINT: Yum! Shakes are perfect on their own, but ask if they'd like to add Whipped Cream or pair with a snack!)",
        "sides" or "hot dogs" or "hot dogs & tots" => " (UPSELL HINT: Tasty! Ask if they want to add a refreshing Drink or Slush to complete their meal!)",
        _ => " (UPSELL HINT: Ask if they'd like to add anything else — maybe a drink, side, or dessert!)",
    };

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>#104: the tool call's own "price" argument is decoded but never trusted --
    /// <see cref="Ordering.OrderState.HandleOrderUpdate"/> always re-prices from the menu. This
    /// only surfaces the raw value through for OrderState's own debug-log comparison / last-resort
    /// fallback when the menu has no price on file at all. A JSON boolean (`true`/`false`) is
    /// deliberately treated as "not a price" here -- JsonValueKind already distinguishes
    /// True/False from Number, so (unlike Python's `isinstance(True, int)` quirk) no special-
    /// casing is needed to exclude it.</summary>
    private static decimal? TryGetPrice(JsonElement args) =>
        args.TryGetProperty("price", out var priceEl) && priceEl.ValueKind == JsonValueKind.Number && priceEl.TryGetDecimal(out var price)
            ? price
            : null;

    private static IReadOnlyDictionary<string, object?> Vars(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);
}
