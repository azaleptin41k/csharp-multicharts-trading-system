// Volumatic_VIDYA_Indicator.cs
using System;
using System.Drawing;
using System.Collections.Generic;
using PowerLanguage.Indicator;

namespace PowerLanguage.Indicator
{
    public enum EPriceSource { Close, Open, High, Low, HL2, HLC3, OHLC4 }
    public enum EMAType { EMA, HMA, JMA, KAMA }

    [SameAsSymbol(true)]
    public class Volumatic_VIDYA_Indicator : IndicatorObject
    {
        private class LiquidityLineData
        {
            public ITrendLineObject Line { get; set; }
            public double AssociatedVolume { get; set; }
        }

        private const int LINE_LIFETIME_BARS = 50;
        private int LOOKBACK_PERIOD;

        [Input("Источник цены")] public EPriceSource Price_Source { get; set; }
        [Input("Период ATR")] public int AtrPeriod { get; set; }
        [Input("Длина VIDYA")] public int VidyaLength { get; set; }
        [Input("Моментум VIDYA (CMO)")] public int VidyaMomentum { get; set; }
        [Input("Множитель дистанции ATR")] public double BandDistance { get; set; }
        [Input("Тип сглаживающей MA")] public EMAType SmoothingType { get; set; }
        [Input("Длина сглаживающей MA")] public int SmoothingLength { get; set; }
        [Input("JMA Phase")] public double JmaPhase { get; set; }
        [Input("JMA Power")] public double JmaPower { get; set; }
        [Input("Макс. кол-во отрезков линии")] public int MaxSegments { get; set; }
        [Input("Левый интервал для пивота")] public int PivotLeftBars { get; set; }
        [Input("Правый интервал для пивота")] public int PivotRightBars { get; set; }
        [Input("Макс. кол-во линий ликвидности")] public int MaxLiquidityLines { get; set; }

        private ISeries<double> m_price;
        private VariableSeries<double> m_vidya_raw, m_vidya_smoothed, m_trend_line_value;
        private VariableSeries<bool> m_is_trend_up;
        private VariableSeries<double> m_cmo_values, m_atr_values, m_true_range;
        private VariableSeries<double> m_hl2, m_hlc3, m_ohlc4;
        private readonly List<ITrendLineObject> m_trend_line_segments;
        private VariableSeries<double> upper_band_series, lower_band_series;
        private VariableSeries<double> ema_storage_atr, ema_storage_smooth, ema_storage_kama;
        private VariableSeries<double> hma_wma_half, hma_wma_full, hma_wma_diff;
        private VariableSeries<double> kama_er, kama_sc;
        private VariableSeries<double> jma_series, volty, det0, det1;

        private readonly List<LiquidityLineData> m_liquidity_lines_high;
        private readonly List<LiquidityLineData> m_liquidity_lines_low;
        private readonly List<ITextObject> m_volume_labels;

        public Volumatic_VIDYA_Indicator(object _ctx) : base(_ctx)
        {
            Price_Source = EPriceSource.Close; AtrPeriod = 200; VidyaLength = 10; VidyaMomentum = 20;
            BandDistance = 2.0; SmoothingType = EMAType.JMA; SmoothingLength = 15;
            JmaPhase = 50; JmaPower = 2;
            MaxSegments = 2000;
            PivotLeftBars = 3; PivotRightBars = 3; MaxLiquidityLines = 100;

            m_trend_line_segments = new List<ITrendLineObject>();
            m_liquidity_lines_high = new List<LiquidityLineData>();
            m_liquidity_lines_low = new List<LiquidityLineData>();
            m_volume_labels = new List<ITextObject>();
        }

        protected override void Create()
        {
            LOOKBACK_PERIOD = Math.Max(VidyaMomentum, AtrPeriod) + SmoothingLength + PivotLeftBars + PivotRightBars + 10;
            m_vidya_raw = new VariableSeries<double>(this); m_vidya_smoothed = new VariableSeries<double>(this);
            m_is_trend_up = new VariableSeries<bool>(this); m_trend_line_value = new VariableSeries<double>(this);
            m_cmo_values = new VariableSeries<double>(this); m_atr_values = new VariableSeries<double>(this);
            m_true_range = new VariableSeries<double>(this); m_hl2 = new VariableSeries<double>(this);
            m_hlc3 = new VariableSeries<double>(this); m_ohlc4 = new VariableSeries<double>(this);
            upper_band_series = new VariableSeries<double>(this); lower_band_series = new VariableSeries<double>(this);
            ema_storage_atr = new VariableSeries<double>(this); ema_storage_smooth = new VariableSeries<double>(this);
            ema_storage_kama = new VariableSeries<double>(this); hma_wma_half = new VariableSeries<double>(this);
            hma_wma_full = new VariableSeries<double>(this); hma_wma_diff = new VariableSeries<double>(this);
            kama_er = new VariableSeries<double>(this); kama_sc = new VariableSeries<double>(this);
            jma_series = new VariableSeries<double>(this); volty = new VariableSeries<double>(this);
            det0 = new VariableSeries<double>(this); det1 = new VariableSeries<double>(this);
        }

        protected override void Destroy()
        {
            ClearLines(m_liquidity_lines_low);
            ClearLines(m_liquidity_lines_high);
            foreach (var label in m_volume_labels) { if (label != null) label.Delete(); }
            m_volume_labels.Clear();
            foreach (var line in m_trend_line_segments) { if (line != null) line.Delete(); }
            m_trend_line_segments.Clear();
        }

        protected override void StartCalc()
        {
            switch (Price_Source) { case EPriceSource.Open: m_price = Bars.Open; break; case EPriceSource.High: m_price = Bars.High; break; case EPriceSource.Low: m_price = Bars.Low; break; case EPriceSource.HL2: m_price = m_hl2; break; case EPriceSource.HLC3: m_price = m_hlc3; break; case EPriceSource.OHLC4: m_price = m_ohlc4; break; default: m_price = Bars.Close; break; }
        }

        protected override void CalcBar()
        {
            if (Bars.CurrentBar < 1) return;

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
            switch (SmoothingType) { case EMAType.EMA: m_vidya_smoothed.Value = CalcEMA(m_vidya_raw, SmoothingLength, ema_storage_smooth); break; case EMAType.HMA: m_vidya_smoothed.Value = CalcHMA(m_vidya_raw, SmoothingLength); break; case EMAType.JMA: m_vidya_smoothed.Value = CalcJMA(m_vidya_raw, SmoothingLength, JmaPhase, JmaPower); break; case EMAType.KAMA: m_vidya_smoothed.Value = CalcKAMA(m_vidya_raw, SmoothingLength, 2, 30); break; }
            upper_band_series.Value = m_vidya_smoothed[0] + (m_atr_values[0] * BandDistance);
            lower_band_series.Value = m_vidya_smoothed[0] - (m_atr_values[0] * BandDistance);
            if (Bars.CurrentBar == LOOKBACK_PERIOD) { m_is_trend_up.Value = true; } else { bool was_trend_up = m_is_trend_up[1]; if (was_trend_up) { m_is_trend_up.Value = m_price[1] >= lower_band_series[1]; } else { m_is_trend_up.Value = m_price[1] > upper_band_series[1]; } }
            if (m_is_trend_up.Value) m_trend_line_value.Value = lower_band_series[0]; else m_trend_line_value.Value = upper_band_series[0];

            if (Bars.Status == EBarState.Close)
            {
                if (Bars.CurrentBar <= LOOKBACK_PERIOD + 2) return;

                if (m_is_trend_up[1] == m_is_trend_up[2])
                {
                    ChartPoint start_point = new ChartPoint(Bars.Time[2], m_trend_line_value[2]);
                    ChartPoint end_point = new ChartPoint(Bars.Time[1], m_trend_line_value[1]);
                    Color line_color = m_is_trend_up[1] ? Color.Lime : Color.Red;
                    ITrendLineObject new_segment = DrwTrendLine.Create(start_point, end_point);
                    new_segment.Color = line_color; new_segment.Size = 2;
                    m_trend_line_segments.Add(new_segment);
                }
                CleanupOldLines(m_trend_line_segments, MaxSegments);

                if (m_is_trend_up[1] != m_is_trend_up[2])
                {
                    if (m_is_trend_up[1]) ClearLines(m_liquidity_lines_high);
                    else ClearLines(m_liquidity_lines_low);
                }

                ExtendLiquidityLines(m_liquidity_lines_low, false);
                ExtendLiquidityLines(m_liquidity_lines_high, true);

                bool is_pivot_high = CheckPivot(true, PivotLeftBars, PivotRightBars);
                bool is_pivot_low = CheckPivot(false, PivotLeftBars, PivotRightBars);

                if (is_pivot_low && Bars.Low[PivotRightBars] > m_trend_line_value[PivotRightBars])
                {
                    DrawLiquidityLine(false, PivotRightBars);
                }
                if (is_pivot_high && Bars.High[PivotRightBars] < m_trend_line_value[PivotRightBars])
                {
                    DrawLiquidityLine(true, PivotRightBars);
                }
                CleanupOldLines(m_liquidity_lines_low, MaxLiquidityLines);
                CleanupOldLines(m_liquidity_lines_high, MaxLiquidityLines);
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

        #region New Feature Helpers
        private bool CheckPivot(bool isHigh, int left, int right)
        {
            if (Bars.CurrentBar < left + right + 1) return false;
            ISeries<double> series = isHigh ? Bars.High : Bars.Low;
            for (int i = 1; i <= left; i++)
            {
                if (isHigh ? series[right] <= series[right + i] : series[right] >= series[right + i]) return false;
            }
            for (int i = 1; i <= right; i++)
            {
                if (isHigh ? series[right] < series[right - i] : series[right] > series[right - i]) return false;
            }
            return true;
        }

        private void DrawLiquidityLine(bool isHigh, int pivotBarIndex)
        {
            int pivot_period = PivotLeftBars + PivotRightBars + 1;
            double pivot_volume = 0;
            for (int i = 0; i < pivot_period; i++) pivot_volume += Bars.Volume[pivotBarIndex + i];
            double avg_pivot_volume = pivot_volume / pivot_period;
            double priceLevel = isHigh ? Bars.High[pivotBarIndex] : Bars.Low[pivotBarIndex];

            var startPoint = new ChartPoint(Bars.Time[pivotBarIndex], priceLevel);
            int end_bar_index = Math.Max(0, pivotBarIndex - 5);
            var endPoint = new ChartPoint(Bars.Time[end_bar_index], priceLevel);
            var line = DrwTrendLine.Create(startPoint, endPoint);
            line.Color = Color.FromArgb(120, isHigh ? Color.Red : Color.Lime);
            line.Size = 1;

            var lineData = new LiquidityLineData { Line = line, AssociatedVolume = avg_pivot_volume };
            if (isHigh) m_liquidity_lines_high.Add(lineData);
            else m_liquidity_lines_low.Add(lineData);
        }

        private void ExtendLiquidityLines(List<LiquidityLineData> lineList, bool isHigh)
        {
            for (int i = lineList.Count - 1; i >= 0; i--)
            {
                LiquidityLineData data = lineList[i];
                if (data == null || data.Line == null) { lineList.RemoveAt(i); continue; }

                bool is_recent_enough = (Bars.CurrentBar - data.Line.Begin.BarNumber) < LINE_LIFETIME_BARS;
                double line_price = data.Line.Begin.Price;

                double trigger_price = m_trend_line_value[1];
                double trigger_price_prev = m_trend_line_value[2];

                bool price_cross = isHigh ? (trigger_price < line_price && trigger_price_prev >= line_price) : (trigger_price > line_price && trigger_price_prev <= line_price);

                if (price_cross && is_recent_enough)
                {
                    data.Line.End = new ChartPoint(Bars.Time[1], line_price);

                    double y_pos;
                    double y_offset = m_atr_values[1] * 0.4;

                    if (isHigh)
                    {
                        y_pos = trigger_price_prev + y_offset;
                    }
                    else
                    {
                        y_pos = trigger_price_prev - y_offset;
                    }

                    var label_location = new ChartPoint(Bars.Time[1], y_pos);
                    var label_text = data.AssociatedVolume.ToString("F0");
                    ITextObject label = DrwText.Create(label_location, label_text);

                    label.Color = isHigh ? Color.Red : Color.Lime;
                    label.Size = 7;
                    m_volume_labels.Add(label);

                    lineList.RemoveAt(i);
                }
            }
        }

        private void CleanupOldLines(List<ITrendLineObject> lineList, int maxCount) { while (lineList.Count > maxCount) { if (lineList[0] != null) lineList[0].Delete(); lineList.RemoveAt(0); } }
        private void CleanupOldLines(List<LiquidityLineData> lineList, int maxCount) { while (lineList.Count > maxCount) { if (lineList[0] != null && lineList[0].Line != null) lineList[0].Line.Delete(); lineList.RemoveAt(0); } }
        private void ClearLines(List<LiquidityLineData> lineList) { foreach (var data in lineList) { if (data != null && data.Line != null) data.Line.Delete(); } lineList.Clear(); }
        #endregion
    }
}