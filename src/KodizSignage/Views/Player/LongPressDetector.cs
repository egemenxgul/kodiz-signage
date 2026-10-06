using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace KodizSignage.Views.Player;

/// <summary>
/// Holding a finger, pen or the mouse still on the player for <see cref="HoldTime"/> raises
/// <see cref="Completed"/>. A ring fills up under the finger so people see that something happens.
/// </summary>
internal sealed class LongPressDetector
{
    public static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(2);
    private const double MaxMove = 40;
    private const double RingSize = 120;

    private readonly FrameworkElement _surface;
    private readonly Canvas _layer;
    private readonly Ellipse _track;
    private readonly Ellipse _ring;
    private Point _start;
    private object? _device;
    private bool _active;

    public LongPressDetector(FrameworkElement surface, Canvas layer)
    {
        _surface = surface;
        _layer = layer;
        _track = new Ellipse { Width = RingSize, Height = RingSize, Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), StrokeThickness = 10 };
        _ring = new Ellipse
        {
            Width = RingSize,
            Height = RingSize,
            Stroke = Brushes.White,
            StrokeThickness = 10,
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-90),
        };
        _layer.Children.Add(_track);
        _layer.Children.Add(_ring);
        Hide();

        Stylus.SetIsPressAndHoldEnabled(surface, false); // No right-click square on touch screens.
        surface.PreviewTouchDown += (_, e) => Begin(e.TouchDevice, e.GetTouchPoint(_layer).Position);
        surface.PreviewTouchMove += (_, e) => Move(e.TouchDevice, e.GetTouchPoint(_layer).Position);
        surface.PreviewTouchUp += (_, e) => End(e.TouchDevice);
        surface.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.StylusDevice is null)
            {
                Begin(e.MouseDevice, e.GetPosition(_layer));
            }
        };
        surface.PreviewMouseMove += (_, e) =>
        {
            if (e.StylusDevice is null)
            {
                Move(e.MouseDevice, e.GetPosition(_layer));
            }
        };
        surface.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.StylusDevice is null)
            {
                End(e.MouseDevice);
            }
        };
        surface.LostMouseCapture += (_, _) => Cancel();
    }

    public bool IsEnabled { get; set; } = true;

    public event EventHandler? Completed;

    private void Begin(object device, Point point)
    {
        if (!IsEnabled || _active)
        {
            return;
        }

        _active = true;
        _device = device;
        _start = point;
        Canvas.SetLeft(_track, point.X - RingSize / 2);
        Canvas.SetTop(_track, point.Y - RingSize / 2);
        Canvas.SetLeft(_ring, point.X - RingSize / 2);
        Canvas.SetTop(_ring, point.Y - RingSize / 2);
        _track.Visibility = _ring.Visibility = Visibility.Visible;

        // Dash length in stroke-thickness units: circumference / thickness.
        var circumference = Math.PI * (RingSize - _ring.StrokeThickness) / _ring.StrokeThickness;
        _ring.StrokeDashArray = new DoubleCollection { circumference, circumference };
        var fill = new DoubleAnimation(circumference, 0, HoldTime);
        fill.Completed += (_, _) =>
        {
            if (_active && ReferenceEquals(_device, device))
            {
                Hide();
                Completed?.Invoke(this, EventArgs.Empty);
            }
        };
        _ring.BeginAnimation(Shape.StrokeDashOffsetProperty, fill);
    }

    private void Move(object device, Point point)
    {
        if (_active && ReferenceEquals(device, _device) && (point - _start).Length > MaxMove)
        {
            Cancel();
        }
    }

    private void End(object device)
    {
        if (ReferenceEquals(device, _device))
        {
            Cancel();
        }
    }

    private void Cancel()
    {
        if (_active)
        {
            Hide();
        }
    }

    private void Hide()
    {
        _active = false;
        _device = null;
        _ring.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        _track.Visibility = _ring.Visibility = Visibility.Collapsed;
    }
}
