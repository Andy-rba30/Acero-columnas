using System;
using System.Collections.Generic;
using System.Linq;

namespace ColumnRebar
{
    /// <summary>Una barra longitudinal: posicion en la seccion y si es obligada (esquina o cruce de estribos) o intermedia.</summary>
    public sealed class PlanBar
    {
        public Pt P;
        /// <summary>Esquina de un estribo o cruce de dos estribos: lleva el diametro de esquina. Las demas son intermedias.</summary>
        public bool Required;
        /// <summary>Estribo y lado en el que se apoya una intermedia (0 abajo, 1 arriba, 2 izquierda, 3 derecha; -1 en obligadas).</summary>
        public int Stirrup = -1, Edge = -1;
        public override string ToString() => P.ToString();
    }

    /// <summary>Un estribo cerrado: rectangulo maximo de la seccion, eje del estribo y rectangulo de los ejes de sus barras de esquina.</summary>
    public sealed class PlanStirrup
    {
        public int Index;
        public Rect Concrete;
        /// <summary>Eje del estribo (rectangulo de hormigon menos recubrimiento y medio diametro).</summary>
        public Rect Line;
        /// <summary>Ejes de las barras de esquina apoyadas en este estribo (Line menos medio estribo y media barra de esquina).</summary>
        public Rect BarLine;
    }

    /// <summary>Una grapa entre dos barras enfrentadas del mismo estribo (de A a B, eje de la grapa).</summary>
    public sealed class PlanTie
    {
        public Pt A, B;
        public int Stirrup;
        public bool AlongU => Math.Abs(A.V - B.V) < 1e-9;
    }

    /// <summary>Numero de barras y reparto de una linea (fila o vertical) elegidos para una columna.</summary>
    public sealed class LineSpec
    {
        /// <summary>Total de barras en la linea (obligadas incluidas). 0 o menos que las obligadas = solo las obligadas.</summary>
        public int Count;
        /// <summary>"auto", "left", "right", "center" o "" (el general).</summary>
        public string Fill = "";
        public LineSpec Clone() => new LineSpec { Count = Count, Fill = Fill };
    }

    /// <summary>
    /// Una linea de barras de la seccion: fila (horizontal, coordenada v) o vertical
    /// (coordenada u). Reune los lados de estribo que caen sobre ella.
    /// </summary>
    public sealed class PlanLine
    {
        public int Index;
        public bool Horizontal;
        /// <summary>Coordenada de los ejes de las barras de esquina de la linea (v en filas, u en verticales).</summary>
        public double Coord;
        /// <summary>Lados de estribo (estribo, lado) que apoyan barras en esta linea.</summary>
        public List<(int stirrup, int edge)> Edges = new List<(int, int)>();
        /// <summary>Barras obligadas de la linea (esquinas y cruces de estribos): el minimo.</summary>
        public int Fixed;
        /// <summary>Barras que han quedado en la linea (obligadas e intermedias).</summary>
        public int Bars;
        /// <summary>Barras que no se pudieron colocar por falta de sitio.</summary>
        public int Missing;
        public string Name => (Horizontal ? "F" : "V") + (Index + 1);
    }

    /// <summary>Todo lo que necesita ColumnPlan.Build (en pies).</summary>
    public sealed class PlanOptions
    {
        public double Cover;
        /// <summary>Diametro del estribo, de las barras de esquina y de las intermedias.</summary>
        public double Ds, DbCorner, DbInter;
        /// <summary>Reparto general de las anadidas: "auto", "left" (abajo en verticales), "right" (arriba), "center".</summary>
        public string Fill = "auto";
        /// <summary>Elecciones por linea de esta columna (indice = fila de arriba abajo / vertical de izquierda a derecha); null = general.</summary>
        public IList<LineSpec> Rows, Cols;
        public bool TiesU, TiesV;
        public double Dt;
        public double Tol;

        /// <summary>Total pedido para la linea (0 = solo las obligadas).</summary>
        public int CountFor(bool horizontal, int index)
        {
            IList<LineSpec> list = horizontal ? Rows : Cols;
            LineSpec own = list != null && index < list.Count ? list[index] : null;
            return own == null ? 0 : own.Count;
        }

        public string FillFor(bool horizontal, int index)
        {
            IList<LineSpec> list = horizontal ? Rows : Cols;
            LineSpec own = list != null && index < list.Count ? list[index] : null;
            return own == null || string.IsNullOrEmpty(own.Fill) ? Fill : own.Fill;
        }
    }

    /// <summary>
    /// Armado de la seccion (sin la altura): estribos, barras longitudinales y grapas en
    /// coordenadas locales (u, v). Geometria pura, compartida por la ventana (esquema) y
    /// el generador, para que lo que se ve sea lo que se crea.
    ///
    /// Reglas:
    ///  - cada rectangulo maximo de la seccion lleva un estribo cerrado a "cover" de las caras;
    ///  - hay barra obligada (diametro de esquina) en cada esquina de cada estribo y donde un
    ///    lado de un estribo cruza un lado de otro (la barra queda apoyada en los dos);
    ///  - las barras se cuentan por lineas: filas (los lados horizontales de los estribos, de
    ///    arriba abajo) y verticales (los lados verticales, de izquierda a derecha). En una
    ///    seccion rectangular hay 2 filas y 2 verticales; en una L, 3 y 3. Cada linea lleva
    ///    un total de barras (obligadas incluidas): las que faltan se anaden, con su propio
    ///    diametro, en los huecos entre obligadas segun el reparto elegido, nunca a menos de
    ///    1.5 diametros libres;
    ///  - grapas opcionales entre cada par de intermedias enfrentadas de un mismo estribo
    ///    que no tenga ya un lado de otro estribo pasando por ahi.
    /// </summary>
    public sealed class ColumnPlan
    {
        public List<PlanStirrup> Stirrups = new List<PlanStirrup>();
        public List<PlanBar> Bars = new List<PlanBar>();
        public List<PlanTie> Ties = new List<PlanTie>();
        public List<PlanLine> Rows = new List<PlanLine>();
        public List<PlanLine> Cols = new List<PlanLine>();
        public List<string> Warnings = new List<string>();
        public string Error;
        public PlanOptions Opt;

        public double Cover => Opt.Cover;
        public double Ds => Opt.Ds;
        public double Dt => Opt.Dt;
        public double DiameterOf(PlanBar b) => b.Required ? Opt.DbCorner : Opt.DbInter;

        public int RequiredCount => Bars.Count(b => b.Required);
        public int IntermediateCount => Bars.Count(b => !b.Required);

        private static double ToMm(double ft) => Math.Round(ft * 304.8);

        public string Describe() =>
            Error != null ? Error
            : Bars.Count + " barras (" + RequiredCount + " en esquinas y cruces, " + IntermediateCount + " intermedias), " +
              Stirrups.Count + (Stirrups.Count == 1 ? " estribo" : " estribos") + (Ties.Count > 0 ? ", " + Ties.Count + " grapas" : "");

        /// <summary>Resumen por linea: "F1 4, F2 3, F3 2 | V1 3, V2 2".</summary>
        public string DescribeLines() =>
            string.Join(", ", Rows.Select(l => l.Name + " " + l.Bars)) + " | " + string.Join(", ", Cols.Select(l => l.Name + " " + l.Bars));

        /// <summary>
        /// Lineas de la seccion (filas o verticales) que tendria esta geometria con estos
        /// diametros, sin colocar barras: para que la ventana sepa cuantas entradas mostrar.
        /// </summary>
        public static (int rows, int cols) LineCount(IList<Rect> rects, double cover, double ds, double dbCorner, double tol)
        {
            var plan = new ColumnPlan { Opt = new PlanOptions { Cover = cover, Ds = ds, DbCorner = dbCorner, DbInter = dbCorner, Tol = tol } };
            if (!plan.MakeStirrups(rects)) return (0, 0);
            plan.MakeLines();
            return (plan.Rows.Count, plan.Cols.Count);
        }

        // lados: 0 abajo (v = Line.V1, hacia +v), 1 arriba (v = Line.V2, hacia -v), 2 izquierda (u = Line.U1, hacia +u), 3 derecha (u = Line.U2, hacia -u)
        private static double LineCoord(PlanStirrup s, int edge) =>
            edge == 0 ? s.Line.V1 : edge == 1 ? s.Line.V2 : edge == 2 ? s.Line.U1 : s.Line.U2;
        private static double Inward(int edge) => edge == 0 || edge == 2 ? 1 : -1;
        private static bool Horizontal(int edge) => edge < 2;
        private double OffC => 0.5 * Opt.Ds + 0.5 * Opt.DbCorner;
        private double OffI => 0.5 * Opt.Ds + 0.5 * Opt.DbInter;

        private Pt PointOn(PlanStirrup s, int edge, double along, double off)
        {
            double perp = LineCoord(s, edge) + Inward(edge) * off;
            return Horizontal(edge) ? new Pt(along, perp) : new Pt(perp, along);
        }

        /// <summary>Rango a lo largo del lado (entre sus dos barras de esquina).</summary>
        private static (double a0, double a1) Range(PlanStirrup s, int edge) =>
            Horizontal(edge) ? (s.BarLine.U1, s.BarLine.U2) : (s.BarLine.V1, s.BarLine.V2);

        /// <summary>Barras (obligadas o intermedias, con cualquiera de los dos diametros) apoyadas en el lado dado, ordenadas a lo largo.</summary>
        public List<PlanBar> EdgeBars(PlanStirrup s, int edge)
        {
            double lo = Math.Min(OffC, OffI) - Opt.Tol, hi = Math.Max(OffC, OffI) + Opt.Tol;
            double line = LineCoord(s, edge), inw = Inward(edge);
            (double a0, double a1) = Range(s, edge);
            var list = new List<PlanBar>();
            foreach (PlanBar b in Bars)
            {
                double perp = Horizontal(edge) ? b.P.V : b.P.U;
                double along = Horizontal(edge) ? b.P.U : b.P.V;
                double d = (perp - line) * inw;
                if (d >= lo && d <= hi && along >= a0 - Opt.Tol && along <= a1 + Opt.Tol) list.Add(b);
            }
            return list.OrderBy(b => Horizontal(edge) ? b.P.U : b.P.V).ToList();
        }

        private bool IsCornerOf(PlanStirrup s, PlanBar b)
        {
            Rect r = s.BarLine;
            return (Math.Abs(b.P.U - r.U1) <= Opt.Tol || Math.Abs(b.P.U - r.U2) <= Opt.Tol) &&
                   (Math.Abs(b.P.V - r.V1) <= Opt.Tol || Math.Abs(b.P.V - r.V2) <= Opt.Tol);
        }

        private bool MakeStirrups(IList<Rect> rects)
        {
            PlanOptions o = Opt;
            for (int i = 0; i < rects.Count; i++)
            {
                Rect line = rects[i].Inset(o.Cover + 0.5 * o.Ds);
                Rect bar = line.Inset(OffC);
                if (bar.W <= o.Tol || bar.H <= o.Tol)
                {
                    Error = "el estribo " + (i + 1) + " (" + ToMm(rects[i].W) + " x " + ToMm(rects[i].H) +
                            " mm) no cabe con recubrimiento " + ToMm(o.Cover) + " mm y estas barras";
                    return false;
                }
                Stirrups.Add(new PlanStirrup { Index = i, Concrete = rects[i].Clone(), Line = line, BarLine = bar });
            }
            return true;
        }

        /// <summary>Agrupa los lados de los estribos en filas (de arriba abajo) y verticales (de izquierda a derecha).</summary>
        private void MakeLines()
        {
            foreach (bool horizontal in new[] { true, false })
            {
                var items = new List<(double coord, int stirrup, int edge)>();
                foreach (PlanStirrup s in Stirrups)
                    foreach (int e in horizontal ? new[] { 0, 1 } : new[] { 2, 3 })
                        items.Add((LineCoord(s, e) + Inward(e) * OffC, s.Index, e));
                List<double> coords = Rectilinear.Cluster(items.Select(x => x.coord), Opt.Tol);
                if (horizontal) coords.Reverse();   // filas de arriba abajo
                var lines = horizontal ? Rows : Cols;
                for (int i = 0; i < coords.Count; i++)
                {
                    var line = new PlanLine { Index = i, Horizontal = horizontal, Coord = coords[i] };
                    foreach (var it in items)
                        if (Math.Abs(it.coord - coords[i]) <= Opt.Tol) line.Edges.Add((it.stirrup, it.edge));
                    lines.Add(line);
                }
            }
        }

        public static ColumnPlan Build(IList<Pt> polygon, IList<Rect> rects, PlanOptions o)
        {
            var plan = new ColumnPlan { Opt = o };
            double tol = o.Tol;
            if (rects == null || rects.Count == 0) { plan.Error = "la seccion no tiene ningun rectangulo"; return plan; }
            if (!plan.MakeStirrups(rects)) return plan;
            double offC = plan.OffC, offI = plan.OffI;

            // --- barras obligadas: esquinas de cada estribo ---
            foreach (PlanStirrup s in plan.Stirrups)
            {
                Rect b = s.BarLine;
                plan.AddBar(new Pt(b.U1, b.V1), true);
                plan.AddBar(new Pt(b.U2, b.V1), true);
                plan.AddBar(new Pt(b.U2, b.V2), true);
                plan.AddBar(new Pt(b.U1, b.V2), true);
            }

            // --- barras obligadas: cruces entre un lado horizontal de un estribo y uno vertical de otro ---
            foreach (PlanStirrup a in plan.Stirrups)
                foreach (PlanStirrup c in plan.Stirrups)
                {
                    if (ReferenceEquals(a, c)) continue;
                    foreach ((double v, double inV) in new[] { (a.Line.V1, +1.0), (a.Line.V2, -1.0) })
                        foreach ((double u, double inU) in new[] { (c.Line.U1, +1.0), (c.Line.U2, -1.0) })
                        {
                            bool crosses = u > a.Line.U1 + tol && u < a.Line.U2 - tol && v > c.Line.V1 + tol && v < c.Line.V2 - tol;
                            if (crosses) plan.AddBar(new Pt(u + inU * offC, v + inV * offC), true);
                        }
                }

            // --- intermedias, linea a linea ---
            plan.MakeLines();
            double minSp = 2.5 * o.DbInter;   // 1.5 diametros libres entre barras
            foreach (PlanLine line in plan.Rows.Concat(plan.Cols))
            {
                // barras ya en la linea (obligadas) con la posicion a lo largo
                var fixedBars = new List<(double along, PlanBar bar)>();
                foreach ((int si, int e) in line.Edges)
                    foreach (PlanBar b in plan.EdgeBars(plan.Stirrups[si], e))
                        if (!fixedBars.Any(x => ReferenceEquals(x.bar, b)))
                            fixedBars.Add((line.Horizontal ? b.P.U : b.P.V, b));
                fixedBars.Sort((x, y) => x.along.CompareTo(y.along));

                // huecos: entre dos barras consecutivas que esten sobre un mismo lado de estribo
                // (asi no se cruza el vacio de una U); cada hueco recuerda el lado que lo cubre
                var gaps = new List<(double a, double b, int stirrup, int edge)>();
                for (int i = 0; i + 1 < fixedBars.Count; i++)
                {
                    double a = fixedBars[i].along, b = fixedBars[i + 1].along;
                    if (b - a <= tol) continue;
                    foreach ((int si, int e) in line.Edges)
                    {
                        (double r0, double r1) = Range(plan.Stirrups[si], e);
                        if (r0 <= a + tol && r1 >= b - tol) { gaps.Add((a, b, si, e)); break; }
                    }
                }

                line.Fixed = fixedBars.Count;
                int want = Math.Max(fixedBars.Count, o.CountFor(line.Horizontal, line.Index));
                int count = want - fixedBars.Count;
                List<(int gap, double pos)> added = FillByCount(gaps.Select(g => (g.a, g.b)).ToList(), count, minSp,
                                                                o.FillFor(line.Horizontal, line.Index), out int missing);
                foreach ((int gi, double pos) in added)
                    plan.AddIntermediate(plan.Stirrups[gaps[gi].stirrup], gaps[gi].edge, pos, offI);
                line.Bars = fixedBars.Count + added.Count;
                line.Missing = missing;
                if (missing > 0)
                    plan.Warnings.Add(line.Name + ": no caben " + missing + " barra(s) mas con 1.5 diametros libres");
            }

            // --- grapas: entre intermedias enfrentadas con la misma coordenada a lo largo del lado ---
            if (o.TiesU || o.TiesV)
                foreach (PlanStirrup s in plan.Stirrups)
                    foreach ((int e1, int e2, bool on) in new[] { (0, 1, o.TiesV), (2, 3, o.TiesU) })
                    {
                        if (!on) continue;
                        List<PlanBar> b1 = plan.EdgeBars(s, e1), b2 = plan.EdgeBars(s, e2);
                        foreach (PlanBar p in b1)
                        {
                            if (plan.IsCornerOf(s, p)) continue;
                            double along = Horizontal(e1) ? p.P.U : p.P.V;
                            PlanBar q = b2.FirstOrDefault(x => Math.Abs((Horizontal(e2) ? x.P.U : x.P.V) - along) <= tol && !plan.IsCornerOf(s, x));
                            if (q == null) continue;
                            if (plan.LegAt(!Horizontal(e1), along, e1 == 0 ? s.Line.V1 : s.Line.U1, e1 == 0 ? s.Line.V2 : s.Line.U2)) continue;
                            plan.Ties.Add(new PlanTie { A = q.P, B = p.P, Stirrup = s.Index });
                        }
                    }

            // --- comprobaciones ---
            foreach (PlanBar bar in plan.Bars)
                if (!Rectilinear.Inside(polygon, bar.P) || Rectilinear.OnBoundary(polygon, bar.P, o.Cover))
                    plan.Warnings.Add("barra en " + ToMm(bar.P.U) + ", " + ToMm(bar.P.V) + " mm fuera del hormigon o dentro del recubrimiento");
            double minClear = double.MaxValue;
            for (int i = 0; i < plan.Bars.Count; i++)
                for (int j = i + 1; j < plan.Bars.Count; j++)
                    minClear = Math.Min(minClear, plan.Bars[i].P.DistanceTo(plan.Bars[j].P) - 0.5 * plan.DiameterOf(plan.Bars[i]) - 0.5 * plan.DiameterOf(plan.Bars[j]));
            double dbMax = Math.Max(o.DbCorner, o.DbInter);
            if (plan.Bars.Count > 1 && minClear < 1.5 * dbMax)
                plan.Warnings.Add("hay barras a menos de 1.5 diametros libres entre si (" + ToMm(minClear) + " mm)");
            return plan;
        }

        /// <summary>
        /// Reparte "count" barras nuevas entre los huecos segun "mode":
        ///  "auto"   = cada una al hueco con la separacion resultante mas grande (lo mas uniforme posible);
        ///  "left"   = al primer hueco empezando por la izquierda (abajo en las verticales) que la admita;
        ///  "right"  = idem empezando por la derecha (arriba);
        ///  "center" = simetrico: por pares izquierda-derecha, el par mas cercano al centro primero; el hueco central al final.
        /// No se anade ninguna que deje una separacion menor que "minSpacing" (eje a eje);
        /// "missing" dice cuantas se han quedado sin colocar. Devuelve (indice del hueco, posicion).
        /// </summary>
        private static List<(int gap, double pos)> FillByCount(List<(double a, double b)> gaps, int count, double minSpacing, string mode, out int missing)
        {
            var added = new List<(int, double)>();
            missing = 0;
            int n = gaps.Count;
            if (count <= 0 || n == 0) { missing = Math.Max(0, count); return added; }
            var perGap = new int[n];   // barras nuevas en cada hueco
            string m = (mode ?? "").Trim().ToLowerInvariant();

            List<int> order = null;
            if (m == "left") order = Enumerable.Range(0, n).ToList();
            else if (m == "right") order = Enumerable.Range(0, n).Reverse().ToList();
            else if (m == "center")
            {
                order = Enumerable.Range(0, n).Where(i => 2 * i != n - 1)
                                  .OrderBy(i => Math.Abs(2 * i - (n - 1))).ThenBy(i => i).ToList();
                if (n % 2 == 1) order.Add((n - 1) / 2);
            }

            double Sp(int i) => (gaps[i].b - gaps[i].a) / (perGap[i] + 2);   // separacion si se anade una mas

            for (int k = 0; k < count; k++)
            {
                int best = -1;
                if (order == null)
                {
                    double bestSp = -1;
                    for (int i = 0; i < n; i++)
                        if (Sp(i) >= minSpacing && Sp(i) > bestSp) { bestSp = Sp(i); best = i; }
                }
                else if (m == "center")
                {
                    for (int t = 0; t < n && best < 0; t++)
                    {
                        int i = order[(k + t) % n];
                        if (Sp(i) >= minSpacing) best = i;
                    }
                }
                else
                {
                    foreach (int i in order)
                        if (Sp(i) >= minSpacing) { best = i; break; }
                }
                if (best < 0) { missing = count - k; break; }
                perGap[best]++;
            }
            for (int i = 0; i < n; i++)
            {
                double gap = gaps[i].b - gaps[i].a;
                for (int k = 1; k <= perGap[i]; k++) added.Add((i, gaps[i].a + gap * k / (perGap[i] + 1)));
            }
            return added;
        }

        /// <summary>True si el lado de algun estribo (vertical si !horizontal) pasa por la coordenada dada y cubre el rango entero.</summary>
        private bool LegAt(bool horizontal, double coord, double from, double to)
        {
            double tol = Opt.Tol;
            double off = Math.Max(OffC, OffI);
            foreach (PlanStirrup s in Stirrups)
            {
                if (horizontal)
                {
                    foreach (double v in new[] { s.Line.V1, s.Line.V2 })
                        if (Math.Abs(v - coord) <= off + tol && s.Line.U1 <= from + tol && s.Line.U2 >= to - tol) return true;
                }
                else
                {
                    foreach (double u in new[] { s.Line.U1, s.Line.U2 })
                        if (Math.Abs(u - coord) <= off + tol && s.Line.V1 <= from + tol && s.Line.V2 >= to - tol) return true;
                }
            }
            return false;
        }

        private void AddBar(Pt p, bool required)
        {
            foreach (PlanBar b in Bars)
                if (b.P.DistanceTo(p) <= Opt.Tol) { b.Required |= required; return; }
            Bars.Add(new PlanBar { P = p, Required = required });
        }

        private void AddIntermediate(PlanStirrup s, int edge, double along, double offI)
        {
            Pt p = PointOn(s, edge, along, offI);
            foreach (PlanBar b in Bars)
                if (b.P.DistanceTo(p) <= Opt.Tol) return;
            Bars.Add(new PlanBar { P = p, Required = false, Stirrup = s.Index, Edge = edge });
        }

        /// <summary>
        /// Filas de barras del mismo diametro alineadas y equiespaciadas a lo largo de u
        /// (misma v): cada fila es un conjunto de Revit con "count" barras desde "first"
        /// cada "step". Las filas de una sola barra son conjuntos sencillos.
        /// </summary>

        public List<(Pt first, int count, double step, bool required)> ArrayRows(double tol)
        {
            var rows = new List<(Pt, int, double, bool)>();
            foreach (bool req in new[] { true, false })
            {
                List<PlanBar> pool = Bars.Where(b => b.Required == req).ToList();
                foreach (double v in Rectilinear.Cluster(pool.Select(b => b.P.V), tol))
                {
                    List<double> us = pool.Where(b => Math.Abs(b.P.V - v) <= tol).Select(b => b.P.U).OrderBy(u => u).ToList();
                    int i = 0;
                    while (i < us.Count)
                    {
                        if (i + 1 >= us.Count) { rows.Add((new Pt(us[i], v), 1, 0, req)); i++; continue; }
                        double step = us[i + 1] - us[i];
                        int j = i + 1;   // ultimo indice de la fila equiespaciada
                        while (j + 1 < us.Count && Math.Abs((us[j + 1] - us[j]) - step) <= tol) j++;
                        rows.Add((new Pt(us[i], v), j - i + 1, step, req));
                        i = j + 1;
                    }
                }
            }
            return rows;
        }
    }
}
