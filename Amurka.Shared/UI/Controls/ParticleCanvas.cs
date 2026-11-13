using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Amurka.Shared.UI.Controls;

public sealed class ParticleCanvas : FrameworkElement
{
    private struct Particle
    {
        public double X;
        public double Y;
        public double Size;
        public double SpeedX;
        public double SpeedY;
        public double Opacity;
    }

    private readonly VisualCollection _children;
    private readonly DrawingVisual _particleVisual = new();

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    private static readonly int HardwareTier = RenderCapability.Tier >> 16;
    private static readonly int ParticleCount = HardwareTier == 0 ? 16 : (HardwareTier == 1 ? 25 : 30);
    private readonly Particle[] _particles = new Particle[ParticleCount];
    private bool _isInitialized;
    private bool _isHooked;
    private bool _isWindowActive = true;
    private long _lastFrameTimestamp;

    private static readonly long ActiveIntervalTicks = (long)(Stopwatch.Frequency * 0.0166); // ~60 FPS
    private static readonly long InactiveIntervalTicks = (long)(Stopwatch.Frequency * 0.050); // 20 FPS при неактивном окне (энергосбережение)

    private static readonly SolidColorBrush[] CachedBrushes = CreateBrushCache();

    private static SolidColorBrush[] CreateBrushCache()
    {
        var brushes = new SolidColorBrush[256];
        for (int i = 0; i < 256; i++)
        {
            // Точный цвет --green: #00C878 (RGB: 0, 200, 120) с сайта amurkapve.ru
            var brush = new SolidColorBrush(Color.FromArgb((byte)i, 0, 200, 120));
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }

    private Window? _parentWindow;

    public ParticleCanvas()
    {
        _children = new VisualCollection(this) { _particleVisual };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _parentWindow = Window.GetWindow(this);
        if (_parentWindow != null)
        {
            _parentWindow.StateChanged += OnWindowStateChanged;
            _parentWindow.Activated += OnWindowActivated;
            _parentWindow.Deactivated += OnWindowDeactivated;
            _parentWindow.IsVisibleChanged += OnParentWindowIsVisibleChanged;
            _isWindowActive = _parentWindow.IsActive;
        }
        UpdateRenderingSubscription();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_parentWindow != null)
        {
            _parentWindow.StateChanged -= OnWindowStateChanged;
            _parentWindow.Activated -= OnWindowActivated;
            _parentWindow.Deactivated -= OnWindowDeactivated;
            _parentWindow.IsVisibleChanged -= OnParentWindowIsVisibleChanged;
            _parentWindow = null;
        }
        UnhookRendering();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateRenderingSubscription();
    private void OnWindowActivated(object? sender, EventArgs e) => _isWindowActive = true;
    private void OnWindowDeactivated(object? sender, EventArgs e) => _isWindowActive = false;
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRenderingSubscription();
    private void OnParentWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateRenderingSubscription();

    private void UpdateRenderingSubscription()
    {
        bool isMinimized = _parentWindow?.WindowState == WindowState.Minimized;
        bool isWindowHidden = _parentWindow != null && (_parentWindow.Visibility != Visibility.Visible || !_parentWindow.IsVisible);
        if (IsLoaded && IsVisible && !isMinimized && !isWindowHidden)
        {
            HookRendering();
        }
        else
        {
            UnhookRendering();
        }
    }

    private void HookRendering()
    {
        if (!_isHooked)
        {
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += OnRendering;
            _isHooked = true;
        }
    }

    private void UnhookRendering()
    {
        if (_isHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isHooked = false;
        }
    }

    private void InitParticles(double width, double height)
    {
        for (int i = 0; i < _particles.Length; i++)
        {
            Particle p = CreateParticle(width, height);
            p.Y = Random.Shared.NextDouble() * height;
            _particles[i] = p;
        }
        _isInitialized = true;
    }

    private static Particle CreateParticle(double width, double height)
    {
        // Точные параметры с сайта https://amurkapve.ru/ (js/main.js & css/page-home.css)
        // Форма: идеальный круг (border-radius: 50%)
        // Размер: diameter = 1.5px..3.5px -> radius = 0.75px..1.75px
        // Длительность пролета: 10s..25s
        double diameter = 1.5 + Random.Shared.NextDouble() * 2.0;
        double radius = diameter * 0.5;
        double duration = 10.0 + Random.Shared.NextDouble() * 15.0;
        double effectiveHeight = height > 0 ? height : 620.0;
        double speedY = -(effectiveHeight * 1.1 / duration);

        return new Particle
        {
            X = Random.Shared.NextDouble() * (width > 0 ? width : 960),
            Y = effectiveHeight + 10.0,
            Size = radius,
            SpeedY = speedY,
            SpeedX = (Random.Shared.NextDouble() - 0.5) * 4.0,   // Прямой вертикальный подъем с микро-дрейфом
            Opacity = 0.40                                      // Точная непрозрачность с сайта (opacity: 0.4)
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        long currentTimestamp = Stopwatch.GetTimestamp();
        long requiredInterval = _isWindowActive ? ActiveIntervalTicks : InactiveIntervalTicks;

        long elapsedTicks = currentTimestamp - _lastFrameTimestamp;
        if (elapsedTicks < requiredInterval)
            return;

        double dt = (double)elapsedTicks / Stopwatch.Frequency;
        if (dt > 0.1) dt = 0.016; // Защита от скачков при сворачивании или системных паузах

        _lastFrameTimestamp = currentTimestamp;

        double w = ActualWidth;
        double h = ActualHeight;

        if (w <= 0 || h <= 0) return;

        if (!_isInitialized)
        {
            InitParticles(w, h);
        }

        for (int i = 0; i < _particles.Length; i++)
        {
            ref Particle p = ref _particles[i];
            p.X += p.SpeedX * dt;
            p.Y += p.SpeedY * dt;

            if (p.Y < -15)
            {
                Particle fresh = CreateParticle(w, h);
                p.X = fresh.X;
                p.Y = h + 10.0;
                p.Size = fresh.Size;
                p.SpeedX = fresh.SpeedX;
                p.SpeedY = fresh.SpeedY;
                p.Opacity = fresh.Opacity;
            }

            if (p.X < -15) p.X = w + 15;
            if (p.X > w + 15) p.X = -15;
        }

        double bottomLimit = h * 0.90;
        double topLimit = h * 0.10;

        using DrawingContext dc = _particleVisual.RenderOpen();
        for (int i = 0; i < _particles.Length; i++)
        {
            ref readonly Particle p = ref _particles[i];

            // Точная кривая keyframes particleFloat с amurkapve.ru:
            // 0% (низ) -> 0.0
            // 10% пути снизу -> 0.4
            // 90% пути -> 0.4
            // 100% (верх) -> 0.0
            double fade;
            if (p.Y > bottomLimit)
            {
                fade = Math.Clamp((h - p.Y) / Math.Max(1.0, h - bottomLimit), 0.0, 1.0);
            }
            else if (p.Y < topLimit)
            {
                fade = Math.Clamp(p.Y / Math.Max(1.0, topLimit), 0.0, 1.0);
            }
            else
            {
                fade = 1.0;
            }

            int alpha = Math.Clamp((int)(p.Opacity * fade * 255.0), 0, 255);
            if (alpha == 0) continue;

            // Форма: четкий правильный круг (border-radius: 50%), цвет #00C878, без сторонних теней
            SolidColorBrush brush = CachedBrushes[alpha];
            dc.DrawEllipse(brush, null, new Point(p.X, p.Y), p.Size, p.Size);
        }
    }
}
