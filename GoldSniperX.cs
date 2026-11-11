
using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GoldSniperX : Robot
    {
        [Parameter("Trade size (lots)", DefaultValue = 0.01, MinValue = 0.01, MaxValue = 0.10, Step = 0.01)]
        public double Lots { get; set; }

        [Parameter("Daily loss limit (%)", DefaultValue = 10, MinValue = 1, MaxValue = 10)]
        public double DailyLossLimit { get; set; }

        [Parameter("Maximum trades per day", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Fast EMA", DefaultValue = 20, MinValue = 2)]
        public int FastPeriod { get; set; }

        [Parameter("Slow EMA", DefaultValue = 50, MinValue = 3)]
        public int SlowPeriod { get; set; }

        [Parameter("RSI period", DefaultValue = 14, MinValue = 2)]
        public int RsiPeriod { get; set; }

        [Parameter("ATR period", DefaultValue = 14, MinValue = 2)]
        public int AtrPeriod { get; set; }

        [Parameter("ATR stop multiplier", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 5)]
        public double AtrMultiplier { get; set; }

        [Parameter("Reward / risk", DefaultValue = 2.0, MinValue = 1, MaxValue = 5)]
        public double RewardRisk { get; set; }

        [Parameter("Break-even trigger (R)", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 3)]
        public double BreakEvenTrigger { get; set; }

        [Parameter("Trailing starts (R)", DefaultValue = 1.5, MinValue = 1, MaxValue = 5)]
        public double TrailingStart { get; set; }

        [Parameter("ATR trailing multiplier", DefaultValue = 1.2, MinValue = 0.5, MaxValue = 4)]
        public double TrailAtrMultiplier { get; set; }

        [Parameter("Maximum spread (pips)", DefaultValue = 5, MinValue = 0.1)]
        public double MaxSpread { get; set; }

        private ExponentialMovingAverage fastEma;
        private ExponentialMovingAverage slowEma;
        private RelativeStrengthIndex rsi;
        private AverageTrueRange atr;

        private DateTime tradingDay;
        private double dayStartEquity;
        private int tradesToday;

        private readonly Dictionary<long, double> initialRiskPips =
            new Dictionary<long, double>();

        private const string BotLabel = "GoldSniperX";

        protected override void OnStart()
        {
            fastEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastPeriod);
            slowEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SlowPeriod);
            rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            tradingDay = Server.Time.Date;
            dayStartEquity = Account.Equity;
            tradesToday = 0;

            foreach (var position in Positions.FindAll(BotLabel, SymbolName))
            {
                if (position.StopLoss.HasValue)
                    initialRiskPips[position.Id] = Math.Abs(position.EntryPrice - position.StopLoss.Value) / Symbol.PipSize;
            }

            Print("GoldSniperX started on {0}. Lots: {1}", SymbolName, Lots);
        }

        protected override void OnBar()
        {
            ResetTradingDayIfNeeded();

            if (Bars.Count < Math.Max(SlowPeriod, 50) + 5)
                return;

            if (IsDailyLossLimitReached() || tradesToday >= MaxTradesPerDay)
                return;

            if (Positions.FindAll(BotLabel, SymbolName).Length > 0)
                return;

            double spreadPips = Symbol.Spread / Symbol.PipSize;
            if (spreadPips > MaxSpread)
                return;

            double close = Bars.ClosePrices.Last(1);
            double fast = fastEma.Result.Last(1);
            double slow = slowEma.Result.Last(1);
            double momentum = rsi.Result.Last(1);
            double atrPrice = atr.Result.Last(1);

            double stopPips = atrPrice * AtrMultiplier / Symbol.PipSize;

            if (stopPips <= 0 || double.IsNaN(stopPips) || double.IsInfinity(stopPips))
                return;

            TradeType? direction = null;

            if (fast > slow && close > fast && momentum > 52)
                direction = TradeType.Buy;
            else if (fast < slow && close < fast && momentum < 48)
                direction = TradeType.Sell;

            if (!direction.HasValue)
                return;

            double volume = Symbol.QuantityToVolumeInUnits(Lots);
            volume = Symbol.NormalizeVolumeInUnits(volume, RoundingMode.Down);

            if (volume < Symbol.VolumeInUnitsMin || volume > Symbol.VolumeInUnitsMax)
                return;

            double takeProfitPips = stopPips * RewardRisk;

            TradeResult result = ExecuteMarketOrder(
                direction.Value, SymbolName, volume, BotLabel, stopPips, takeProfitPips);

            if (result.IsSuccessful)
            {
                tradesToday++;

                if (result.Position != null)
                    initialRiskPips[result.Position.Id] = stopPips;

                Print("{0} opened. Lots: {1}, SL: {2}, TP: {3}",
                    direction.Value, Lots, stopPips, takeProfitPips);
            }
            else
                Print("Order failed: {0}", result.Error);
        }

        protected override void OnTick()
        {
            ResetTradingDayIfNeeded();

            if (IsDailyLossLimitReached())
                return;

            foreach (var position in Positions.FindAll(BotLabel, SymbolName))
                ManagePosition(position);
        }

        private void ManagePosition(Position position)
        {
            double riskPips;

            if (!initialRiskPips.TryGetValue(position.Id, out riskPips) || riskPips <= 0)
                return;

            double profitPips = position.Pips;

            if (profitPips < riskPips * BreakEvenTrigger)
                return;

            double newStop;

            if (position.TradeType == TradeType.Buy)
            {
                newStop = position.EntryPrice;

                if (profitPips >= riskPips * TrailingStart)
                    newStop = Math.Max(newStop,
                        Symbol.Bid - atr.Result.LastValue * TrailAtrMultiplier);

                if (!position.StopLoss.HasValue ||
                    newStop > position.StopLoss.Value + Symbol.PipSize)
                    ModifyPosition(position, newStop, position.TakeProfit, ProtectionType.Absolute);
            }
            else
            {
                newStop = position.EntryPrice;

                if (profitPips >= riskPips * TrailingStart)
                    newStop = Math.Min(newStop,
                        Symbol.Ask + atr.Result.LastValue * TrailAtrMultiplier);

                if (!position.StopLoss.HasValue ||
                    newStop < position.StopLoss.Value - Symbol.PipSize)
                    ModifyPosition(position, newStop, position.TakeProfit, ProtectionType.Absolute);
            }
        }

        private void ResetTradingDayIfNeeded()
        {
            if (Server.Time.Date == tradingDay)
                return;

            tradingDay = Server.Time.Date;
            dayStartEquity = Account.Equity;
            tradesToday = 0;
        }

        private bool IsDailyLossLimitReached()
        {
            return Account.Equity <= dayStartEquity * (1 - DailyLossLimit / 100.0);
        }
    }
}
