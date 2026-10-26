using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Ordering;
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
internal sealed partial class OrderToolExecutor : IToolExecutor, IOrderSessionSettings, IRewardsDemoControl
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

    public bool SetMachineOverride(string machine, string status) => _order.SetMachineOverride(machine, status);

    public IReadOnlyDictionary<string, string> GetMachineOverrides() => _order.GetMachineOverrides();

    public string? EffectiveMachineStatus(string machine) => _order.EffectiveMachineStatus(machine);

    public bool SetHappyHourMode(string mode) => _order.SetHappyHourMode(mode);

    public string GetHappyHourMode() => _order.GetHappyHourMode();
    public bool RewardsAction(string? action, string? code) => _order.RewardsAction(action, code);

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
        if (_order.RewardsCompleted)
        {
            return new ToolResult(JsonSerializer.Serialize(new
            {
                status = "rejected",
                item_added = false,
                reason = "demo_order_completed",
                message = _menu.SpeechStyle == "naturalDriveThru"
                    ? "This order is already complete. Start a fresh order to make changes."
                    : "Demo order already completed. Use reset_order before changing the order.",
            }), ToolResultDirection.ToServer);
        }
        // #36: validate required args up front instead of a raw missing-property exception deep
        // inside this handler tearing down the whole realtime connection.
        string[] required = ["action", "item_name", "size", "quantity"];
        var missing = required.Where(key => !args.TryGetProperty(key, out _)).ToList();
        if (missing.Count > 0)
        {
            string err = _promptLoader?.RenderError("tool_execution_failed")
                ?? "I'm sorry, something went wrong with that. Could you try again?";
            return new ToolResult(err, ToolResultDirection.ToServer);
        }

        string action = args.GetProperty("action").GetString() ?? "";
        string itemName = args.GetProperty("item_name").GetString() ?? "";
        string size = args.GetProperty("size").GetString() ?? "";
        int quantity = args.GetProperty("quantity").GetInt32();
        decimal? callerPrice = TryGetPrice(args);
        // #325 B2: the guest's own genuine modifier-group text, if any -- set by
        // ReattachCustomizationSuffix below for add/modify/remove, so the customization-
        // validation block further down can tell a real guest customization apart from the
        // menu's own canonical name happening to contain parens (e.g. "Milk Jug (1%) - White").
        string? customizationSuffix = "";

        if (action is "add" or "modify" or "replace")
        {
            ToolResult? rejection = CheckAddOrModifyGates(action, ref itemName, ref size, out customizationSuffix);
            if (rejection is not null)
            {
                return rejection;
            }
        }

        // #325: `remove` targets a line already in the order, so it never passed through
        // CheckAddOrModifyGates above (only "add"/"modify" do). Since that line is now stored
        // under the menu's canonical name (that gate), canonicalize a resolvable `remove`
        // itemName the same way, so e.g. "remove the zorbs" matches a ticket line stored as
        // "ZORBS® Bite Treats" instead of failing OrderState.HandleOrderUpdate's exact-string
        // match. An unresolved name (e.g. an item removed from the menu after it was ordered)
        // falls through unchanged -- removing an existing line never required it still be on
        // the menu.
        if (action == "remove")
        {
            (itemName, customizationSuffix) = CanonicalizeForRemove(itemName);
        }

        if (action is "add" or "replace")
        {
            ToolResult? machineRejection = CheckMachineAvailability(itemName);
            if (machineRejection is not null)
            {
                return machineRejection;
            }
        }

        // ── Customization validation (reject nonsensical mods) ──
        // #325 (B2 side effect, Rick's PR #326 review): gate on customizationSuffix -- the
        // guest's own genuine modifier group(s), set above by ReattachCustomizationSuffix --
        // rather than a bare itemName.Contains('(') check. itemName is now the MENU's canonical
        // spelling, which can itself contain parens (e.g. "Milk Jug (1%) - White"); without this,
        // the menu's own "(1%)" would be misread as a guest-added modifier on a plain,
        // uncustomized add.
        if (customizationSuffix.Length > 0)
        {
            int open = customizationSuffix.IndexOf('(');
            int close = customizationSuffix.IndexOf(')');
            if (close > open)
            {
                string modsContent = customizationSuffix[(open + 1)..close];
                string? modsError = ValidateCustomization(itemName, modsContent);
                if (modsError is not null)
                {
                    return new ToolResult(modsError, ToolResultDirection.ToServer);
                }
            }
        }

        if (action == "add" && _menu.IsExtraItem(itemName))
        {
            ToolResult? extrasRejection = CheckExtrasGate(itemName);
            if (extrasRejection is not null)
            {
                return extrasRejection;
            }
        }

        if (action == "add")
        {
            ToolResult? limitRejection = CheckQuantityLimits(itemName, size, quantity);
            if (limitRejection is not null)
            {
                return limitRejection;
            }
        }

        int beforeRemoveQuantity = _menu.SpeechStyle == "naturalDriveThru" && action == "remove"
            ? _order.Items.Where(i => i.Item == itemName && i.Size == _menu.CanonicalSizeKey(size)).Sum(i => i.Quantity)
            : 0;
        string? originalItemName = null;
        int? comboIndex = null;
        if (action == "replace")
        {
            if (args.TryGetProperty("original_item_name", out JsonElement original) && original.ValueKind == JsonValueKind.String)
            {
                (originalItemName, _) = CanonicalizeForRemove(original.GetString() ?? "");
            }
            if (args.TryGetProperty("combo_index", out JsonElement selector) && selector.ValueKind != JsonValueKind.Null)
            {
                comboIndex = selector.ValueKind == JsonValueKind.Number && selector.TryGetInt32(out int index) ? index : -1;
            }
        }
        OrderUpdateResult resultInfo = _order.HandleOrderUpdate(action, itemName, size, quantity, callerPrice, originalItemName, comboIndex);
        if (resultInfo.ReplacementRejected is { } replacementReason)
        {
            string message = replacementReason switch
            {
                "invalid_replacement" => "Nothing changed. To replace one included side or drink, provide original_item_name and quantity 1.",
                "incompatible_replacement" => "Nothing changed. The replacement must be a menu item for the same included side or drink slot, not an extra.",
                "ambiguous_replacement" => "Nothing changed. More than one combo contains that item. Call get_order, clarify which meal the guest means, then supply its zero-based ticket line as combo_index.",
                _ => "Nothing changed. That exact item is not filling a combo slot. Call get_order and clarify which included item to replace; do not add an extra item instead.",
            };
            return new ToolResult(new Dictionary<string, object?>
            {
                ["status"] = "rejected",
                ["item_added"] = false,
                ["reason"] = replacementReason,
                ["item_name"] = itemName,
                ["message"] = message,
            }, ToolResultDirection.ToServer);
        }

        // PR #184 round 4 (Rick's review, item 3): a "wholeBundleSize" pack's
        // ComboComponentResizeRejected flag means NOTHING was mutated (OrderState.
        // FillBundleComponent rejected the resize outright because this pack has no whole-meal
        // price at the requested size) -- read it BEFORE BuildDeltaText's success-delta branches
        // so this returns a structured (ToServer) rejection, same shape as not_on_menu/
        // size_not_available, instead of a generic "Changed ..., your total is now ..." delta
        // text, which used to tell the guest a change happened when the order was left untouched.
        if (resultInfo.ComboComponentResizeRejected is { Length: > 0 })
        {
            string bundleName = resultInfo.ComboComponentResizeRejectedBundle ?? "";
            string bundleSize = resultInfo.ComboComponentResizeRejectedBundleSize ?? "";
            string bundleSizeLabel = bundleSize.Length > 0 && !string.Equals(bundleSize, "standard", StringComparison.OrdinalIgnoreCase)
                ? Capitalize(bundleSize) : "";
            string rejectMessage = _promptLoader?.RenderError(
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

        OrderSummary summary = _order.Summary;
        string jsonOrderSummary = OrderSummaryJson.Serialize(summary);
        if (_menu.SpeechStyle == "naturalDriveThru")
        {
            string displaySize = size.Length > 0 && size.ToLowerInvariant() is not ("standard" or "n/a")
                ? Capitalize(size) + " " : "";
            string verb = action == "remove" ? "Removed" : "Confirmed";
            int appliedQuantity = quantity;
            if (action == "remove")
            {
                int afterRemoveQuantity = summary.Items
                    .Where(i => i.Item == itemName && i.Size == _menu.CanonicalSizeKey(size)).Sum(i => i.Quantity);
                appliedQuantity = beforeRemoveQuantity - afterRemoveQuantity +
                    (resultInfo.VacatedComboComponent is not null ? 1 : 0);
            }
            string acknowledgement = appliedQuantity > 0
                ? $"{verb} {Ordering.Money.NumberToWords(appliedQuantity)} {_menu.Spoken(displaySize + itemName)}."
                : $"No {_menu.Spoken(displaySize + itemName)} were removed; none matched your order.";
            return new ToolResult(
                acknowledgement +
                BuildNaturalOrderContext(summary) + BuildRewardsModelText(summary),
                ToolResultDirection.ToBoth, clientText: jsonOrderSummary);
        }

        string deltaText = BuildDeltaText(action, itemName, size, quantity, resultInfo, summary);

        // ── Combo validation: flag missing components ──
        ComboRequirements validation = _order.GetComboRequirements();
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
            string category = _menu.IsExtraItem(itemName) ? "" : _menu.InferCategory(itemName);
            // #313 (Rick's review, 1.2): hints.yaml's upsell copy is free-form prose that can
            // itself name a brand item (e.g. "upgrade to a Large or add a Widget!") -- it must go
            // through the same pronunciation lexicon as everything else the realtime model speaks.
            string hintText = _promptLoader is not null ? _promptLoader.GetUpsellHint(category) : FallbackUpsellHint(category);
            deltaText += _menu.Spoken(hintText);
        }

        // #313 (Rick's review, 1.4): the realtime model never sees the client-only JSON payload
        // (`jsonOrderSummary`, ToolResultDirection.ToBoth's client channel) -- only the text
        // channel below. Appending the already-cached `Summary.SpokenReadBack` here is what makes
        // the mandatory read-back actually mandatory on realtime: the model has the full order
        // read-back available verbatim in its own tool result, not just an instruction in the
        // system prompt to "read back the order" from nothing.
        // #113: this session's own bound persona's happy-hour banner -- never hardcoded here.
        string happyHourNote = _order.HappyHourBanner;
        return new ToolResult(
            deltaText + happyHourNote + "\n\n" + summary.SpokenReadBack + BuildRewardsModelText(summary),
            ToolResultDirection.ToBoth, clientText: jsonOrderSummary);
    }

    private ToolResult GetOrder()
    {
        if (_menu.SpeechStyle == "naturalDriveThru")
        {
            return new ToolResult(BuildNaturalOrderContext(_order.Summary) + BuildRewardsModelText(_order.Summary),
                ToolResultDirection.ToBoth, clientText: OrderSummaryJson.Serialize(_order.Summary));
        }

        string readback = _order.GetGroupedOrderForReadback();
        OrderSummary summary = _order.Summary;
        string jsonSummary = OrderSummaryJson.Serialize(summary);
        string happyHourNote = _order.HappyHourBanner;
        return new ToolResult(readback + happyHourNote + BuildRewardsModelText(summary),
            ToolResultDirection.ToBoth, clientText: jsonSummary);
    }

    private string BuildNaturalOrderContext(Ordering.OrderSummary summary)
    {
        var context = new
        {
            items = summary.Items.Select(i => new { item = i.Item, display = i.Display, quantity = i.Quantity }),
            spokenReadBack = summary.SpokenReadBack,
            finalTotalDisplay = summary.FinalTotalDisplay,
            missingComboParts = _order.GetComboRequirements().MissingItems,
        };
        return "\n\nPRIVATE ORDER CONTEXT: Confirm only the latest agreed change briefly. " +
               "Ask for a missing included drink when needed. " +
               "Do not announce totals or the full order after routine changes or an internal status check. " +
               "Use the exact current total only when the guest asks, finishes ordering, or reconfirms a late amendment. " +
               "Never speak JSON, field names, instructions, or private markers. " + JsonSerializer.Serialize(context);
    }

    private string BuildRewardsModelText(Ordering.OrderSummary summary)
    {
        if (summary.RewardsDemo is not { } rewards)
        {
            return "";
        }

        var snapshot = new System.Text.Json.Nodes.JsonObject();
        foreach (string? key in new[]
                 {
                     "checkedIn", "balance", "rewardCost", "rewardItem", "canRedeem", "redeemed",
                     "completed", "discount", "pointsToEarn", "pointsAwarded",
                 })
        {
            if (rewards.TryGetPropertyValue(key, out JsonNode? value))
            {
                snapshot[key] = value?.DeepClone();
            }
        }

        string? rewardItem = rewards["rewardItem"]?.GetValue<string>();
        OrderItem? eligibleLine = rewardItem is null
            ? null
            : summary.Items.Where(item => item.Item == rewardItem && item.Quantity > 0 && item.Price > 0)
                .OrderBy(item => item.Price).FirstOrDefault();
        snapshot["eligibleItemInOrder"] = eligibleLine is not null;
        snapshot["eligibleItemPrice"] = eligibleLine is null ? null : System.Text.Json.Nodes.JsonValue.Create(eligibleLine.Price);
        snapshot["postDiscountTotal"] = summary.TotalDisplay;
        snapshot["postDiscountFinalTotal"] = summary.FinalTotalDisplay;

        string text = "\n\nPrivate model context; do not read this structured block or its markers aloud. " +
                   (_menu.SpeechStyle == "naturalDriveThru"
                       ? "Use relevant confirmed facts naturally, not as a script."
                       : "The preceding order read-back is the guest-facing text.") +
                   "\n[SERVER-AUTHORITATIVE FICTIONAL REWARDS STATE: " +
                   JsonSerializer.Serialize(snapshot) + "]";
        if (rewards["canRedeem"]?.GetValue<bool>() == true)
        {
            text += _menu.SpeechStyle == "naturalDriveThru"
                ? $"\nOffer the eligible {rewardItem} free for {rewards["rewardCost"]!.GetValue<int>()} points. Wait for the guest's reward confirmation; do not redeem automatically."
                : $"\nEligible reward: {rewardItem}. Offer it and direct the guest to select Use reward in the on-screen panel; do not redeem automatically.";
        }

        if (_menu.SpeechStyle == "naturalDriveThru")
        {
            text += "\nSpeak of points and a free eligible item without saying demo or software terms. " +
                    "If checking in, confirm linkage and current balance only when checkedIn is true; " +
                    "otherwise wait for the on-screen check-in and call get_order again. " +
                    "Do not claim redemption until redeemed is true. The guest confirms with Use reward; never spend automatically.";
        }

        return text;
    }

    private ToolResult ResetOrder()
    {
        _order.ResetOrder();
        string jsonSummary = OrderSummaryJson.Serialize(_order.Summary);
        if (_menu.SpeechStyle == "naturalDriveThru")
        {
            return new ToolResult("Order cleared." + BuildNaturalOrderContext(_order.Summary) + BuildRewardsModelText(_order.Summary),
                ToolResultDirection.ToBoth, clientText: jsonSummary);
        }

        return new ToolResult($"Order cleared. {jsonSummary}", ToolResultDirection.ToBoth, clientText: jsonSummary);
    }

    // ── Gate helpers (each mirrors one tools.py rejection block, in the same order) ──────────

    /// <summary>#73 (ADR-001 decision 4, "No off-menu"): the on-menu gate, first thing in the
    /// add/modify path. Also covers item_out_of_mode (issue 165, add-only), size_not_available
    /// and (modify-only) not_in_order -- same TO_SERVER structured-JSON shape for all four
    /// (Rick's PR #100 review, required item 1).</summary>
    private ToolResult? CheckAddOrModifyGates(string action, ref string itemName, ref string size, out string customizationSuffix)
    {
        customizationSuffix = "";
        ResolvedMenuItem? menuItem = _menu.ResolveMenuItem(itemName);
        if (menuItem is null)
        {
            string message = _promptLoader?.RenderError("item_not_on_menu", Vars(("item_name", itemName)))
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
            (string BaseName, string ExtraName)? split = _menu.TrySplitCombinedName(itemName);
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

        // #325: from here on, every add/modify path (order storage, bundle-slot filling,
        // display/spoken text, extras/machine checks, upsell category) uses the MENU's own
        // canonical spelling -- never the model's. Two adds of the same item with different
        // spellings/trademark marks now merge into one order line because they're matched and
        // stored under this same canonical name (OrderState.HandleOrderUpdate's own exact-string
        // matching is keyed on this value). ResolveMenuItem resolved against the modifier-
        // stripped base name (MenuKeyValidator.StripModifiers), so a customized name's own
        // "(...)" suffix is reattached via ReattachCustomizationSuffix (B2 fix, Rick's PR #326
        // review) -- a multiset difference against the canonical name's OWN paren groups, so a
        // canonical name that itself contains parens (e.g. "Milk Jug (1%) - White") is never
        // duplicated, and only a genuine guest-added modifier group is reattached.
        (itemName, customizationSuffix) = ReattachCustomizationSuffix(itemName, menuItem.Name);

        // Issue 165: this session's own bound menu-mode gate -- an item that's real and on the
        // menu, but not offered in the active daypart (e.g. a breakfast-only item add while the
        // session is bound to "lunch"). Runs for "add" only: a "modify" target is already IN the
        // order, which means it passed this same gate at add time and the mode never changes
        // mid-session (no mid-conversation ?mode= switching, exactly like persona/model), so
        // gating it again here would be a no-op at best and a spurious reject at worst. A
        // persona with no features.dayparts always resolves menuMode to null, and
        // MenuCatalog.ItemAvailableNow is a no-op (returns true) for a null active mode -- so
        // this block never rejects anything for those packs.
        if (action is "add" or "replace" && !_menu.ItemAvailableNow(itemName, _menuMode))
        {
            string message = _promptLoader?.RenderError(
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

        string requestedSize = _menu.CanonicalSizeKey(size);
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
            string defaultSize = _menu.BundleDefaultSize(itemName);
            string defaultKey = string.IsNullOrEmpty(defaultSize) ? "" : _menu.CanonicalSizeKey(defaultSize);
            if (!string.IsNullOrEmpty(defaultKey) && menuItem.Sizes.Contains(defaultKey))
            {
                requestedSize = defaultKey;
                size = defaultSize;
            }
        }
        if (!menuItem.Sizes.Contains(requestedSize))
        {
            IReadOnlyDictionary<string, string> sizeMap = _menu.SizeMap;
            var availableSizes = menuItem.Sizes.Select(s => sizeMap.GetValueOrDefault(s, Capitalize(s))).ToList();
            string message = _promptLoader?.RenderError(
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
        // #325: `itemName` is now the ref param, which a lambda can't capture directly (CS1628)
        // -- copy it to a local first. It's already the canonical name at this point.
        string resolvedItemName = itemName;
        if (action == "modify" && !_order.Items.Any(item => item.Item == resolvedItemName) && !_order.IsAbsorbedComponent(resolvedItemName))
        {
            string message = _promptLoader?.RenderError("item_not_in_order", Vars(("item_name", menuItem.Name)))
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

    // #325: one shared helper for the add/modify gate AND the remove path (Rick's PR #326
    // review, B2) -- previously each site re-derived the guest's "(modifiers)" suffix with
    // `itemName[openParen..]`, which blindly grabbed EVERYTHING from the first '(' in the
    // MODEL's own string. That's wrong whenever the menu's own canonical name itself contains a
    // paren group (e.g. a shipped "Milk Jug (1%) - White"-style item): a model that echoed the exact
    // canonical spelling got back "Milk Jug (1%) - White (1%) - White" (the canonical name's own
    // "(1%)" duplicated). A multiset difference of the paren groups (`\([^)]*\)`) in the model's
    // raw string vs. the canonical name's OWN groups fixes both: any canonical-name group the
    // model also said is consumed and never re-appended, so only a GENUINE guest-added modifier
    // group (one the canonical name doesn't already have) is reattached. Returns
    // (newItemName, guestModifierSuffix) -- the second value is "" unless the guest actually
    // added a NEW modifier group beyond the canonical name's own, so the unconditional
    // customization-validation block in UpdateOrder can tell "Milk Jug (1%) - White" (the
    // canonical name's own parens, not a guest modifier) apart from "Milk Jug (1%) - White (no
    // ice)" (a genuine customization).
    [System.Text.RegularExpressions.GeneratedRegex(@"\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex ParenGroupRegex();

    private static (string ItemName, string CustomizationSuffix) ReattachCustomizationSuffix(string rawItemName, string canonicalName)
    {
        string raw = rawItemName ?? "";
        string canonical = canonicalName ?? "";
        var rawGroups = ParenGroupRegex().Matches(raw).Select(m => m.Value).ToList();
        var canonicalGroups = ParenGroupRegex().Matches(canonical).Select(m => m.Value).ToList();
        foreach (string? group in canonicalGroups)
        {
            rawGroups.Remove(group);
        }
        if (rawGroups.Count == 0)
        {
            return (canonical, "");
        }
        string suffix = string.Join(" ", rawGroups);
        return ($"{canonical} {suffix}", suffix);
    }

    /// <summary>#325: see UpdateOrder's own "remove" branch -- canonicalizes a resolvable
    /// `remove` itemName the same way CheckAddOrModifyGates does for add/modify, so a removal
    /// matches a line stored under the menu's canonical spelling regardless of which spelling
    /// the model used for this particular turn.</summary>
    private (string ItemName, string CustomizationSuffix) CanonicalizeForRemove(string itemName)
    {
        ResolvedMenuItem? menuItem = _menu.ResolveMenuItem(itemName);
        if (menuItem is null)
        {
            return (itemName, "");
        }
        return ReattachCustomizationSuffix(itemName, menuItem.Name);
    }

    /// <summary>#77: add-time <c>machine_unavailable</c> -- an on-menu item that requires a
    /// machine this persona's OWN <c>machines.&lt;key&gt;.status</c> currently reports "down".
    /// <c>modify</c> never reaches this check.</summary>
    private ToolResult? CheckMachineAvailability(string itemName)
    {
        string? machine = _menu.RequiresMachine(itemName);
        if (string.IsNullOrEmpty(machine) || _order.EffectiveMachineStatus(machine) != "down")
        {
            return null;
        }
        string machineLabel = _menu.MachineLabel(machine);
        string message = _promptLoader?.RenderError(
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
        bool hasAllowedBase = false;
        bool hasBlockedBase = false;
        foreach (OrderItem orderItem in _order.Items)
        {
            string category = _menu.InferCategory(orderItem.Item);
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
        int existingQty = _menu.SpeechStyle == "naturalDriveThru"
            ? _order.Items.Where(i => i.Item == itemName && i.Size == _menu.CanonicalSizeKey(size)).Sum(i => i.Quantity)
            : _order.Items.FirstOrDefault(oi => oi.Item == itemName && oi.Size == size)?.Quantity ?? 0;
        // Widened to long: Python's ints are unbounded, so a huge model-supplied quantity must
        // trip the limit rather than wrapping a 32-bit sum negative and slipping past it.
        long newItemQty = (long)existingQty + quantity;
        if (newItemQty > _maxItemQuantity)
        {
            int allowed = _maxItemQuantity - existingQty;
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

        long totalQty = (long)_order.Items.Sum(oi => oi.Quantity) + quantity;
        if (totalQty > _maxOrderItems)
        {
            int remaining = _maxOrderItems - _order.Items.Sum(oi => oi.Quantity);
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
        string baseName = MenuKeyValidator.StripModifiers(itemName);
        string category = _menu.InferCategory(baseName);
        string modsLower = modsContent.ToLowerInvariant();
        foreach ((string? catKey, IReadOnlyList<string>? forbiddenList) in _menu.InvalidModifiers)
        {
            if (!category.Contains(catKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (string forbidden in forbiddenList)
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
        string displaySize = string.IsNullOrEmpty(size) ||
            (size.ToLowerInvariant() is "" or "standard" or "n/a" or "na" or "none" or "n.a.")
            ? ""
            : size;
        string displayName = $"{(displaySize.Length > 0 ? Capitalize(displaySize) + " " : "")}{itemName}";
        string spokenItemName = _menu.Spoken(itemName);
        string spokenDisplayName = _menu.Spoken(displayName);

        if (resultInfo.ReplacedFromItem is { } originalItem)
        {
            string upchargeText = resultInfo.ComboComponentUpchargeDisplay is { Length: > 0 } upcharge
                ? $" with a {upcharge} upcharge" : "";
            return $"Replaced {_menu.Spoken(originalItem)} with {spokenDisplayName} in your combo{upchargeText} — your total is now {summary.FinalTotalSpoken}";
        }
        if (resultInfo.AbsorbedIntoCombo)
        {
            return resultInfo.ComboComponentUpchargeDisplay is { Length: > 0 } upcharge
                ? $"{spokenDisplayName} included with your combo with a {upcharge} upcharge — your total is {summary.FinalTotalSpoken}"
                : $"{spokenDisplayName} included with your combo — your total is {summary.FinalTotalSpoken}";
        }
        if (resultInfo.ComboConvertedFrom is not null && action == "add")
        {
            string comboDisplay = spokenDisplayName;
            string mods = resultInfo.ModsCarried ?? "";
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
            string tpl = pl.GetDeltaTemplate(action);
            return PromptLoader.RenderTemplate(tpl, Vars(
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
        args.TryGetProperty("price", out JsonElement priceEl) && priceEl.ValueKind == JsonValueKind.Number && priceEl.TryGetDecimal(out decimal price)
            ? price
            : null;

    private static Dictionary<string, object?> Vars(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}
