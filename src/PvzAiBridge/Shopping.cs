using System.Text.Json.Nodes;
using Godot;

namespace PvzAiBridge;

public sealed partial class Entry
{
    private PurchaseLedger? _purchaseLedger;
    private PurchaseLedger Purchases => _purchaseLedger ??= new PurchaseLedger(ProjectSettings.GlobalizePath("user://AiBridge/purchases.json"));
    private Shop? _shop;
    private string _shopId = "";
    private string _preparedTransaction = "";
    private ShopItem? _quotedItem;
    private ShopItemStageConfig? _quotedStage;
    private DialogBoxShopSale? _quotedDialog;

    private void ForgetPreparedPurchase(string id)
    {
        if (id != _preparedTransaction) return;
        _preparedTransaction = ""; _quotedItem = null; _quotedStage = null; _quotedDialog = null;
    }
    private void NativePurchaseClosed(string id)
    {
        try { PurchaseLifecycle.Close(Purchases, id); }
        catch (Exception ex) { _notice = "Purchase close could not be persisted: " + ex.GetBaseException().Message; }
        finally { ForgetPreparedPurchase(id); }
    }
    private void NativePurchaseConfirmed(string id, ShopItem item, DialogBoxShopSale.SaleEventHandler nativeSale)
    {
        try
        {
            var entry = Purchases.Get(id)!;
            var stage = _quotedStage ?? throw new InvalidOperationException("Native quote is no longer bound.");
            var result = PurchaseLifecycle.Confirm(Purchases, id, stage.saveType, stage.addNum,
                () => (TowerDefenseManager.Instance.GetCoin(), StageValue(stage)),
                (coins, value) => {
                    RequireShop(new JsonObject { ["shop_id"] = Text(entry, "shop_id") }, true);
                    int price = Integer(entry, "price");
                    if (id != _preparedTransaction || item != _quotedItem || !VisibleNode(item) || !VisibleNode(_quotedDialog)
                        || _quotedDialog!.item != item || SaleStage(item) != stage || item.currentStageId != Integer(entry, "stage")
                        || item.cost != price || coins < price || price > Integer(entry, "max_spend")
                        || value != entry["value_before"]!.GetValue<long>()
                        || Purchases.Budget(Text(entry, "budget_id"))["expires_at_unix_ms"]!.GetValue<long>() <= UnixMs)
                        throw new InvalidOperationException("Native quote, balance, ownership or budget changed; no purchase made.");
                }, () => nativeSale(item));
            _notice = "Purchase " + Text(result, "status") + ": " + id;
        }
        catch (Exception ex) { _notice = "Purchase outcome requires review: " + ex.GetBaseException().Message; }
    }

    private static bool VisibleNode(Node? n) => Valid(n) && n!.IsInsideTree() && !n.IsQueuedForDeletion()
        && (n is not CanvasItem item || item.IsVisibleInTree());
    private Shop? FindShop()
    {
        var shops = WalkNodes(_tree.Root).OfType<Shop>().Where(n => VisibleNode(n) && !n.editorPreviewMode).ToArray();
        if (shops.Length > 1) throw new InvalidOperationException("Ambiguous shop instance.");
        var current = shops.SingleOrDefault();
        if (current != _shop || !Valid(_shop)) { _shop = current; _shopId = current is null ? "" : Guid.NewGuid().ToString("N"); }
        return current;
    }
    private Shop RequireShop(JsonObject body, bool confirmation = false)
    {
        var shop = FindShop();
        if (shop is null || Text(body, "shop_id") != _shopId) throw new InvalidOperationException("Shop ID is stale; call shop_observe.");
        var blockers = WalkNodes(_tree.Root).OfType<DialogBoxBase>().Where(VisibleNode)
            .Where(n => n != shop && !(confirmation && n == _quotedDialog)).ToArray();
        if (blockers.Length > 0) throw new InvalidOperationException("Another dialog blocks the shop action.");
        return shop;
    }
    private static ShopItemStageConfig? SaleStage(ShopItem item) => Valid(item.config) && item.currentStageId >= 0
        && item.currentStageId < item.config.stageList.Count && Valid(item.config.stageList[item.currentStageId])
        ? item.config.stageList[item.currentStageId] : null;
    private static long StageValue(ShopItemStageConfig stage) => stage.saveType switch {
        "Feature" => GlobalFeatureManager.Instance.GetValue(stage.saveKey),
        "TowerDefensePacket" => GameSaveManager.Instance.GetTowerDefensePacketValue(stage.saveKey).GetValueOrDefault("Unlock", false).AsBool() ? 1 : 0,
        _ => throw new InvalidOperationException("Unsupported purchase effect; no mutation allowed.") };

    private JsonObject ShopObserve()
    {
        var shop = FindShop();
        var items = new JsonArray();
        bool blocked = true; string ledgerStatus = "ready";
        try { blocked = Purchases.Unresolved; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        { ledgerStatus = "unreadable_buying_blocked"; }
        if (shop is not null)
            foreach (var item in WalkNodes(shop).OfType<ShopItem>().Where(VisibleNode))
            {
                var stage = SaleStage(item);
                items.Add(new JsonObject { ["item_id"] = item.GetInstanceId().ToString(), ["stage"] = item.currentStageId,
                    ["price"] = item.cost, ["currency"] = stage?.type ?? "unknown", ["available"] = stage is not null && stage.type == "Coin" && item.cost >= 0,
                    ["description"] = stage is not null ? shop.Tr(stage.describe ?? "").ToString() : "",
                    ["effect_type"] = stage?.saveType, ["effect_key"] = stage?.saveKey,
                    ["effect_amount"] = stage?.saveType == "TowerDefensePacket" ? 1 : stage?.addNum, ["name"] = stage?.saveType == "TowerDefensePacket" ? NameOf(stage.saveKey) : stage?.saveKey,
                    ["value_before"] = stage is not null && stage.saveType is "Feature" or "TowerDefensePacket" ? JsonValue.Create(StageValue(stage)) : null });
            }
        return new JsonObject { ["ok"] = true, ["shop"] = new JsonObject {
            ["shop_id"] = _shopId, ["open"] = shop is not null, ["unlocked"] = GlobalFeatureManager.Instance?.IsUnlocked("Shop") ?? false,
            ["coins"] = Valid(TowerDefenseManager.Instance) ? JsonValue.Create(TowerDefenseManager.Instance.GetCoin()) : null,
            ["currency"] = "Coin", ["category"] = shop?.currentCategory, ["page"] = shop?.currentPage,
            ["page_count"] = shop?.pageNum, ["categories"] = ObservationFacts.Strings(new[] { "Total", "Item", "Packet", "Custom", "Garden" }),
            ["items"] = items, ["prepared_transaction_id"] = _preparedTransaction,
            ["purchase_ledger_status"] = ledgerStatus, ["unknown_purchase_blocks_buying"] = blocked,
            ["confirmation_ready"] = VisibleNode(_quotedDialog),
            ["popup_types"] = ObservationFacts.Strings(WalkNodes(_tree.Root).OfType<DialogBoxBase>().Where(n => n != shop && VisibleNode(n)).Select(n => n.GetType().Name)) } };
    }

    private JsonObject ShopAction(string op, JsonObject body, ref bool started)
    {
        if (op == "shop_budget")
        {
            int seconds = body["ttl_seconds"]?.GetValue<int>() ?? 600;
            if (seconds is < 10 or > 3600) throw new ArgumentException("ttl_seconds must be 10..3600.");
            var keys = body["allowed_effect_keys"] as JsonArray ?? throw new ArgumentException("allowed_effect_keys required.");
            Purchases.Authorize(Text(body, "budget_id"), Integer(body, "max_total"), keys.Select(k => k!.GetValue<string>()), UnixMs + seconds * 1000L);
            return new JsonObject { ["ok"] = true, ["budget"] = Purchases.Budget(Text(body, "budget_id")) };
        }
        if (op == "open_shop")
        {
            PreparationReady();
            if (FindShop() is not null) throw new InvalidOperationException("Shop is already open.");
            if (GlobalFeatureManager.Instance?.IsUnlocked("Shop") != true) throw new InvalidOperationException("Shop is not unlocked by native progression.");
            var main = WalkNodes(_tree.Root).OfType<MainMenu>().SingleOrDefault(n => VisibleNode(n) && !n.wait);
            var selection = TowerDefenseManager.Instance?.GetPacketBankFeature();
            if (main is null && (Running || !SelectionOpen(selection))) throw new InvalidOperationException("Open shop from main menu or ready card selection only.");
            started = true;
            if (main is not null) main.ShopButtonPressed(); else selection!.ShopButtonPressed();
            return ShopObserve();
        }
        if (op == "shop_cancel")
        {
            string id = Text(body, "transaction_id"); var entry = Purchases.Get(id) ?? throw new ArgumentException("Unknown transaction_id.");
            if (Text(entry, "status") == "prepared")
            {
                if (id == _preparedTransaction && VisibleNode(_quotedDialog)) { started = true; _quotedDialog!.FalseButtonPressed(); }
                PurchaseLifecycle.Close(Purchases, id);
            }
            ForgetPreparedPurchase(id);
            return new JsonObject { ["ok"] = true, ["transaction"] = Purchases.Get(id) };
        }
        if (op == "shop_dismiss")
        {
            var current = FindShop();
            if (current is null || Text(body, "shop_id") != _shopId) throw new InvalidOperationException("Shop ID is stale.");
            var popups = WalkNodes(_tree.Root).OfType<DialogBoxBase>().Where(n => VisibleNode(n) && n != current).ToArray();
            if (popups.Length != 1 || popups[0] is not (DialogBoxShopSale or DialogBoxShopCantSale)) throw new InvalidOperationException("No sole supported shopping popup to dismiss.");
            started = true;
            if (popups[0] is DialogBoxShopSale sale) sale.FalseButtonPressed(); else ((DialogBoxShopCantSale)popups[0]).TrueButtonPressed();
            return ShopObserve();
        }
        if (op == "shop_confirm")
        {
            string id = Text(body, "transaction_id"); var entry = Purchases.Get(id) ?? throw new ArgumentException("Unknown transaction_id.");
            if (Text(entry, "status") != "prepared") return new JsonObject { ["ok"] = Text(entry, "status") == "committed", ["transaction"] = entry, ["outcome"] = Text(entry, "status"), ["replayed"] = true };
            RequireShop(body, true);
            if (id != _preparedTransaction || !VisibleNode(_quotedItem) || !VisibleNode(_quotedDialog) || _quotedDialog!.item != _quotedItem
                || SaleStage(_quotedItem!) != _quotedStage || _quotedItem!.currentStageId != Integer(entry, "stage") || _quotedItem.cost != Integer(entry, "price"))
                throw new InvalidOperationException("Native quote/confirmation changed; cancel and obtain a new quote.");
            started = true;
            _quotedDialog.TrueButtonPressed();
            var result = Purchases.Get(id)!; string status = Text(result, "status");
            return new JsonObject { ["ok"] = status == "committed", ["outcome"] = status == "committed" ? "Purchased" : status == "cancelled" ? "rejected" : "unknown_observe_before_retry",
                ["transaction"] = result, ["error"] = result["receipt"]?["error"]?.DeepClone() };
        }
        var shop = RequireShop(body);
        if (_preparedTransaction.Length > 0) throw new InvalidOperationException("Cancel or complete the prepared purchase before navigation.");
        if (op == "close_shop") { started = true; shop.MainMenuButtonPressed(); return new JsonObject { ["ok"] = true, ["outcome"] = "shop_close_requested" }; }
        if (op == "shop_navigate")
        {
            string action = Text(body, "action");
            if (action is not ("next" or "previous" or "Total" or "Item" or "Packet" or "Custom" or "Garden" or "try_levels")) throw new ArgumentException("Unsupported shop navigation.");
            if (action is "next" or "previous" && shop.pageNum <= 0) throw new InvalidOperationException("No native shop pages are available.");
            started = true;
            switch(action) { case "next": shop.NextButtonpressed(); break; case "previous": shop.PreButtonPressed(); break;
                case "Total": shop.TotalButtonPressed(); break; case "Item": shop.ItemButtonPressed(); break; case "Packet": shop.PacketButtonPressed(); break;
                case "Custom": shop.CustomButtonPressed(); break; case "Garden": shop.GardenButtomPressed(); break;
                case "try_levels": shop.TryLevelButtonPressed(); break; }
            return ShopObserve();
        }
        if (op != "shop_prepare") throw new ArgumentException("Unknown shop action.");
        var candidates = WalkNodes(shop).OfType<ShopItem>().Where(n => VisibleNode(n) && n.GetInstanceId().ToString() == Text(body, "item_id")).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("Item ID is stale.");
        var item = candidates[0]; var stage = SaleStage(item);
        int expected = Integer(body, "expected_price"), max = Integer(body, "max_spend");
        if (stage is null || stage.type != "Coin" || stage.saveType is not ("Feature" or "TowerDefensePacket") || stage.saveType == "Feature" && stage.addNum <= 0
            || item.currentStageId != Integer(body, "expected_stage") || item.cost != expected || expected < 0 || max < expected
            || TowerDefenseManager.Instance.GetCoin() < expected) throw new InvalidOperationException("Item is unavailable, changed, unaffordable or outside explicit budget.");
        string tx = Text(body, "transaction_id");
        Purchases.Prepare(tx, new JsonObject { ["budget_id"] = Text(body, "budget_id"), ["shop_id"] = _shopId, ["item_id"] = Text(body, "item_id"), ["stage"] = item.currentStageId,
            ["price"] = expected, ["max_spend"] = max, ["effect_type"] = stage.saveType, ["effect_key"] = stage.saveKey, ["value_before"] = StageValue(stage) });
        _preparedTransaction = tx; _quotedItem = item; _quotedStage = stage;
        started = true; shop.ItemPressed(item);
        var dialogs = WalkNodes(_tree.Root).OfType<DialogBoxShopSale>().Where(n => Valid(n) && n.IsInsideTree() && !n.IsQueuedForDeletion() && n.item == item).ToArray();
        _quotedDialog = dialogs.Length == 1 ? dialogs[0] : null;
        if (_quotedDialog is not null)
        {
            // Replace exactly this dialog's existing native sale callback, then
            // call it once behind the same durable gate for API and player clicks.
            DialogBoxShopSale.SaleEventHandler nativeSale = shop.Sale;
            _quotedDialog.OnSale -= nativeSale;
            _quotedDialog.OnSale += soldItem => NativePurchaseConfirmed(tx, soldItem, nativeSale);
            _quotedDialog.OnClose += () => NativePurchaseClosed(tx);
        }
        else
        {
            foreach (var dialog in dialogs) dialog.FalseButtonPressed();
            PurchaseLifecycle.Close(Purchases, tx);
            ForgetPreparedPurchase(tx);
        }
        return new JsonObject { ["ok"] = _quotedDialog is not null, ["outcome"] = _quotedDialog is not null ? "purchase_prepared" : "confirmation_not_ready",
            ["transaction"] = Purchases.Get(tx), ["note"] = _quotedDialog is not null ? "No coins spent. AI may confirm within the authorized scope; no player click required." : "No purchase confirmation bound; reservation cancelled. Do not replay blindly." };
    }
}
