using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using HelloOtp.Core;

namespace HelloOtp;

/// <summary>One line in the code panel.</summary>
public sealed class CodeRow : INotifyPropertyChanged
{
    private const int ExpiringSeconds = 5;

    private readonly OtpAccount _account;
    private long _step = -1;
    private string _display = "";
    private double _fraction;
    private bool _expiring;

    internal CodeRow(OtpAccount account)
    {
        _account = account;
        Title = account.Issuer.Length > 0 ? account.Issuer : account.Label;
        Subtitle = account.Issuer.Length > 0 ? account.Label : "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }
    public string Subtitle { get; }
    public string Code { get; private set; } = "";

    public string Display
    {
        get => _display;
        private set => Change(ref _display, value);
    }

    public double Fraction
    {
        get => _fraction;
        private set => Change(ref _fraction, value);
    }

    public bool Expiring
    {
        get => _expiring;
        private set => Change(ref _expiring, value);
    }

    public bool Matches(string filter) =>
        Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public void Update(long unixMilliseconds)
    {
        var seconds = unixMilliseconds / 1000;
        var step = Totp.Step(seconds, _account.Period);
        if (step != _step)
        {
            _step = step;
            Code = _account.CodeAt(seconds);
            Display = Group(Code);
        }

        var periodMs = _account.Period * 1000L;
        Fraction = (periodMs - unixMilliseconds % periodMs) / (double)periodMs;
        Expiring = Totp.SecondsRemaining(seconds, _account.Period) <= ExpiringSeconds;
    }

    private static string Group(string code) => code.Length == 8
        ? code[..4] + " " + code[4..]
        : code[..3] + " " + code[3..];

    private void Change<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A small countdown circle.</summary>
public sealed class RingElement : FrameworkElement
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(RingElement),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(RingElement),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext context)
    {
        const double thickness = 2.5;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - thickness / 2;
        if (radius <= 0)
            return;

        var pen = new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (Fraction >= 0.999)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = Fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var stream = geometry.Open())
        {
            stream.BeginFigure(start, isFilled: false, isClosed: false);
            stream.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }
}
