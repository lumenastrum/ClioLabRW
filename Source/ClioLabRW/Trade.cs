using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

// Overseer, part 4: trading and naming. Andres, 10/4: "If you wanna trade, you can fix it and reboot
// anytime~" — the Leium caravan left while I could only wave. This is Dialog_Trade without the
// window: the same TradeSession/TradeDeal, prices from the negotiator's real skill, executed with
// TradeDeal.TryExecute. The negotiator must be standing near the trader first (walk them over with
// `order goto`), exactly as the right-click "Trade with" requires.

namespace ClioLabRW
{
    internal static class Trade
    {
        private const float MaxNegotiatorDistance = 8f;

        /// <summary>
        /// no args: traders on the map | trader + negotiator: open a session and list the goods |
        /// buy {def:n} / sell {def:n}: set the deal (absolute counts) | execute | cancel.
        /// </summary>
        public static Json Run(Args a)
        {
            Map map = Colony.Home();
            if (a.Bool("cancel"))
            {
                TradeSession.Close();
                return new Json().Str("trade", "cancelled");
            }
            if (a.Has("trader"))
            {
                Pawn trader = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == a.Str("trader") || p.LabelShort == a.Str("trader"));
                if (trader == null || !trader.CanTradeNow) throw new LabError("no trader '" + a.Str("trader") + "' willing to trade now");
                Pawn neg = Commands.FindPawn(a.Str("negotiator"));
                if (!neg.IsColonist) throw new LabError(neg.LabelShort + " is not a colonist");
                if (neg.Position.DistanceTo(trader.Position) > MaxNegotiatorDistance)
                    throw new LabError(neg.LabelShort + " is " + neg.Position.DistanceTo(trader.Position).ToString("0") + " cells from the trader at "
                        + trader.Position.x + "," + trader.Position.z + "; walk over first (order goto)");
                TradeSession.SetupWith(trader, neg, false);
            }
            if (!TradeSession.Active) return Traders(map);

            foreach (KeyValuePair<string, object> kv in a.Dict("buy")) Set(kv.Key, Convert.ToInt32(kv.Value), false);
            foreach (KeyValuePair<string, object> kv in a.Dict("sell")) Set(kv.Key, Convert.ToInt32(kv.Value), true);
            TradeSession.deal.UpdateCurrencyCount();

            if (a.Bool("execute"))
            {
                if (!TradeSession.deal.DoesTraderHaveEnoughSilver()) throw new LabError("the trader can't pay for that much");
                int silverBefore = SilverAfter();
                bool ok = TradeSession.deal.TryExecute(out bool traded);
                string who = TradeSession.trader.TraderName;
                TradeSession.Close();
                if (!ok) throw new LabError("deal refused (can't afford?)");
                return new Json().Str("trade", traded ? "done" : "nothing traded").Str("with", who);
            }
            return Goods(a.Bool("all"));
        }

        private static Json Traders(Map map)
        {
            List<string> rows = new List<string>();
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.CanTradeNow && p.Faction != Faction.OfPlayer))
                rows.Add(new Json().Str("id", p.ThingID).Str("name", p.LabelShort).Str("faction", p.Faction?.Name)
                    .Str("kind", p.TraderKind?.label).Str("pos", p.Position.x + "," + p.Position.z).ToString());
            return new Json().Raw("traders", Json.Array(rows)).Str("hint", "trade {trader, negotiator} to open (negotiator within 8 cells)");
        }

        /// <summary>count = how many to move (absolute): buy from the trader or sell to them.</summary>
        private static void Set(string defName, int count, bool sell)
        {
            List<Tradeable> matches = TradeSession.deal.AllTradeables
                .Where(t => !t.IsCurrency && t.ThingDef != null && string.Equals(t.ThingDef.defName, defName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) throw new LabError("nothing tradeable called " + defName);
            // Several stacks of one def (qualities, stuffs) are separate tradeables: fill in order.
            int left = count;
            foreach (Tradeable t in matches)
            {
                int have = sell ? t.CountHeldBy(Transactor.Colony) : t.CountHeldBy(Transactor.Trader);
                int n = Math.Min(left, have);
                // Positive CountToTransfer means "toward destination" for some tradeables and "toward
                // source" for others; translate a plain buy/sell count into the raw value.
                bool positiveIsSell = t.PositiveCountDirection == TransferablePositiveCountDirection.Destination;
                int raw = sell == positiveIsSell ? n : -n;
                if (!t.CanAdjustTo(raw).Accepted) throw new LabError("can't " + (sell ? "sell " : "buy ") + n + " " + defName + ": " + t.CanAdjustTo(raw).Reason);
                t.AdjustTo(raw);
                left -= n;
            }
            if (left > 0) throw new LabError("only " + (count - left) + " " + defName + " available to " + (sell ? "sell" : "buy"));
        }

        private static int SilverAfter()
        {
            Tradeable cur = TradeSession.deal.CurrencyTradeable;
            return cur == null ? 0 : cur.CountPostDealFor(Transactor.Colony);
        }

        private static Json Goods(bool all)
        {
            List<string> rows = new List<string>();
            foreach (Tradeable t in TradeSession.deal.AllTradeables)
            {
                if (t.IsCurrency || t.ThingDef == null) continue;
                int col = t.CountHeldBy(Transactor.Colony), tr = t.CountHeldBy(Transactor.Trader);
                if (!all && tr == 0 && t.ActionToDo == TradeAction.None) continue;   // our junk they'd buy: ask with all:true
                Json j = new Json().Str("def", t.ThingDef.defName).Str("label", t.Label).Num("ours", col).Num("theirs", tr);
                if (tr > 0) j.Num("buy", t.GetPriceFor(TradeAction.PlayerBuys), 1);
                if (col > 0 && t.TraderWillTrade) j.Num("sell", t.GetPriceFor(TradeAction.PlayerSells), 1);
                if (t.ActionToDo != TradeAction.None) j.Str("deal", t.ActionToDo + " " + Math.Abs(t.CountToTransfer));
                rows.Add(j.ToString());
            }
            return new Json().Str("with", TradeSession.trader.TraderName).Str("negotiator", TradeSession.playerNegotiator.LabelShort)
                .Num("silverNow", TradeSession.deal.CurrencyTradeable?.CountHeldBy(Transactor.Colony) ?? 0)
                .Num("silverAfter", SilverAfter()).Bool("traderCanPay", TradeSession.deal.DoesTraderHaveEnoughSilver())
                .Raw("goods", Json.Array(rows));
        }

        // ------------------------------------------------------------------ names

        /// <summary>zone + to, or pawn + to (an animal's name, or a colonist's nickname).</summary>
        public static Json Rename(Args a)
        {
            Map map = Colony.Home();
            string to = a.Str("to");
            if (string.IsNullOrEmpty(to)) throw new LabError("rename needs 'to'");
            if (a.Has("zone"))
            {
                Zone z = map.zoneManager.AllZones.FirstOrDefault(x => string.Equals(x.label, a.Str("zone"), StringComparison.OrdinalIgnoreCase));
                if (z == null) throw new LabError("no zone '" + a.Str("zone") + "'");
                string old = z.label;
                z.label = to;
                return new Json().Str("zone", old).Str("now", to);
            }
            Pawn p = Commands.FindPawn(a.Str("pawn"));
            if (p.Faction != Faction.OfPlayer) throw new LabError(p.LabelShort + " isn't ours to name");
            string before = p.LabelShort;
            if (p.Name is NameTriple nt) p.Name = new NameTriple(nt.First, to, nt.Last);
            else p.Name = new NameSingle(to);
            return new Json().Str("pawn", before).Str("now", p.LabelShort);
        }
    }
}
