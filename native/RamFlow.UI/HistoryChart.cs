using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace RamFlow.UI;

/// <summary>A bounded, passive history of engine snapshots; null samples leave gaps.</summary>
public sealed class HistoryChart : FrameworkElement
{
    private const int Capacity = 180;
    private readonly List<(DateTimeOffset Time, double? Value)> _samples = new();
    private readonly string _title;
    private readonly Brush _stroke;
    private readonly string _unit;
    private double _maximum = 100;

    public HistoryChart(string title, Brush stroke, string unit = "%")
    {
        _title = title;
        _stroke = stroke;
        _unit = unit;
        Height = 138;
        MinWidth = 200;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AutomationProperties.SetName(this, title + " · 최근 6분 기록");
        ToolTip = "최근 최대 180개 샘플 · 같은 시각의 샘플은 중복 기록하지 않습니다.";
    }

    public void AddSample(DateTimeOffset time, double? value, double maximum = 100)
    {
        Dispatcher.VerifyAccess();
        if (_samples.Count > 0 && time <= _samples[_samples.Count - 1].Time) return;
        if (value.HasValue && !double.IsFinite(value.Value)) value = null;
        _maximum = double.IsFinite(maximum) && maximum > 0 ? maximum : 100;
        _samples.Add((time, value.HasValue ? Math.Clamp(value.Value, 0, _maximum) : null));
        if (_samples.Count > Capacity) _samples.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 100 || ActualHeight < 100) return;
        double? latest = _samples.Count > 0 ? _samples[_samples.Count - 1].Value : null;
        string current = latest.HasValue ? $"{latest.Value:0.0}{_unit}" : "측정 대기";
        FormattedText valueText = Text(current, latest.HasValue ? 18 : 12,
            latest.HasValue ? _stroke : UiTheme.Muted, true, latest.HasValue);
        dc.DrawText(valueText, new Point(Math.Max(14, ActualWidth - valueText.Width - 14), 6));
        dc.DrawEllipse(_stroke, null, new Point(16, 17), 3, 3);
        FormattedText titleText = Text(_title, 12.5, UiTheme.Text, true);
        titleText.MaxTextWidth = Math.Max(1, ActualWidth - valueText.Width - 54);
        titleText.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(titleText, new Point(27, 8));

        var plot = new Rect(38, 40, Math.Max(1, ActualWidth - 52), ActualHeight - 66);
        Brush gridBrush = UiTheme.Border.Clone();
        gridBrush.Opacity = 0.55;
        gridBrush.Freeze();
        var gridPen = new Pen(gridBrush, 0.75);
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Top + plot.Height * i / 4;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Write(dc, (_maximum * (4 - i) / 4).ToString("0", CultureInfo.CurrentCulture),
                10.5, UiTheme.Muted, new Point(4, y - 7), true);
        }
        if (_samples.Count == 0)
        {
            Write(dc, "첫 샘플을 기다리는 중", 12, UiTheme.Muted,
                new Point(plot.Left + 12, plot.Top + plot.Height / 2 - 8));
            return;
        }

        DateTimeOffset end = _samples[_samples.Count - 1].Time;
        DateTimeOffset start = end.AddMinutes(-6);
        Point Position(int index) => new(
            plot.Left + Math.Clamp((_samples[index].Time - start).TotalSeconds / 360, 0, 1) * plot.Width,
            plot.Bottom - _samples[index].Value.GetValueOrDefault() / _maximum * plot.Height);

        var line = new Pen(_stroke, 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        Brush fill;
        if (_stroke is SolidColorBrush solid)
        {
            var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(34, solid.Color.R, solid.Color.G, solid.Color.B), 0));
            fade.GradientStops.Add(new GradientStop(Color.FromArgb(2, solid.Color.R, solid.Color.G, solid.Color.B), 1));
            fill = fade;
        }
        else { fill = _stroke.Clone(); fill.Opacity = 0.1; }
        if (fill.CanFreeze) fill.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - 3, plot.Top - 3, plot.Width + 6, plot.Height + 6)));
        int cursor = 0;
        while (cursor < _samples.Count)
        {
            while (cursor < _samples.Count &&
                (!_samples[cursor].Value.HasValue || _samples[cursor].Time < start)) cursor++;
            int first = cursor;
            if (first == _samples.Count) break;
            cursor++;
            while (cursor < _samples.Count && _samples[cursor].Value.HasValue &&
                (_samples[cursor].Time - _samples[cursor - 1].Time).TotalSeconds <= 10) cursor++;
            int last = cursor - 1;
            var area = new StreamGeometry();
            using (StreamGeometryContext context = area.Open())
            {
                context.BeginFigure(new Point(Position(first).X, plot.Bottom), true, true);
                for (int i = first; i <= last; i++) context.LineTo(Position(i), true, false);
                context.LineTo(new Point(Position(last).X, plot.Bottom), true, false);
            }
            dc.DrawGeometry(fill, null, area);
            for (int i = first + 1; i <= last; i++) dc.DrawLine(line, Position(i - 1), Position(i));
            dc.DrawEllipse(_stroke, null, Position(last), 2.5, 2.5);
        }
        dc.Pop();
        Write(dc, start.ToLocalTime().ToString("HH:mm:ss"), 10.5, UiTheme.Muted,
            new Point(plot.Left, plot.Bottom + 6), true);
        FormattedText endText = Text(end.ToLocalTime().ToString("HH:mm:ss"), 10.5, UiTheme.Muted, false, true);
        dc.DrawText(endText, new Point(plot.Right - endText.Width, plot.Bottom + 6));
    }

    private FormattedText Text(string text, double size, Brush brush, bool strong = false, bool numeric = false) => new(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily(numeric ? "Segoe UI" : "Malgun Gothic"), FontStyles.Normal,
            strong ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private void Write(DrawingContext dc, string text, double size, Brush brush, Point point, bool numeric = false) =>
        dc.DrawText(Text(text, size, brush, false, numeric), point);
}
