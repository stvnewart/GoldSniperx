
using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class GoldSniperX : Robot
    {
        [Parameter("Risk per trade (%)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 10)]
        public double RiskPercent { get; set; }

        [Parameter("Daily loss limit (%)", DefaultValue = 10, MinValue = 1, MaxValue = 10)]
        public double DailyLossLimit { get; set; }

        [Parameter("Maximum trades per day", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Fast EMA", DefaultValue = 20)]
        public int FastPeriod { get; set; }

        [Parameter("Slow EMA", DefaultValue = 50)]
        public int SlowPeriod { get; set; }

        [Parameter("RSI period", DefaultValue = 14)]
        public int RsiPeriod { get; set; }

        [Parameter("ATR period", DefaultValue = 14)]
        public int AtrPeriod { get; set; }

        [Parameter("ATR stop multiplier", DefaultValue = 2.0)]
        public double AtrMultiplier { get; set; }

        [Parameter("Reward / risk", DefaultValue = 2.0)]
        public double RewardRisk { get; set; }

        [Parameter("Maximum spread (pips)", DefaultValue = 5)]
        public double MaxSpread { get; set; }

        private ExponentialMovingAverage fastEma;
        private ExponentialMovingAverage slowEma;
        private RelativeStrengthIndex rsi;
        private AverageTrueRange atr;

        private DateTime tradingDay;
        private double dayStartEquity;
        private int tradesToday;
        private const string BotLabel = "GoldSniperX";

        protected override void OnStart()
        {
            fastEma = Indicators.ExponentialMovingAverage(
                Bars.ClosePrices, FastPeriod);

            slowEma = Indicators.ExponentialMovingAverage(
                Bars.ClosePrices, SlowPeriod);

            rsi = Indicators.RelativeStrengthIndex(
                Bars.ClosePrices, RsiPeriod);

            atr = Indicators.AverageTrueRange(
                AtrPeriod, MovingAverageType.Exponential);

            tradingDay = Server.Time.Date;
            dayStartEquity = Account.Equity;
            tradesToday = 0;

            Print("GoldSniperX started on {0}", SymbolName);
            Print("Risk: {0}% | Daily limit: {1}%",
                RiskPercent, DailyLossLimit);
        }

        protected override void OnBar()
        {
            if (Bars.Count < 210)
                return;

            if (Server.Time.Date != tradingDay)
            {
                tradingDay = Server.Time.Date;
                dayStartEquity = Account.Equity;
                tradesToday = 0;
            }

            if (Account.Equity <=
                dayStartEquity * (1 - DailyLossLimit / 100))
            {
                Print("Daily loss limit reached. Trading stopped.");
                return;
            }

            if (tradesToday >= MaxTradesPerDay)
                return;

            if (Positions.FindAll(BotLabel, SymbolName).Length > 0)
                return;

            double spread = Symbol.Spread / Symbol.PipSize;

            if (spread > MaxSpread)
                return;

            double close = Bars.ClosePrices.Last(1);
            double fast = fastEma.Result.Last(1);
            double slow = slowEma.Result.Last(1);
            double momentum = rsi.Result.Last(1);
            double volatility = atr.Result.Last(1);

            double stopPips =
                volatility * AtrMultiplier / Symbol.PipSize;

            if (stopPips <= 0)
                return;

            TradeType? direction = null;

            if (fast > slow && close > fast && momentum > 52)
                direction = TradeType.Buy;

            if (fast < slow && close < fast && momentum < 48)
                direction = TradeType.Sell;

            if (direction == null)
                return;

            double riskAmount =
                Account.Balance * RiskPercent / 100.0;

            double volume = Symbol.VolumeForFixedRisk(
                riskAmount, stopPips, RoundingMode.Down);

            volume = Symbol.NormalizeVolumeInUnits(
                volume, RoundingMode.Down);

            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("Trade skipped: minimum volume would exceed risk.");
                return;
            }

            if (volume > Symbol.VolumeInUnitsMax)
                volume = Symbol.VolumeInUnitsMax;

            double takeProfitPips = stopPips * RewardRisk;

            TradeResult result = ExecuteMarketOrder(
                direction.Value,
                SymbolName,
                volume,
                BotLabel,
                stopPips,
                takeProfitPips
            );

            if (result.IsSuccessful)
            {
                tradesToday++;

                Print("{0} opened. Volume: {1}, SL: {2}, TP: {3}",
                    direction.Value, volume, stopPips, takeProfitPips);
            }
            else
            {
                Print("Order failed: {0}", result.Error);
            }
        }
    }
}
