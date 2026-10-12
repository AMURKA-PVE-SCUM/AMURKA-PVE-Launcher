using System.Windows;
using System.Windows.Media;

namespace AmurkaLauncher.Controls;

public class ParticleCanvas : FrameworkElement
{
    private class Particle
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Size { get; set; }
        public double SpeedX { get; set; }
        public double SpeedY { get; set; }
        public double Opacity { get; set; }
    }

    private readonly List<Particle> _particles = new();
    private readonly Random _rand = new();
    private bool _isInitialized;

    public ParticleCanvas()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
    }

    private void InitParticles(double width, double height)
    {
        _particles.Clear();
        for (int i = 0; i < 80; i++)
        {
            var p = CreateParticle(width, height);
            p.Y = _rand.NextDouble() * height;
            _particles.Add(p);
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
        double w = ActualWidth;
        double h = ActualHeight;

        if (w <= 0 || h <= 0) return;

        if (!_isInitialized || _particles.Count == 0)
        {
            InitParticles(w, h);
        }

        foreach (var p in _particles)
        {
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

        foreach (var p in _particles)
        {
            byte alpha = (byte)Math.Clamp(p.Opacity * 0.6 * 255, 0, 255);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, 0, 255, 136));
            brush.Freeze();

            dc.DrawEllipse(brush, null, new Point(p.X, p.Y), p.Size, p.Size);

            if (p.Size > 1.5)
            {
                byte glowAlpha = (byte)Math.Clamp(p.Opacity * 0.08 * 255, 0, 255);
                var glowBrush = new SolidColorBrush(Color.FromArgb(glowAlpha, 0, 255, 136));
                glowBrush.Freeze();
                dc.DrawEllipse(glowBrush, null, new Point(p.X, p.Y), p.Size * 2, p.Size * 2);
            }
        }
    }
}
