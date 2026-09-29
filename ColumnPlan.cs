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
        /// <summary>Coordenada a lo largo del lado (u en lados horizontales, v en verticales).</summary>
        public double Along;
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

    /// <summary>
    /// Numero de barras de un estribo en modo "por numero": total arriba, total abajo
    /// (esquinas incluidas) e intermedias por costado (sin las esquinas; los cruces con
    /// otros estribos cuentan como intermedias).
    /// </summary>
    public sealed class BarCounts
    {
        public int Top = 3, Bottom = 3, Side = 1;
        /// <summary>
        /// Donde van las barras que se anaden cuando el lado esta partido por otro estribo:
        /// "auto" = al hueco mas grande; "left" = al hueco de la izquierda (en los costados,
        /// el de abajo); "right" = al de la derecha (arriba); "center" = simetrico, alternando
        /// izquierda y derecha (o el hueco central primero). "" = el general.
        /// </summary>
        public string Fill = "";
        public BarCounts Clone() => new BarCounts { Top = Top, Bottom = Bottom, Side = Side, Fill = Fill };
        public override string ToString() => Top + "/" + Bottom + "/" + Side;
    }

    /// <summary>Todo lo que necesita ColumnPlan.Build (en pies).</summary>
    public sealed class PlanOptions
    {
        public double Cover;
        /// <summary>Diametro del estribo, de las barras de esquina y de las intermedias.</summary>
        public double Ds, DbCorner, DbInter;
        /// <summary>true = intermedias por numero (Counts); false = por separacion maxima (MaxSpacing).</summary>
        public bool ByCount;
        public double MaxSpacing;
        /// <summary>Numero de barras por estribo (indice del rectangulo); null o fuera de rango = Default.</summary>
        public IList<BarCounts> Counts;
        public BarCounts DefaultCounts = new BarCounts();
        public bool TiesU, TiesV;
        public double Dt;
        public double Tol;

        public BarCounts CountsFor(int stirrup) =>
            Counts != null && stirrup < Counts.Count && Counts[stirrup] != null ? Counts[stirrup] : DefaultCounts;
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
    ///  - las intermedias (su propio diametro) se reparten a lo largo de cada lado entre las
    ///    obligadas: por separacion maxima, o por numero (total arriba y abajo, intermedias
    ///    por costado), siempre en los huecos mas grandes para que queden lo mas uniformes
    ///    posible respetando las obligadas;
    ///  - grapas opcionales entre cada par de intermedias enfrentadas (misma coordenada a lo
    ///    largo del lado) que no tenga ya un lado de otro estribo pasando por ahi.
    /// </summary>
    public sealed class ColumnPlan
    {
        public List<PlanStirrup> Stirrups = new List<PlanStirrup>();
        public List<PlanBar> Bars = new List<PlanBar>();
        public List<PlanTie> Ties = new List<PlanTie>();
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

        /// <summary>Resumen por estribo: "E1 4/4/1" (arriba/abajo/intermedias por costado).</summary>
        public string DescribeCounts()
        {
            var parts = new List<string>();
            foreach (PlanStirrup s in Stirrups)
                parts.Add("E" + (s.Index + 1) + " " + EdgeBars(s, 1).Count + "/" + EdgeBars(s, 0).Count + "/" +
                          EdgeBars(s, 2).Count(b => !IsCornerOf(s, b)));
            return string.Join(", ", parts);
        }

        // lados: 0 abajo (v = Line.V1, hacia +v), 1 arriba (v = Line.V2, hacia -v), 2 izquierda (u = Line.U1, hacia +u), 3 derecha (u = Line.U2, hacia -u)
        private static double LineCoord(PlanStirrup s, int edge) =>
            edge == 0 ? s.Line.V1 : edge == 1 ? s.Line.V2 : edge == 2 ? s.Line.U1 : s.Line.U2;
        private static double Inward(int edge) => edge == 0 || edge == 2 ? 1 : -1;
        private static bool Horizontal(int edge) => edge < 2;

        private Pt PointOn(PlanStirrup s, int edge, double along, double off)
        {
            double perp = LineCoord(s, edge) + Inward(edge) * off;
            return Horizontal(edge) ? new Pt(along, perp) : new Pt(perp, along);
        }

        /// <summary>Barras (obligadas o intermedias, con cualquiera de los dos diametros) apoyadas en el lado dado, ordenadas a lo largo.</summary>
        public List<PlanBar> EdgeBars(PlanStirrup s, int edge)
        {
            double offC = 0.5 * Opt.Ds + 0.5 * Opt.DbCorner, offI = 0.5 * Opt.Ds + 0.5 * Opt.DbInter;
            double lo = Math.Min(offC, offI) - Opt.Tol, hi = Math.Max(offC, offI) + Opt.Tol;
            double line = LineCoord(s, edge), inw = Inward(edge);
            double a0 = Horizontal(edge) ? s.BarLine.U1 : s.BarLine.V1;
            double a1 = Horizontal(edge) ? s.BarLine.U2 : s.BarLine.V2;
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

        public static ColumnPlan Build(IList<Pt> polygon, IList<Rect> rects, PlanOptions o)
        {
            var plan = new ColumnPlan { Opt = o };
            double tol = o.Tol;
            if (rects == null || rects.Count == 0) { plan.Error = "la seccion no tiene ningun rectangulo"; return plan; }
            double offC = 0.5 * o.Ds + 0.5 * o.DbCorner;   // del eje del estribo al eje de la barra de esquina
            double offI = 0.5 * o.Ds + 0.5 * o.DbInter;    // idem para una intermedia

            // --- estribos ---
            for (int i = 0; i < rects.Count; i++)
            {
                Rect line = rects[i].Inset(o.Cover + 0.5 * o.Ds);
                Rect bar = line.Inset(offC);
                if (bar.W <= tol || bar.H <= tol)
                {
                    plan.Error = "el estribo " + (i + 1) + " (" + ToMm(rects[i].W) + " x " + ToMm(rects[i].H) +
                                 " mm) no cabe con recubrimiento " + ToMm(o.Cover) + " mm y estas barras";
                    return plan;
                }
                plan.Stirrups.Add(new PlanStirrup { Index = i, Concrete = rects[i].Clone(), Line = line, BarLine = bar });
            }

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

            // --- intermedias, por pares de lados enfrentados de cada estribo ---
            foreach (PlanStirrup s in plan.Stirrups)
            {
                BarCounts n = o.CountsFor(s.Index);
                foreach ((int e1, int e2) in new[] { (0, 1), (2, 3) })
                {
                    List<double> f1 = plan.EdgeBars(s, e1).Select(b => Horizontal(e1) ? b.P.U : b.P.V).ToList();
                    List<double> f2 = plan.EdgeBars(s, e2).Select(b => Horizontal(e2) ? b.P.U : b.P.V).ToList();
                    List<double> add1, add2;
                    if (o.ByCount)
                    {
                        // arriba/abajo: total de barras en el lado; costados: 2 esquinas + intermedias (los cruces
                        // con otros estribos cuentan como intermedias). Los estribos se recorren en orden (E1, E2...):
                        // las barras que ya puso un estribo anterior sobre el mismo lado cuentan para este, asi
                        // en un lado compartido manda el primero.
                        int want1 = e1 == 0 ? n.Bottom : 2 + Math.Max(0, n.Side);
                        int want2 = e2 == 1 ? n.Top : 2 + Math.Max(0, n.Side);
                        double minSp = 2.5 * o.DbInter;   // 1.5 diametros libres entre barras
                        add1 = FillByCount(Rectilinear.Cluster(f1, tol), want1 - f1.Count, minSp, n.Fill, out int miss1);
                        add2 = FillByCount(Rectilinear.Cluster(f2, tol), want2 - f2.Count, minSp, n.Fill, out int miss2);
                        if (miss1 > 0) plan.Warnings.Add("E" + (s.Index + 1) + " " + EdgeName(e1) + ": no caben " + miss1 + " barra(s) mas con 1.5 diametros libres");
                        if (miss2 > 0) plan.Warnings.Add("E" + (s.Index + 1) + " " + EdgeName(e2) + ": no caben " + miss2 + " barra(s) mas con 1.5 diametros libres");
                    }
                    else
                    {
                        // las mismas posiciones en los dos lados enfrentados (para poder atarlas con grapas)
                        List<double> union = Rectilinear.Cluster(f1.Concat(f2), tol);
                        List<double> filled = FillBySpacing(union, o.MaxSpacing);
                        add1 = filled.Where(x => !f1.Any(y => Math.Abs(x - y) <= tol)).ToList();
                        add2 = filled.Where(x => !f2.Any(y => Math.Abs(x - y) <= tol)).ToList();
                    }
                    foreach (double x in add1) plan.AddIntermediate(s, e1, x, offI);
                    foreach (double x in add2) plan.AddIntermediate(s, e2, x, offI);
                }
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

        /// <summary>Anade a la lista ordenada las posiciones intermedias necesarias para no superar "maxSpacing".</summary>
        private static List<double> FillBySpacing(List<double> sorted, double maxSpacing)
        {
            var result = new List<double>(sorted);
            if (maxSpacing <= 1e-9) return result;
            for (int i = 0; i + 1 < sorted.Count; i++)
            {
                double gap = sorted[i + 1] - sorted[i];
                int n = (int)Math.Ceiling(gap / maxSpacing - 1e-9) - 1;
                for (int k = 1; k <= n; k++) result.Add(sorted[i] + gap * k / (n + 1));
            }
            return result.OrderBy(x => x).ToList();
        }

        private static string EdgeName(int edge) => edge == 0 ? "abajo" : edge == 1 ? "arriba" : edge == 2 ? "costado izquierdo" : "costado derecho";

        /// <summary>
        /// Reparte "count" barras nuevas entre las posiciones fijas segun "mode":
        ///  "auto"   = cada una al hueco con la separacion resultante mas grande (lo mas uniforme posible);
        ///  "left"   = al primer hueco empezando por la izquierda (abajo en los costados) que la admita;
        ///  "right"  = idem empezando por la derecha (arriba);
        ///  "center" = simetrico: por pares izquierda-derecha, el par mas cercano al centro primero; el hueco central al final.
        /// No se anade ninguna que deje una separacion menor que "minSpacing" (eje a eje);
        /// "missing" dice cuantas se han quedado sin colocar. Devuelve solo las nuevas.
        /// </summary>
        private static List<double> FillByCount(List<double> sorted, int count, double minSpacing, string mode, out int missing)
        {
            var added = new List<double>();
            missing = 0;
            if (count <= 0 || sorted.Count < 2) { missing = Math.Max(0, count); return added; }
            int n = sorted.Count - 1;
            var perGap = new int[n];   // barras nuevas en cada hueco
            string m = (mode ?? "").Trim().ToLowerInvariant();

            // orden de preferencia de los huecos (null = por separacion)
            List<int> order = null;
            if (m == "left") order = Enumerable.Range(0, n).ToList();
            else if (m == "right") order = Enumerable.Range(0, n).Reverse().ToList();
            else if (m == "center")
            {
                // por pares simetricos (izquierda y despues derecha), el par mas cercano al centro
                // primero; el hueco central, si lo hay, queda para el final
                order = Enumerable.Range(0, n).Where(i => 2 * i != n - 1)
                                  .OrderBy(i => Math.Abs(2 * i - (n - 1))).ThenBy(i => i).ToList();
                if (n % 2 == 1) order.Add((n - 1) / 2);
            }

            for (int k = 0; k < count; k++)
            {
                int best = -1;
                if (order == null)
                {
                    double bestSp = -1;
                    for (int i = 0; i < n; i++)
                    {
                        double sp = (sorted[i + 1] - sorted[i]) / (perGap[i] + 2);   // separacion si se anade una mas
                        if (sp >= minSpacing && sp > bestSp) { bestSp = sp; best = i; }
                    }
                }
                else if (m == "center")
                {
                    // la k-esima barra va al k-esimo hueco del orden simetrico; si no cabe, al siguiente
                    for (int t = 0; t < n && best < 0; t++)
                    {
                        int i = order[(k + t) % n];
                        if ((sorted[i + 1] - sorted[i]) / (perGap[i] + 2) >= minSpacing) best = i;
                    }
                }
                else
                {
                    // todas al primer hueco del orden que las admita
                    foreach (int i in order)
                        if ((sorted[i + 1] - sorted[i]) / (perGap[i] + 2) >= minSpacing) { best = i; break; }
                }
                if (best < 0) { missing = count - k; break; }
                perGap[best]++;
            }
            for (int i = 0; i < n; i++)
            {
                double gap = sorted[i + 1] - sorted[i];
                for (int k = 1; k <= perGap[i]; k++) added.Add(sorted[i] + gap * k / (perGap[i] + 1));
            }
            return added;
        }

        /// <summary>True si el lado de algun estribo (vertical si !horizontal) pasa por la coordenada dada y cubre el rango entero.</summary>
        private bool LegAt(bool horizontal, double coord, double from, double to)
        {
            double tol = Opt.Tol;
            double offC = 0.5 * Opt.Ds + 0.5 * Opt.DbCorner, offI = 0.5 * Opt.Ds + 0.5 * Opt.DbInter;
            foreach (PlanStirrup s in Stirrups)
            {
                if (horizontal)
                {
                    foreach (double v in new[] { s.Line.V1, s.Line.V2 })
                        if (Math.Abs(v - coord) <= Math.Max(offC, offI) + tol && s.Line.U1 <= from + tol && s.Line.U2 >= to - tol) return true;
                }
                else
                {
                    foreach (double u in new[] { s.Line.U1, s.Line.U2 })
                        if (Math.Abs(u - coord) <= Math.Max(offC, offI) + tol && s.Line.V1 <= from + tol && s.Line.V2 >= to - tol) return true;
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
            Bars.Add(new PlanBar { P = p, Required = false, Stirrup = s.Index, Edge = edge, Along = along });
        }

        /// <summary>
        /// Filas de barras del mismo diametro alineadas y equiespaciadas a lo largo de u
        /// (misma v): cada fila es un conjunto de Revit con "count" barras desde "first"
        /// cada "step". Las filas de una sola barra son conjuntos sencillos.
        /// </summary>
        public List<(Pt first, int count, double step, bool required)> Rows(double tol)
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
