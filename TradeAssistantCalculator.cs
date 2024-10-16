﻿using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.DynamicValues;
using Eco.Gameplay.Items;
using Eco.Gameplay.Items.Recipes;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Gameplay.Property;
using Eco.Gameplay.Systems.NewTooltip;
using Eco.Gameplay.Systems.TextLinks;
using Eco.Shared;
using Eco.Shared.Items;
using Eco.Shared.Localization;
using Eco.Shared.Math;
using Eco.Shared.Utils;
using Eco.Shared.Voxel;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TradeAssistant
{
    public class TradeAssistantCalculator
    {
        private record CachedPrice(float Price, StringBuilder Reason, Recipe Recipe = null, List<LocString> Warnings = null);
        private record IngredientPrice(float Price, Item Item, LocString Reason, List<LocString> Warnings);
        private record ProductPrice(CraftingElement Product, float Price, LocString Reason);
        private List<CraftingComponent> CraftingTables { get; }
        private List<User> Users { get; }
        private Dictionary<int, float> StoreBuyPrices { get; }

        private Dictionary<int, float> StoreSellPrices { get; }
        private Dictionary<int, CachedPrice> CachedPrices { get; } = new();

        public UserConfig Config { get; }
        public StoreComponent Store { get; }
        public Dictionary<LocString, List<Item>> CraftableItems { get; }

        public const int WORLD_OBJECT_CAP_NUMBER = 1;
        public const int TOOL_CAP_NUMBER = 5;

        private TradeAssistantCalculator(StoreComponent store, List<CraftingComponent> craftingTables, Dictionary<LocString, List<Item>> craftableItems, User user, UserConfig config)
        {
            Store = store;
            CraftingTables = craftingTables;
            CraftableItems = craftableItems;
            Users = config.PartnerPlayers.Append(user.Id).ToHashSet().Select(id => UserManager.FindUserByID(id)).ToList();
            Config = config;

            StoreBuyPrices = store.StoreData.BuyOffers
                .GroupBy(o => o.Stack.Item.TypeID)
                .ToDictionary(x => x.Key, x => x.Max(o => o.Price));

            StoreSellPrices = store.StoreData.SellOffers
                .GroupBy(o => o.Stack.Item.TypeID)
                .ToDictionary(x => x.Key, x => x.Min(o => o.Price));
        }

        public static async Task<TradeAssistantCalculator> TryInitialize(User user)
        {
            var sb = new StringBuilder();
            var deed = GetDeed(sb, user);
            if (deed == null)
            {
                user.MsgLocStr(sb.ToString());
                return null;
            }

            var store = await GetStore(sb, user, deed);
            if (store == null)
            {
                user.MsgLocStr(sb.ToString());
                return null;
            }

            if (TryGetStoreAndCraftingTables(sb, user, deed, out var craftingTables, out var craftableItems))
            {
                //If the profit % is set to -100% the mod calculations won't work
                if (user.Config().Profit == -100f)
                {
                    user.TempServerMessage(Localizer.Do($"Price calculations won't work your profit is set to -100%"));
                    return null;
                }

                return new TradeAssistantCalculator(store!, craftingTables!, craftableItems!, user, user.Config());
            }

            user.MsgLocStr(sb.ToString());
            return null;
        }

        public Dictionary<int, List<int>> ProductToRequiredItemsLookup()
        {
            return CraftingTables
                .SelectMany(w => w.Recipes.Where(recipe => Users.Any(user => recipe.RequiredSkills.All(s => s.IsMet(user)))))
                .SelectMany(r => r.Recipes.Skip(r.CraftableDefault ? 0 : 1))
                .SelectMany(r => r.Products.Select(i => new { product = i.Item, r.Ingredients }))
                .SelectMany(x => x.Ingredients.Select(i => new { x.product, ingredient = i }))
                .SelectMany(x => x.ingredient.IsSpecificItem ? new[] { new { x.product, item = x.ingredient.Item } } : x.ingredient.Tag.TaggedItems().Select(t => new { x.product, item = t }))
                .Where(x => !x.item.Hidden)
                .DistinctBy(x => $"{x.product.TypeID}:{x.item.TypeID}")
                .GroupBy(x => x.product.TypeID)
                .ToDictionary(x => x.Key, x => x.Select(y => y.item.TypeID).ToList());
        }

        public bool TryGetCostPrice(int item, out float outPrice, out StringBuilder reason, out List<LocString> warnings) => TryGetCostPrice(Item.Get(item), out outPrice, out reason, out warnings);
        public bool TryGetCostPrice(Item item, out float outPrice, out StringBuilder reason, out List<LocString> warnings)
        {
            warnings = null;

            // Check if the user is buying the item already in the store
            var hasBuyOrder = StoreBuyPrices.TryGetValue(item.TypeID, out var buyPrice);
            var buyReason = Localizer.DoStr($"{Store.Parent.UILink()} has a buy order for {item.UILink()} at a price of {Text.StyledNum(buyPrice)}");

            if (hasBuyOrder && !StoreSellPrices.ContainsKey(item.TypeID))
            {
                reason = new StringBuilder(buyReason);
                outPrice = buyPrice;
                return true;
            }

            // Check if we haven't already calculated the price of the item.
            if (CachedPrices.TryGetValue(item.TypeID, out var cachedPrice))
            {
                outPrice = cachedPrice.Price;
                reason = cachedPrice.Reason;
                warnings = cachedPrice.Warnings;
                return !float.IsPositiveInfinity(cachedPrice.Price);
            }
            CachedPrices.Add(item.TypeID, new CachedPrice(float.PositiveInfinity, new StringBuilder(Localizer.Do($"Recursive recipe!"))));

            if (Config.FrozenSellPrices.Any(typeID => typeID == item.TypeID))
            {
                var productIsSold = StoreSellPrices.TryGetValue(item.TypeID, out outPrice);
                if (productIsSold)
                {
                    // SellPrice = CostPrice * (1 + Profit) / (1 - TaxRate)
                    // CostPrice = SellPrice / (1 + Profit) * (1 - TaxRate)
                    var costPrice = outPrice / (1 + Config.Profit / 100f) * (1 - Store.GetTax());
                    reason = new StringBuilder(Localizer.Do($"{item.UILink()} has a frozen sell price and is sold at a price of {outPrice.ToStyledNum()}"));
                    reason.AppendLineLoc($"Cost Price = SellPrice / (1 + Profit) * (1 - TaxRate)");
                    reason.AppendLineLoc($"Cost Price = {outPrice.ToStyledNum()} / (1 + {(Config.Profit / 100f).ToStyledNum()}) * (1 - {Store.GetTax().ToStyledNum()}) = {costPrice.ToStyledNum()}");
                    outPrice = costPrice;
                }
                else
                {
                    outPrice = float.PositiveInfinity;
                    reason = new StringBuilder(Localizer.Do($"No sell order for item with frozen sell price: {item.UILink()}"));
                }

                CachedPrices[item.TypeID] = new CachedPrice(outPrice, reason);
                return productIsSold;
            }


            var recipes = CraftingTables.SelectMany(ct => ct.Recipes
                .SelectMany(rf => rf.Recipes.Skip(rf.CraftableDefault ? 0 : 1).Select(r => (ct, r)))
                .Where(x => x.r.Products.Any(i => i.Item.TypeID == item.TypeID))
            ).ToList();

            if (!recipes.Any())
            {
                outPrice = hasBuyOrder ? buyPrice : float.PositiveInfinity;
                reason = new StringBuilder(Localizer.DoStr($"There is no recipe on any of the crafting tables that can craft {item.UILink()}"));
                if (hasBuyOrder) reason.Append("\n\n").AppendLine(buyReason);
                CachedPrices[item.TypeID] = new CachedPrice(outPrice, reason);
                return hasBuyOrder;
            }

            CachedPrice bestPrice = null;
            var rejectedPrices = new List<CachedPrice>();
            var failedRecipes = new List<LocString>();
            foreach (var (craftingTable, recipe) in recipes)
                foreach (var user in Users)
                {
                    var explanation = new StringBuilder();
                    LocString userText = Users.Count == 1 ? LocString.Empty : Localizer.Do($" by {user.UILink()}");

                    // Ignore by-products if there is more than one product, and make sure the only product is the specified item
                    var products = recipe.Products;
                    var resourceEfficiencyContext = new ModuleContext(user, craftingTable.Parent.Position, craftingTable.ResourceEfficiencyModule);
                    if (recipe.Products.Count > 1)
                    {
                        products = recipe.Products.Where(p => !Config.ByProducts.Any(byProductId => byProductId == p.Item.TypeID)).ToList();
                        if (products.Count > 1)
                            failedRecipes.AddLoc($"{recipe.UILink()} has multiple output products, specify which are by-product(s): {string.Join(", ", products.Select(p => p.Item.UILink()))}");
                        else if (products.Count == 0)
                            failedRecipes.AddLoc($"All the products ({string.Join(", ", recipe.Products.Select(p => p.Item.UILink()))}) of the {recipe.UILink()} has been marked as waste, one should not be. Run {Text.Name("/ta config")} to set the by-products.");
                        if (products.Count != 1) continue;
                    }
                    var product = products[0];
                    if (product.Item.TypeID != item.TypeID)
                    {
                        failedRecipes.AddLoc($"{recipe.UILink()} has {item.UILink()} as a by-product. The main product of that recipe seems to be {product.Item.UILink()}.");
                        continue;
                    }

                    // Check the price of the by-products
                    var byProducts = recipe.Products.Where(p => p != product).Select(p => ParseByProduct(p, resourceEfficiencyContext, craftingTable)).ToList();
                    var unsetByProduct = byProducts.Where(p => float.IsPositiveInfinity(p.Price)).FirstOrDefault();
                    if (unsetByProduct != null)
                    {
                        failedRecipes.AddLoc($"No price set for the by-product {unsetByProduct.Product.Item.UILink()}, unable to process the recipe {recipe.UILink()}");
                        continue;
                    }
                    var byProductsPrice = byProducts.Sum(p => p.Price);
                    var byProductText = LocString.Empty;
                    if (byProducts.Any())
                    {
                        var byProductFoldout = TextLoc.Foldout(TextLoc.StyledNum(byProductsPrice), Localizer.Do($"By-Products for {recipe.UILink()}"), byProducts.Select(bp => bp.Reason).FoldoutListLoc("By-Product", Eco.Shared.Items.TooltipOrigin.None));
                        explanation.AppendLineLoc($"By-Products: {byProductFoldout}");
                    }

                    // Labour cost
                    var labourCost = recipe.Family.LaborInCalories.GetCurrentValue(user) * Config.CostPer1000Calories / 1000f;
                    explanation.AppendLineLoc($"Labour{userText}: UserModifiedCalories ({Text.StyledNum(recipe.Family.LaborInCalories.GetCurrentValue(user))}) * CostPer1000Calories ({Text.StyledNum(Config.CostPer1000Calories)}) / 1000 = {Text.StyledNum(labourCost)}");

                    // Ingredients cost
                    IngredientPrice getIngredientPrice(Item ingredient, IngredientElement element)
                    {
                        if (!TryGetCostPrice(ingredient, out var tempPrice, out var reason, out var innerWarnings))
                            return new IngredientPrice(float.PositiveInfinity, ingredient, reason.ToStringLoc(), innerWarnings);

                        var ingredientCount = element.Quantity.GetCurrentValue(resourceEfficiencyContext, craftingTable);
                        var count = ingredientCount;
                        var countText = TextLoc.StyledNum(count);

                        if (item is WorldObjectItem)
                        {
                            count = element.Quantity.GetCurrentValueInt(resourceEfficiencyContext, craftingTable, WORLD_OBJECT_CAP_NUMBER) * 1f / WORLD_OBJECT_CAP_NUMBER;
                            countText = TextLoc.Foldout(TextLoc.StyledNum(count), Localizer.Do($"Count rounding reason"), Localizer.Do($"Crafting placable items are capped at crafting {Text.Info(WORLD_OBJECT_CAP_NUMBER)} at a time, so the count got rounded up from {Text.StyledNum(ingredientCount)} to {Text.StyledNum(count)}"));
                        }
                        else if (item.IsTool)
                        {
                            count = element.Quantity.GetCurrentValueInt(resourceEfficiencyContext, craftingTable, TOOL_CAP_NUMBER) * 1f / TOOL_CAP_NUMBER;
                            countText = TextLoc.Foldout(TextLoc.StyledNum(count), Localizer.Do($"Count rounding reason"), Localizer.Do($"Tools are capped at crafting {Text.Info(TOOL_CAP_NUMBER)} tool(s) at a time, so the count got rounded up from {Text.StyledNum(ingredientCount)} to {Text.StyledNum(count)}"));

                        }
                        var costPriceLink = TextLoc.FoldoutLoc($"CostPrice", $"Cost price of {ingredient.UILink()}", reason.ToStringLoc());
                        return new IngredientPrice(tempPrice * count, ingredient, Localizer.Do($"Ingredient {ingredient.UILink()}: Count ({countText}) * {costPriceLink} ({Text.StyledNum(tempPrice)}) = {Text.StyledNum(tempPrice * count)}"), innerWarnings);
                    }

                    var costs = recipe.Ingredients.Select(i => i.IsSpecificItem
                        ? getIngredientPrice(i.Item, i)
                        : i.Tag.TaggedItems().Select(ti => getIngredientPrice(ti, i)).OrderBy(x => x.Price).First());
                    if (costs.Any(c => float.IsPositiveInfinity(c.Price)))
                    {
                        failedRecipes.AddLoc($"For crafting {recipe.UILink()} we could not determine the price of {string.Join(", ", costs.Where(c => float.IsPositiveInfinity(c.Price)).Select(c => Localizer.Do($"{c.Item.UILink()} ({WhyFoldout(c)})")))}. Please run /ta setupbuy to add missing ingredients");
                        continue;
                    };
                    var ingredientsTotal = costs.Select(c => c.Price).Sum();
                    costs.ForEach(c => explanation.AppendLine(c.Reason));
                    var ingredientWarnings = costs.Where(c => c.Warnings != null).SelectMany(c => c.Warnings!).ToList();
                    if (ingredientWarnings.Any())
                    {
                        warnings ??= new List<LocString>();
                        warnings.AddRange(ingredientWarnings);
                    }
                    explanation.AppendLineLoc($"Ingredients Total: {Text.StyledNum(ingredientsTotal)}");

                    var productCount = product.Quantity.GetCurrentValue(resourceEfficiencyContext, craftingTable);

                    var totalCost = (ingredientsTotal - byProductsPrice + labourCost) / productCount;
                    explanation.AppendLineLoc($"Total Cost: (Ingredients ({Text.StyledNum(ingredientsTotal)}) - ByProduct ({Text.StyledNum(byProductsPrice)}) + Labour Cost ({Text.StyledNum(labourCost)})) / ProductCount ({Text.StyledNum(productCount)}) = {Text.StyledNum(totalCost)}");
                    if (bestPrice == null || totalCost < bestPrice.Price)
                    {
                        if (bestPrice != null) { rejectedPrices.Add(bestPrice); }
                        bestPrice = new CachedPrice(totalCost, explanation, recipe);
                    }
                    else
                        rejectedPrices.Add(new CachedPrice(totalCost, explanation, recipe));
                }


            if (bestPrice == null)
            {
                reason = new StringBuilder();
                reason.AppendLineLoc($"Could not calculate the item cost from any of the provided recipes:");
                foreach (var recipeError in failedRecipes)
                    reason.AppendLine($"- {recipeError}");
                if (hasBuyOrder) reason.AppendLine().AppendLine(buyReason);

                outPrice = hasBuyOrder ? buyPrice : float.PositiveInfinity;
                CachedPrices[item.TypeID] = new CachedPrice(outPrice, reason);
                return hasBuyOrder;
            }
            else
            {
                if (failedRecipes.Count > 0 || rejectedPrices.Count > 0)
                {
                    var rejectedWarnings = new List<LocString>();
                    rejectedPrices
                        .Select(price => new
                        {
                            Price = price,
                            Foldout = TextLoc.FoldoutLoc($"{(price.Price > bestPrice.Price ? Localizer.Do($"greater than") : Localizer.Do($"equal to"))}", $"{item.UILink()} alternative cost price", price.Reason.ToStringLoc()),
                            // BestPrice * (1 + Percentage) = OtherPrice
                            // 1 + Percentage = OtherPrice / BestPrice
                            // Percentage = OtherPrice / BestPrice - 1
                            IncreasePercentage = bestPrice.Price <= 0 ? float.PositiveInfinity : (price.Price / bestPrice.Price - 1f),
                        })
                        .ForEach(x =>
                        {
                            failedRecipes.AddLoc($"{x.Price.Recipe.UILink()}'s price is{(x.IncreasePercentage > 0 ? (" " + Text.Negative(Text.Percent(x.IncreasePercentage))) : LocString.Empty)} {x.Foldout} the selected recipe's price.");
                            if (x.IncreasePercentage > Config.Profit / 100f)
                            {
                                rejectedWarnings.AddLoc($"Crafting {x.Price.Recipe.UILink()} will cause you to lose money. You should decrease the buy price of its ingredients");
                            }
                        });

                    if (rejectedWarnings.Count > 0)
                    {
                        warnings ??= new List<LocString>();
                        warnings.AddRange(rejectedWarnings);
                    }

                    var failedFoldout = TextLoc.FoldoutLoc($"these", $"Ignored recipes", new StringBuilder(string.Join("\n", failedRecipes.Select(m => $"- {m}"))).ToStringLoc());
                    bestPrice.Reason.Insert(0, Localizer.Do($"Ignored {failedFoldout} recipes.\n"));
                }
                if (warnings != null)
                    bestPrice = new CachedPrice(bestPrice.Price, bestPrice.Reason, bestPrice.Recipe, new List<LocString>(warnings));
                CachedPrices[item.TypeID] = bestPrice;
                outPrice = bestPrice.Price;
                reason = bestPrice.Reason;
                return true;
            }
        }

        private static LocString WhyFoldout(IngredientPrice price) => TextLoc.FoldoutLoc($"Why?", $"Why {price.Item.UILink()}", price.Reason);

        private ProductPrice ParseByProduct(CraftingElement product, ModuleContext resourceEfficiency, CraftingComponent craftingTable)
        {
            if (!StoreSellPrices.TryGetValue(product.Item.TypeID, out var storePrice))
                return new ProductPrice(product, float.PositiveInfinity, Localizer.Do($"No sell price set for the by-product {product.Item.UILink()}."));
            // SellPrice = CostPrice * (1 + Profit) / (1 - TaxRate)
            // CostPrice = SellPrice / (1 + Profit) * (1 - TaxRate)
            var costPrice = storePrice / (1 + Config.Profit / 100f) * (1 - Store.GetTax());
            var quantity = product.Quantity.GetCurrentValue(resourceEfficiency, craftingTable);
            var totalCostPrice = costPrice * quantity;

            var costReasonContent = new LocStringBuilder();
            costReasonContent.AppendLine(Localizer.Do($"CostPrice = SellPrice / (1 + Profit) * (1 - TaxRate)"));
            costReasonContent.AppendLine(Localizer.Do($"          = {Text.StyledNum(storePrice)} / (1 + {Text.StyledNum(Config.Profit / 100f)}) * (1 - {Text.StyledNum(Store.GetTax())})"));
            costReasonContent.AppendLine(Localizer.Do($"          = {Text.StyledNum(costPrice)}"));
            var costLink = TextLoc.Foldout(Localizer.Do($"CostPrice ({Text.StyledNum(costPrice)})"), Localizer.Do($"By-Product {product.Item.UILink()} cost price"), costReasonContent.ToLocString());

            var reason = Localizer.Do($"{Text.StyledNum(quantity)} {product.Item.UILink()} * {costLink} = {Text.StyledNum(totalCostPrice)}");
            return new ProductPrice(product, totalCostPrice, reason);
        }

        private static Deed GetDeed(StringBuilder sb, User user)
        {
            // Get the plot the user is currently standing in
            var playerPlot = user.Position.XZi().ToPlotPos();

            // Check if the user is standing in a deed
            var accessedDeeds = PropertyManager.Obj.Deeds.Where(d => d.IsAuthorized(user, AccessType.FullAccess));
            var deedStandingIn = accessedDeeds.FirstOrDefault(d => d.Plots.Any(p => p.PlotPos == playerPlot));
            if (deedStandingIn == null)
            {
                sb.AppendLine(Localizer.Do($"You have to stand in a deed you have access to when you run this command"));
                return null;
            }
            return deedStandingIn;
        }
        private static async Task<StoreComponent> GetStore(StringBuilder sb, User user, Deed deed)
        {
            var stores = WorldObjectUtil.AllObjsWithComponent<StoreComponent>()
                .Where(store => store.IsRPCAuthorized(user.Player, AccessType.FullAccess, Array.Empty<object>())
                    && deed.Plots.Any(p => p.PlotPos == store.Parent.PlotPos()))
                .OrderBy(store => World.WrappedDistance(user.Player.WorldPos(), store.Parent.WorldPos()))
                .Take(2)
                .ToList();
            if (stores.Count == 0)
            {
                sb.AppendLine(Localizer.Do($"You don't have a store you have owner access to in the plot you are standing in"));
                return null;
            }
            var store = stores.First();

            if (stores.Count == 1 || await user.ConfirmBoxLoc($"You have multiple stores on your Plot, please confirm to apply the command to the nearest store: {store.Parent.MarkedUpName}"))
                return store;

            sb.AppendLine(Localizer.Do($"You have more than one store on this property and declined to use the current closes store."));
            return null;
        }
        private static bool TryGetStoreAndCraftingTables(StringBuilder sb, User user, Deed deed, out List<CraftingComponent> craftingTables, out Dictionary<LocString, List<Item>> craftableItems)
        {
            craftingTables = null;
            craftableItems = null;


            // Get all the crafting tables in the deed
            craftingTables = WorldObjectUtil.AllObjsWithComponent<CraftingComponent>()
                .Where(workbench => workbench.IsRPCAuthorized(user.Player, AccessType.FullAccess, Array.Empty<object>()) && deed.Plots.Any(p => p.PlotPos == workbench.Parent.PlotPos()))
                .DistinctBy(craftingTable => $"{craftingTable.Parent.Name}:{(craftingTable.ResourceEfficiencyModule == null ? "null" : craftingTable.ResourceEfficiencyModule.Name)}")
                .ToList();
            if (!craftingTables.Any())
            {
                sb.AppendLine(Localizer.Do($"Could not find any crafting tables in {deed.UILink()}"));
                return false;
            }

            // Check that the user can at least craft one recipe from the crafting tables
            craftableItems = craftingTables
                .SelectMany(ct => ct.Recipes
                    .Where(recipe => recipe.RequiredSkills.All(s => s.IsMet(user)))
                    .SelectMany(rf => rf.CraftableDefault ? rf.Recipes : rf.Recipes.Skip(1))
                    .SelectMany(r => r.Products)
                    .Select(p => new { CraftingTable = ct.Parent.DisplayName, p.Item })
                )
                .DistinctBy(x => $"{x.CraftingTable}:{x.Item.Name}")
                .GroupBy(x => x.CraftingTable)
                .ToDictionary(x => x.Key, x => x.Select(x => x.Item).ToList());
            if (!craftableItems.Any())
            {
                sb.AppendLine(Localizer.Do($"You don't have the required skills/levels to craft any of the recipes in these crafting tables: {string.Join(", ", craftingTables.Select(w => w.Parent.UILink()))}"));
                return false;
            }

            return true;
        }

        public List<LocString> SetSellPrice(int item, float newPrice) => SetSellPrice(Item.Get(item), newPrice);
        public List<LocString> SetSellPrice(Item item, float newPrice)
        {
            newPrice = Mathf.RoundToAcceptedDigits(newPrice);
            StoreSellPrices[item.TypeID] = newPrice;

            var msgs = new List<LocString>();
            Store.StoreData.SellOffers.Where(o => o.Stack.Item.TypeID == item.TypeID && o.Price != newPrice).ForEach(o =>
            {
                msgs.AddLoc($"Updating sell price of {item.UILink()} from {Text.StyledNum(o.Price)} to {Text.StyledNum(newPrice)}");
                o.Price = newPrice;
            });
            return msgs;
        }

        public List<LocString> SetBuyPrice(int item, float newPrice) => SetBuyPrice(Item.Get(item), newPrice);
        public List<LocString> SetBuyPrice(Item item, float newPrice)
        {
            newPrice = Mathf.RoundToAcceptedDigits(newPrice);
            StoreBuyPrices[item.TypeID] = newPrice;

            var msgs = new List<LocString>();
            Store.StoreData.BuyOffers.Where(o => o.Stack.Item.TypeID == item.TypeID && o.Price != newPrice).ForEach(o =>
            {
                msgs.AddLoc($"Updating buy price of {item.UILink()} from {Text.StyledNum(o.Price)} to {Text.StyledNum(newPrice)}");
                o.Price = newPrice;
            });
            return msgs;
        }
    }
}