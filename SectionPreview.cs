using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ColumnRebar
{
    /// <summary>
    /// Esquema de la seccion de la columna con su armado: hormigon, cada estribo con su
    /// color, las grapas y cada barra longitudinal como un punto (las obligadas en rojo
    /// oscuro, las intermedias en naranja). Zoom con la rueda (centrado en el cursor),
    /// desplazamiento arrastrando y doble clic para volver a encajar. Toda la geometria
    /// sale de ColumnPlan, la misma clase que usa el generador.
    /// </summary>
    public sealed class SectionPreview : Canvas
    {
        private ColumnSection _s;
        private ColumnPlan _plan;
        private bool _hooks, _tieHooks;
        private string _message = "Sin elemento armable";

        private double _zoom = 1;
        private Vector _pan;
        private double _x0, _y0;
        private Point _dragStart;
        private Vector _panStart;
        private bool _dragging;

        private const double FtToMm = 304.8;

        public static readonly Brush[] StirrupBrushes =
        {
            new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x7A)),
            new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2E)),
            new SolidColorBrush(Color.FromRgb(0x3B, 0x6F, 0xB6)),
            new SolidColorBrush(Color.FromRgb(0x3F, 0x9C, 0x5A)),
            new SolidColorBrush(Color.FromRgb(0x9C, 0x3F, 0x8A)),
            new SolidColorBrush(Color.FromRgb(0x7A, 0x5A, 0x1F))
        };
        public static readonly Brush RequiredBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E));
        public static readonly Brush IntermediateBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x6C, 0x2A));
        public static readonly Brush TieBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x3E, 0x9D));

        public static Brush StirrupBrush(int index) => StirrupBrushes[index % StirrupBrushes.Length];

        public SectionPreview()
        {
            Background = Brushes.White;
            ClipToBounds = true;
            SizeChanged += (s, e) => Redraw();
            MouseWheel += OnWheel;
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseLeave += (s, e) => { _dragging = false; ReleaseMouseCapture(); };
            Cursor = Cursors.Hand;
        }

        /// <param name="hooks">Dibujar los ganchos a 135 grados de los estribos (esquema).</param>
        /// <param name="tieHooks">Dibujar los ganchos de las grapas.</param>
        public void Show(ColumnSection s, ColumnPlan plan, bool hooks, bool tieHooks)
        {
            bool changed = !ReferenceEquals(_s, s);
            _s = s; _plan = plan; _hooks = hooks; _tieHooks = tieHooks;
            if (changed) ResetView(); else Redraw();
        }

        public void Clear(string message)
        {
            _s = null; _plan = null; _message = message;
            Redraw();
        }

        public void ResetView()
        {
            _zoom = 1; _pan = new Vector(0, 0);
            Redraw();
        }

        // --- zoom y desplazamiento ---
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_s == null) return;
            double factor = e.Delta > 0 ? 1.25 : 1 / 1.25;
            double newZoom = Math.Max(1, Math.Min(40, _zoom * factor));
            factor = newZoom / _zoom;
            Point m = e.GetPosition(this);
            _pan = new Vector(m.X - _x0 - (m.X - _pan.X - _x0) * factor, m.Y - _y0 - (m.Y - _pan.Y - _y0) * factor);
            _zoom = newZoom;
            if (_zoom <= 1.0001) _pan = new Vector(0, 0);
            Redraw();
            e.Handled = true;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (_s == null) return;
            if (e.ClickCount == 2) { ResetView(); e.Handled = true; return; }
            _dragging = true; _dragStart = e.GetPosition(this); _panStart = _pan;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _pan = _panStart + (e.GetPosition(this) - _dragStart);
            Redraw();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }

        // --- dibujo ---
        private static string Mm(double ft) => Math.Round(ft * FtToMm).ToString(CultureInfo.InvariantCulture);

        private void Redraw()
        {
            Children.Clear();
            double W = ActualWidth, H = ActualHeight;
            if (W < 10 || H < 10) return;
            if (_s == null || _plan == null)
            {
                Text(_message, 10, 10, Brushes.Gray, 12);
                return;
            }

            double margin = 46;
            double k = Math.Min((W - 2 * margin) / Math.Max(_s.Width, 1e-6), (H - 2 * margin) / Math.Max(_s.Depth, 1e-6)) * _zoom;
            // origen sin zoom (esquina inferior izquierda de la seccion); el zoom crece desde ahi y el desplazamiento se suma
            _x0 = 0.5 * (W - _s.Width * k / _zoom);
            _y0 = 0.5 * (H + _s.Depth * k / _zoom);
            double x0 = _x0 + _pan.X, y0 = _y0 + _pan.Y;
            Func<double, double> X = u => x0 + u * k;
            Func<double, double> Y = v => y0 - v * k;

            // hormigon
            var poly = new Polygon { Fill = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)), Stroke = Brushes.DimGray, StrokeThickness = 1.2 };
            foreach (Pt p in _s.Polygon) poly.Points.Add(new Point(X(p.U), Y(p.V)));
            Children.Add(poly);

            // cotas generales
            Text(Mm(_s.Width) + " mm", X(_s.Width / 2) - 25, Y(0) + 6, Brushes.DimGray, 11);
            Text(Mm(_s.Depth) + " mm", X(_s.Width) + 6, Y(_s.Depth / 2) - 8, Brushes.DimGray, 11);

            if (_plan.Error != null)
            {
                Text(_plan.Error, 10, 10, Brushes.Firebrick, 12);
                return;
            }

            // estribos
            foreach (PlanStirrup st in _plan.Stirrups)
            {
                Brush b = StirrupBrush(st.Index);
                double th = Math.Max(1.5, _plan.Ds * k);
                var r = new Rectangle
                {
                    Stroke = b, StrokeThickness = th, Fill = null, StrokeLineJoin = PenLineJoin.Round,
                    Width = st.Line.W * k, Height = st.Line.H * k
                };
                SetLeft(r, X(st.Line.U1)); SetTop(r, Y(st.Line.V2));
                r.ToolTip = "Estribo " + (st.Index + 1) + ": " + Mm(st.Concrete.W) + " x " + Mm(st.Concrete.H) + " mm";
                Children.Add(r);
                Text("E" + (st.Index + 1), X(st.Line.CU) - 8, Y(st.Line.V2) - 16 + st.Index * 12, b, 11, true);

                // ganchos a 135 grados en la esquina superior izquierda (donde empieza y acaba el estribo):
                // cada extremo sigue su lado, dobla hacia el interior y acaba a 45 grados hacia el nucleo
                if (_hooks)
                {
                    double hook = Math.Max(6 * _plan.Ds, 75 / FtToMm);   // longitud de la pata (6 diametros, minimo 75 mm)
                    double gap = 1.2 * _plan.Ds;                          // las dos patas van una junto a otra
                    Pt c = new Pt(st.Line.U1, st.Line.V2);
                    // extremo que llega por el lado superior: dobla hacia abajo-derecha
                    Hook(X, Y, new Pt(c.U + gap, c.V), new Pt(c.U + gap + hook * 0.7071, c.V - hook * 0.7071), b, th);
                    // extremo que llega por el lado izquierdo: dobla hacia abajo-derecha, un poco mas abajo
                    Hook(X, Y, new Pt(c.U, c.V - gap), new Pt(c.U + hook * 0.7071, c.V - gap - hook * 0.7071), b, th);
                }
            }

            // grapas
            foreach (PlanTie t in _plan.Ties)
            {
                var ln = new Line
                {
                    X1 = X(t.A.U), Y1 = Y(t.A.V), X2 = X(t.B.U), Y2 = Y(t.B.V),
                    Stroke = TieBrush, StrokeThickness = Math.Max(1.2, _plan.Dt * k), StrokeDashArray = new DoubleCollection { 4, 2 },
                    ToolTip = "Grapa (estribo " + (t.Stirrup + 1) + ")"
                };
                Children.Add(ln);
                if (_tieHooks)
                {
                    // gancho a 135 grados en cada extremo: vuelve hacia dentro a 45 grados
                    double hook = Math.Max(6 * _plan.Dt, 75 / FtToMm) * 0.7071;
                    double du = t.B.U - t.A.U, dv = t.B.V - t.A.V;
                    double len = Math.Sqrt(du * du + dv * dv);
                    if (len > 1e-9)
                    {
                        du /= len; dv /= len;
                        // perpendicular hacia el centro del estribo
                        PlanStirrup st = _plan.Stirrups[t.Stirrup];
                        double pu = -dv, pv = du;
                        double mu = 0.5 * (t.A.U + t.B.U), mv = 0.5 * (t.A.V + t.B.V);
                        if ((st.Line.CU - mu) * pu + (st.Line.CV - mv) * pv < 0) { pu = -pu; pv = -pv; }
                        Hook(X, Y, t.A, new Pt(t.A.U + (du + pu) * hook, t.A.V + (dv + pv) * hook), TieBrush, Math.Max(1.2, _plan.Dt * k));
                        Hook(X, Y, t.B, new Pt(t.B.U + (-du + pu) * hook, t.B.V + (-dv + pv) * hook), TieBrush, Math.Max(1.2, _plan.Dt * k));
                    }
                }
            }

            // barras (cada una con su diametro)
            foreach (PlanBar bar in _plan.Bars)
            {
                double rr = Math.Max(2.5, 0.5 * _plan.DiameterOf(bar) * k);
                var e = new Ellipse
                {
                    Width = 2 * rr, Height = 2 * rr,
                    Fill = bar.Required ? RequiredBrush : IntermediateBrush, Stroke = Brushes.Black, StrokeThickness = 0.6,
                    ToolTip = (bar.Required ? "Barra de esquina o cruce" : "Barra intermedia") + " (" + Mm(_plan.DiameterOf(bar)) + " mm) en u=" + Mm(bar.P.U) + ", v=" + Mm(bar.P.V) + " mm"
                };
                SetLeft(e, X(bar.P.U) - rr); SetTop(e, Y(bar.P.V) - rr);
                Children.Add(e);
            }

            // resumen
            Text(_plan.Describe() + " | " + _plan.DescribeCounts() + " (arriba/abajo/intermedias por costado)", 8, H - 20, Brushes.DimGray, 11);
            if (_plan.Warnings.Count > 0) Text(string.Join(" | ", _plan.Warnings), 8, H - 36, Brushes.Firebrick, 11);
        }

        private void Hook(Func<double, double> X, Func<double, double> Y, Pt from, Pt to, Brush brush, double thickness)
        {
            Children.Add(new Line
            {
                X1 = X(from.U), Y1 = Y(from.V), X2 = X(to.U), Y2 = Y(to.V),
                Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }

        private TextBlock Text(string s, double x, double y, Brush brush, double size, bool bold = false)
        {
            var t = new TextBlock { Text = s, Foreground = brush, FontSize = size, TextWrapping = TextWrapping.NoWrap };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            SetLeft(t, x); SetTop(t, y);
            Children.Add(t);
            return t;
        }
    }
}
