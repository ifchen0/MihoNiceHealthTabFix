using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace NiceHealthTabPatch
{
    /// <summary>
    /// Lets the negotiator pay with their royal favor at traders whose currency is favor.
    /// </summary>
    /// <remarks>
    /// Vanilla Tradeable_RoyalFavor reports 0 favor on the colony side and only handles favor flowing to the player, so
    /// favor traders (such as Miho's relic curators) can be sold to but nothing they stock can be bought. The colony side
    /// now reads the negotiator's favor with the trader's faction, and favor paid to the trader is removed from the
    /// negotiator. The favor price is fixed at 0 so paying in favor does not count as market value sent for goodwill
    /// (vanilla would read the price of a thing that does not exist and throw).
    /// </remarks>
    [StaticConstructorOnStartup]
    public static class FavorSpendPatch
    {
        static FavorSpendPatch()
        {
            var harmony = new Harmony("ifchen0.nicehealthtabpatch");
            harmony.Patch(AccessTools.Method(typeof(Tradeable_RoyalFavor), nameof(Tradeable.CountHeldBy)),
                postfix: new HarmonyMethod(typeof(FavorSpendPatch), nameof(CountHeldByPostfix)));
            harmony.Patch(AccessTools.Method(typeof(Tradeable_RoyalFavor), nameof(Tradeable.ResolveTrade)),
                postfix: new HarmonyMethod(typeof(FavorSpendPatch), nameof(ResolveTradePostfix)));
            harmony.Patch(AccessTools.Method(typeof(Tradeable), nameof(Tradeable.GetPriceFor)),
                prefix: new HarmonyMethod(typeof(FavorSpendPatch), nameof(GetPriceForPrefix)),
                postfix: new HarmonyMethod(typeof(FavorSpendPatch), nameof(GetPriceForPostfix)));
        }

        private static void CountHeldByPostfix(Transactor trans, ref int __result)
        {
            if (trans != Transactor.Colony)
                return;
            Faction faction = TradeSession.trader?.Faction;
            Pawn negotiator = TradeSession.playerNegotiator;
            if (faction != null && negotiator?.royalty != null)
                __result = negotiator.royalty.GetFavor(faction);
        }

        private static void ResolveTradePostfix(Tradeable_RoyalFavor __instance)
        {
            if (__instance.ActionToDo != TradeAction.PlayerSells)
                return;
            Faction faction = TradeSession.trader?.Faction;
            Pawn negotiator = TradeSession.playerNegotiator;
            if (faction != null && negotiator?.royalty != null)
                negotiator.royalty.TryRemoveFavor(faction, __instance.CountToTransferToDestination);
        }

        private static bool GetPriceForPrefix(Tradeable __instance, ref float __result)
        {
            if (!(__instance is Tradeable_RoyalFavor))
                return true;
            __result = 0f;
            return false;
        }

        // Buy prices in favor come from market value x 1.4 x negotiator bonus and are shown unformatted (e.g. 6.2999997),
        // so they are rounded up to whole favor points (up, so they never drop below the sell price, which vanilla caps at the buy price). Sell prices are the items' RoyalFavorValue and stay as they are.
        private static void GetPriceForPostfix(Tradeable __instance, TradeAction action, ref float __result)
        {
            if (action != TradeAction.PlayerBuys || __instance is Tradeable_RoyalFavor || !TradeSession.Active || TradeSession.TradeCurrency != TradeCurrency.Favor)
                return;
            __result = Mathf.Max(1f, Mathf.Ceil(__result - 0.001f));
        }
    }
}
