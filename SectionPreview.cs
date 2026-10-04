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
        /// <summary>Angulo (grados) de los ganchos de estribos y grapas; 0 = sin gancho.</summary>
        private double _hookDeg, _tieHookDeg;
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
        /// <summary>Numeros de posicion de barra (para elegir los estribos interiores).</summary>
        public static readonly Brush PositionBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x5F, 0x9F));

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

        /// <param name="hookDeg">Angulo del gancho de los estribos en grados (90, 135, 180); 0 = sin gancho.</param>
        /// <param name="tieHookDeg">Idem para las grapas.</param>
        public void Show(ColumnSection s, ColumnPlan plan, double hookDeg, double tieHookDeg)
        {
            bool changed = !ReferenceEquals(_s, s);
            _s = s; _plan = plan; _hookDeg = hookDeg; _tieHookDeg = tieHookDeg;
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
            Text(Mm(_s.Width) + " mm", X(_s.Width / 2) - 25, Y(0) + 18, Brushes.DimGray, 11);
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
                if (st.Octagonal)
                {
                    // octogonal: el poligono de su eje (tramos rectos sobre el estribo rectangular, diagonales en las esquinas)
                    var pg = new Polygon
                    {
                        Stroke = b, StrokeThickness = th, Fill = null, StrokeLineJoin = PenLineJoin.Round,
                        ToolTip = "Estribo " + (st.Index + 1) + " (octogonal, " + st.Path.Count + " lados): pasa por " + st.Held.Count + " barras intermedias"
                    };
                    foreach (Pt p in st.Path) pg.Points.Add(new Point(X(p.U), Y(p.V)));
                    Children.Add(pg);
                }
                else
                {
                    var r = new Rectangle
                    {
                        Stroke = b, StrokeThickness = th, Fill = null, StrokeLineJoin = PenLineJoin.Round,
                        Width = st.Line.W * k, Height = st.Line.H * k
                    };
                    SetLeft(r, X(st.Line.U1)); SetTop(r, Y(st.Line.V2));
                    r.ToolTip = st.Interior
                        ? "Estribo " + (st.Index + 1) + " (interior): " + Mm(st.Line.W + _plan.Ds) + " x " + Mm(st.Line.H + _plan.Ds) + " mm por fuera"
                        : "Estribo " + (st.Index + 1) + ": " + Mm(st.Concrete.W) + " x " + Mm(st.Concrete.H) + " mm";
                    Children.Add(r);
                }
                Text("E" + (st.Index + 1), X(st.Line.CU) - 8, Y(st.Line.V2) - 16 + st.Index * 12, b, 11, true);

                // ganchos en la esquina superior izquierda (donde empieza y acaba el estribo). El
                // estribo sale de esa esquina hacia abajo y vuelve a ella por el lado superior; cada
                // extremo dobla el angulo del tipo de gancho hacia el interior.
                if (_hookDeg > 0)
                {
                    double hook = Math.Max(6 * _plan.Ds, 75 / FtToMm);   // pata (6 diametros, minimo 75 mm)
                    double gap = 1.2 * _plan.Ds;
                    if (st.Octagonal)
                    {
                        // empieza y acaba en el primer vertice: llega por el lado anterior y sale por el siguiente
                        int n = st.Path.Count;
                        Pt c = st.Path[0], prev = st.Path[n - 1], next = st.Path[1];
                        HookAtVertex(X, Y, st, c, prev, next, gap, _hookDeg, hook, b, th);
                    }
                    else
                    {
                        Pt c = new Pt(st.Line.U1, st.Line.V2);
                        // extremo final: llega por el lado superior hacia la izquierda (d = -u), interior = -v
                        HookLeg(X, Y, new Pt(c.U, c.V - gap), -1, 0, 0, -1, _hookDeg, hook, b, th);
                        // extremo inicial: sale hacia abajo, es decir "llega" desde abajo (d = +v), interior = +u
                        HookLeg(X, Y, new Pt(c.U + gap, c.V), 0, 1, 1, 0, _hookDeg, hook, b, th);
                    }
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
                if (_tieHookDeg > 0)
                {
                    double hook = Math.Max(6 * _plan.Dt, 75 / FtToMm);
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
                        double thick = Math.Max(1.2, _plan.Dt * k);
                        HookLeg(X, Y, t.B, du, dv, pu, pv, _tieHookDeg, hook, TieBrush, thick);      // extremo B: la barra llega en direccion d
                        HookLeg(X, Y, t.A, -du, -dv, pu, pv, _tieHookDeg, hook, TieBrush, thick);    // extremo A: llega en direccion -d
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

            // etiquetas de filas (izquierda) y verticales (abajo)
            foreach (PlanLine l in _plan.Rows)
                Text(l.Name, X(0) - 26, Y(l.Coord) - 8, Brushes.DimGray, 10, true);
            foreach (PlanLine l in _plan.Cols)
                Text(l.Name, X(l.Coord) - 8, Y(0) + 4, Brushes.DimGray, 10, true);

            // posiciones de barra con las que se eligen los estribos interiores (solo si la columna tiene alguno):
            // encima, de izquierda a derecha; a la izquierda de las filas, de arriba abajo
            if (_plan.Opt.Inner != null && _plan.Opt.Inner.Count > 0)
            {
                for (int i = 0; i < _plan.BarUs.Count; i++)
                    Text((i + 1).ToString(CultureInfo.InvariantCulture), X(_plan.BarUs[i]) - (i >= 9 ? 6 : 3), Y(_s.Depth) - 16, PositionBrush, 9, true);
                for (int i = 0; i < _plan.BarVs.Count; i++)
                    Text((i + 1).ToString(CultureInfo.InvariantCulture), X(0) - 40, Y(_plan.BarVs[i]) - 7, PositionBrush, 9, true);
            }

            // resumen
            Text(_plan.Describe() + " | " + _plan.DescribeLines(), 8, H - 20, Brushes.DimGray, 11);
            var alerts = new List<string>();
            if (_plan.InteriorError != null) alerts.Add(_plan.InteriorError);
            alerts.AddRange(_plan.Warnings);
            if (alerts.Count > 0) Text(string.Join(" | ", alerts), 8, H - 36, Brushes.Firebrick, 11);
        }

        /// <summary>
        /// Pata de un gancho en el punto "at": la barra llega con direccion (du, dv) y dobla
        /// "deg" grados hacia el interior (nu, nv): 90 = la pata sigue el interior, 135 = a 45
        /// grados hacia el nucleo, 180 = vuelve sobre la barra (separada un diametro).
        /// </summary>
        private void HookLeg(Func<double, double> X, Func<double, double> Y, Pt at, double du, double dv, double nu, double nv,
                             double deg, double len, Brush brush, double thickness)
        {
            double a = deg * Math.PI / 180;
            double lu = Math.Cos(a) * du + Math.Sin(a) * nu, lv = Math.Cos(a) * dv + Math.Sin(a) * nv;
            Pt from = at;
            if (deg >= 170) from = new Pt(at.U + nu * 1.5 * _plan.Ds, at.V + nv * 1.5 * _plan.Ds);   // el pliegue de un gancho a 180 deja la pata por dentro
            Hook(X, Y, from, new Pt(from.U + lu * len, from.V + lv * len), brush, thickness);
        }

        /// <summary>
        /// Los dos ganchos de un estribo poligonal en el vertice "c": el extremo final llega por el
        /// lado prev-c y el inicial sale por c-next (es decir, "llega" desde next). Cada uno dobla hacia
        /// el interior del estribo, separado "gap" del vertice sobre su propio lado.
        /// </summary>
        private void HookAtVertex(Func<double, double> X, Func<double, double> Y, PlanStirrup st, Pt c, Pt prev, Pt next,
                                  double gap, double deg, double len, Brush brush, double thickness)
        {
            foreach (Pt other in new[] { prev, next })
            {
                double du = c.U - other.U, dv = c.V - other.V;
                double l = Math.Sqrt(du * du + dv * dv);
                if (l < 1e-9) continue;
                du /= l; dv /= l;   // direccion de llegada al vertice (del otro extremo del lado hacia c)
                // normal hacia el interior del estribo
                double nu = -dv, nv = du;
                if ((st.Line.CU - c.U) * nu + (st.Line.CV - c.V) * nv < 0) { nu = -nu; nv = -nv; }
                // como en el rectangular: el extremo se dibuja un poco hacia dentro de su lado
                HookLeg(X, Y, new Pt(c.U + nu * gap, c.V + nv * gap), du, dv, nu, nv, deg, len, brush, thickness);
            }
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
