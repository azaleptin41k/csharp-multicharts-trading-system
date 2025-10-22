// Файл: Sig_Volumatic_VIDYA_Reversal.cs
using System;
using PowerLanguage.Strategy;
using PowerLanguage.Indicator;
using System.Diagnostics.Contracts;

namespace PowerLanguage.Strategy
{
    [IOGMode(IOGMode.Enabled)]
    public class Sig_Volumatic_VIDYA_Reversal : SignalObject
    {
        private int LOOKBACK_PERIOD;

        [Input("Источник цены")] public EPriceSource Price_Source { get; set; }
        [Input("Размер позиции")] public int TradeSize { get; set; }
        [Input("Период ATR")] public int AtrPeriod { get; set; }
        [Input("Длина VIDYA")] public int VidyaLength { get; set; }
        [Input("Моментум VIDYA (CMO)")] public int VidyaMomentum { get; set; }
        [Input("Множитель дистанции ATR")] public double BandDistance { get; set; }
        [Input("Тип сглаживающей MA")] public EMAType SmoothingType { get; set; }
        [Input("Длина сглаживающей MA")] public int SmoothingLength { get; set; }
        [Input("JMA Phase")] public double JmaPhase { get; set; }
        [Input("JMA Power")] public double JmaPower { get; set; }

        private IOrderMarket buyOrder, sellOrder, closeLongOrder, closeShortOrder;
        private ISeries<double> m_price;
        private VariableSeries<double> m_vidya_raw, m_vidya_smoothed;
        private VariableSeries<bool> m_is_trend_up;
        private VariableSeries<double> m_cmo_values, m_atr_values, m_true_range;
        private VariableSeries<double> m_hl2, m_hlc3, m_ohlc4;
        private VariableSeries<double> upper_band_series, lower_band_series;
        private VariableSeries<double> ema_storage_atr, ema_storage_smooth, ema_storage_kama;
        private VariableSeries<double> hma_wma_half, hma_wma_full, hma_wma_diff;
        private VariableSeries<double> kama_er, kama_sc;
        private VariableSeries<double> jma_series, volty, det0, det1;

        public Sig_Volumatic_VIDYA_Reversal(object _ctx) : base(_ctx)
        {
            Price_Source = EPriceSource.Close; TradeSize = 1; AtrPeriod = 200; VidyaLength = 10; VidyaMomentum = 20;
            BandDistance = 2.0; SmoothingType = EMAType.JMA; SmoothingLength = 15;
            JmaPhase = 50; JmaPower = 2;
        }

        protected override void Create()
        {
            LOOKBACK_PERIOD = Math.Max(VidyaMomentum, AtrPeriod) + SmoothingLength + 10;
            buyOrder = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "V_VIDYA_Buy", EOrderAction.Buy));
            sellOrder = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "V_VIDYA_Sell", EOrderAction.SellShort));
            closeLongOrder = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "V_VIDYA_ExitL", EOrderAction.Sell));
            closeShortOrder = OrderCreator.MarketThisBar(new SOrderParameters(Contracts.Default, "V_VIDYA_ExitS", EOrderAction.BuyToCover));
            m_vidya_raw = new VariableSeries<double>(this); m_vidya_smoothed = new VariableSeries<double>(this);
            m_is_trend_up = new VariableSeries<bool>(this); m_cmo_values = new VariableSeries<double>(this);
            m_atr_values = new VariableSeries<double>(this); m_true_range = new VariableSeries<double>(this);
            m_hl2 = new VariableSeries<double>(this); m_hlc3 = new VariableSeries<double>(this); m_ohlc4 = new VariableSeries<double>(this);
            upper_band_series = new VariableSeries<double>(this); lower_band_series = new VariableSeries<double>(this);
            ema_storage_atr = new VariableSeries<double>(this); ema_storage_smooth = new VariableSeries<double>(this);
            ema_storage_kama = new VariableSeries<double>(this); hma_wma_half = new VariableSeries<double>(this);
            hma_wma_full = new VariableSeries<double>(this); hma_wma_diff = new VariableSeries<double>(this);
            kama_er = new VariableSeries<double>(this); kama_sc = new VariableSeries<double>(this);
            jma_series = new VariableSeries<double>(this); volty = new VariableSeries<double>(this);
            det0 = new VariableSeries<double>(this); det1 = new VariableSeries<double>(this);
        }

        protected override void StartCalc()
        {
            switch (Price_Source)
            {
                case EPriceSource.Open: m_price = Bars.Open; break;
                case EPriceSource.High: m_price = Bars.High; break;
                case EPriceSource.Low: m_price = Bars.Low; break;
                case EPriceSource.HL2: m_price = m_hl2; break;
                case EPriceSource.HLC3: m_price = m_hlc3; break;
                case EPriceSource.OHLC4: m_price = m_ohlc4; break;
                default: m_price = Bars.Close; break;
            }
        }

        protected override void CalcBar()
        {
            m_hl2.Value = (Bars.High[0] + Bars.Low[0]) / 2;
            m_hlc3.Value = (Bars.High[0] + Bars.Low[0] + Bars.Close[0]) / 3;
            m_ohlc4.Value = (Bars.Open[0] + Bars.High[0] + Bars.Low[0] + Bars.Close[0]) / 4;

            if (Bars.CurrentBar < LOOKBACK_PERIOD) return;

            m_true_range.Value = Math.Max(Bars.High[0], Bars.Close[1]) - Math.Min(Bars.Low[0], Bars.Close[1]);
            m_atr_values.Value = CalcEMA(m_true_range, AtrPeriod, ema_storage_atr);
            m_cmo_values.Value = CalcCMO(m_price, VidyaMomentum);

            double abs_cmo = Math.Abs(m_cmo_values[0]);
            double alpha_vidya = 2.0 / (VidyaLength + 1);
            double k = alpha_vidya * abs_cmo / 100.0;

            if (Bars.CurrentBar == LOOKBACK_PERIOD) m_vidya_raw.Value = m_price[0];
            else m_vidya_raw.Value = k * m_price[0] + (1 - k) * m_vidya_raw[1];

            switch (SmoothingType)
            {
                case EMAType.EMA: m_vidya_smoothed.Value = CalcEMA(m_vidya_raw, SmoothingLength, ema_storage_smooth); break;
                case EMAType.HMA: m_vidya_smoothed.Value = CalcHMA(m_vidya_raw, SmoothingLength); break;
                case EMAType.JMA: m_vidya_smoothed.Value = CalcJMA(m_vidya_raw, SmoothingLength, JmaPhase, JmaPower); break;
                case EMAType.KAMA: m_vidya_smoothed.Value = CalcKAMA(m_vidya_raw, SmoothingLength, 2, 30); break;
            }

            upper_band_series.Value = m_vidya_smoothed[0] + (m_atr_values[0] * BandDistance);
            lower_band_series.Value = m_vidya_smoothed[0] - (m_atr_values[0] * BandDistance);

            if (Bars.CurrentBar == LOOKBACK_PERIOD) { m_is_trend_up.Value = true; }
            else
            {
                bool was_trend_up = m_is_trend_up[1];
                if (was_trend_up)
                {
                    m_is_trend_up.Value = m_price[0] >= lower_band_series[0];
                }
                else
                {
                    m_is_trend_up.Value = m_price[0] > upper_band_series[0];
                }
            }

            bool trend_changed_to_up = m_is_trend_up.Value && !m_is_trend_up[1];
            bool trend_changed_to_down = !m_is_trend_up.Value && m_is_trend_up[1];

            if (StrategyInfo.MarketPosition == 0)
            {
                if (Bars.CurrentBar == LOOKBACK_PERIOD)
                {
                    if (m_is_trend_up.Value) buyOrder.Send(TradeSize);
                    else sellOrder.Send(TradeSize);
                }
                else
                {
                    if (trend_changed_to_up) buyOrder.Send(TradeSize);
                    else if (trend_changed_to_down) sellOrder.Send(TradeSize);
                }
            }
            else if (StrategyInfo.MarketPosition > 0 && trend_changed_to_down)
            {
                closeLongOrder.Send(StrategyInfo.MarketPosition);
                sellOrder.Send(TradeSize);
            }
            else if (StrategyInfo.MarketPosition < 0 && trend_changed_to_up)
            {
                closeShortOrder.Send(Math.Abs(StrategyInfo.MarketPosition));
                buyOrder.Send(TradeSize);
            }
        }

        #region Calculation Helpers
        private double CalcEMA(ISeries<double> series, int period, VariableSeries<double> storage) { if (Bars.CurrentBar == LOOKBACK_PERIOD) { storage.Value = series[0]; } else { storage.Value = series[0] * (2.0 / (period + 1)) + storage[1] * (1 - (2.0 / (period + 1))); } return storage.Value; }
        private double CalcWMA(ISeries<double> series, int period) { if (Bars.CurrentBar < period) return series[0]; double weightedSum = 0; double weightSum = 0; for (int i = 0; i < period; i++) { double weight = period - i; weightedSum += series[i] * weight; weightSum += weight; } return weightSum != 0 ? weightedSum / weightSum : series[0]; }
        private double CalcHMA(ISeries<double> series, int period) { int halfLength = period / 2; int sqrtLength = (int)Math.Sqrt(period); hma_wma_half.Value = CalcWMA(series, halfLength); hma_wma_full.Value = CalcWMA(series, period); hma_wma_diff.Value = 2 * hma_wma_half.Value - hma_wma_full.Value; return CalcWMA(hma_wma_diff, sqrtLength); }
        private double CalcKAMA(ISeries<double> series, int period, int fast, int slow) { if (Bars.CurrentBar == LOOKBACK_PERIOD) { ema_storage_kama.Value = series[0]; } if (Bars.CurrentBar < period) return series[0]; double change = Math.Abs(series[0] - series[period]); double volatility = 0; for (int i = 0; i < period; i++) { volatility += Math.Abs(series[i] - series[i + 1]); } kama_er.Value = (volatility != 0) ? change / volatility : 0; double fastAlpha = 2.0 / (fast + 1); double slowAlpha = 2.0 / (slow + 1); kama_sc.Value = Math.Pow(kama_er.Value * (fastAlpha - slowAlpha) + slowAlpha, 2); ema_storage_kama.Value = ema_storage_kama[1] + kama_sc.Value * (series[0] - ema_storage_kama[1]); return ema_storage_kama.Value; }
        private double CalcJMA(ISeries<double> series, int period, double phase, double power) { if (Bars.CurrentBar < LOOKBACK_PERIOD) { jma_series.Value = series[0]; det0.Value = 0; return series[0]; } double phaseRatio = (phase < -100) ? 0.5 : (phase > 100) ? 2.5 : phase / 100.0 + 1.5; double beta = 0.45 * (period - 1) / (0.45 * (period - 1) + 2); double alpha = Math.Pow(beta, power); double vsum = 0; for (int i = 0; i < Math.Min(10, Bars.CurrentBar - 1); i++) { vsum += Math.Abs(series[i] - series[i + 1]); } volty.Value = vsum / Math.Min(10, Bars.CurrentBar - 1); det0.Value = (1 - alpha) * (series[0] - jma_series[1]) + alpha * det0[1]; jma_series.Value = jma_series[1] + det0.Value; double upperBand = jma_series.Value + phaseRatio * volty.Value; double lowerBand = jma_series.Value - phaseRatio * volty.Value; if (series[0] > upperBand) { det1.Value = (series[0] - upperBand); } else if (series[0] < lowerBand) { det1.Value = (series[0] - lowerBand); } else { det1.Value = 0; } if (Math.Abs(det1.Value) > Math.Abs(det0.Value)) { jma_series.Value = jma_series[1] + det1.Value; } return jma_series.Value; }
        private double CalcCMO(ISeries<double> series, int period) { if (Bars.CurrentBar < period) return 0; double sum_pos_momentum = 0; double sum_neg_momentum = 0; for (int i = 0; i < period; i++) { double change = series[i] - series[i + 1]; if (change > 0) sum_pos_momentum += change; else sum_neg_momentum -= change; } if (sum_pos_momentum + sum_neg_momentum == 0) return 0; return 100 * (sum_pos_momentum - sum_neg_momentum) / (sum_pos_momentum + sum_neg_momentum); }
        #endregion
    }
}