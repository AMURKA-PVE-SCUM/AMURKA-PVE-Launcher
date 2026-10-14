using System.Windows;
using System.Windows.Media;

namespace AmurkaLauncher.Controls;

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

    private const int ParticleCount = 80;
    private readonly Particle[] _particles = new Particle[ParticleCount];
    private readonly Random _rand = new();
    private bool _isInitialized;
    private bool _isHooked;

    // Кэш замороженных кистей для всех 256 уровней альфа-канала цвета #00FF88.
    // Полностью устраняет тысячи GC-аллокаций кистей в секунду при рендеринге (60-144 FPS).
    private static readonly SolidColorBrush[] CachedBrushes = CreateBrushCache();

    private static SolidColorBrush[] CreateBrushCache()
    {
        var brushes = new SolidColorBrush[256];
        for (int i = 0; i < 256; i++)
        {
            var brush = new SolidColorBrush(Color.FromArgb((byte)i, 0, 255, 136));
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }

    public ParticleCanvas()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateRenderingSubscription();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookRendering();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateRenderingSubscription();
    }

    private void UpdateRenderingSubscription()
    {
        if (IsLoaded && IsVisible)
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
        for (int i = 0; i < ParticleCount; i++)
        {
            var p = CreateParticle(width, height);
            p.Y = _rand.NextDouble() * height;
            _particles[i] = p;
        }
        _isInitialized = true;
    }

    private Particle CreateParticle(double width, double height)
    {
        double size = _rand.NextDouble() * 2.5 + 0.5;
        return new Particle
        {
            X = _rand.NextDouble() * (width > 0 ? width : 960),
            Y = (height > 0 ? height : 620) + size,
            Size = size,
            SpeedY = -(_rand.NextDouble() * 0.4 + 0.1),
            SpeedX = (_rand.NextDouble() - 0.5) * 0.2,
            Opacity = _rand.NextDouble() * 0.5 + 0.1
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible) return;

        double w = ActualWidth;
        double h = ActualHeight;

        if (w <= 0 || h <= 0) return;

        if (!_isInitialized)
        {
            InitParticles(w, h);
        }

        for (int i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            p.X += p.SpeedX;
            p.Y += p.SpeedY;

            if (p.Y < -10)
            {
                var fresh = CreateParticle(w, h);
                p.X = fresh.X;
                p.Y = h + fresh.Size;
                p.Size = fresh.Size;
                p.SpeedX = fresh.SpeedX;
                p.SpeedY = fresh.SpeedY;
                p.Opacity = fresh.Opacity;
            }

            if (p.X < -10) p.X = w + 10;
            if (p.X > w + 10) p.X = -10;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (!IsVisible) return;

        for (int i = 0; i < _particles.Length; i++)
        {
            ref readonly var p = ref _particles[i];

            int alpha = Math.Clamp((int)(p.Opacity * 0.6 * 255.0), 0, 255);
            var brush = CachedBrushes[alpha];

            dc.DrawEllipse(brush, null, new Point(p.X, p.Y), p.Size, p.Size);

            if (p.Size > 1.5)
            {
                int glowAlpha = Math.Clamp((int)(p.Opacity * 0.08 * 255.0), 0, 255);
                var glowBrush = CachedBrushes[glowAlpha];
                dc.DrawEllipse(glowBrush, null, new Point(p.X, p.Y), p.Size * 2, p.Size * 2);
            }
        }
    }
}
