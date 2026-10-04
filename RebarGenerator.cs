using System;
using System.Collections.Generic;
using System.Linq;
using Arba.Comun;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ColumnRebar
{
    /// <summary>Un conjunto (elemento Rebar) creado para una columna.</summary>
    public sealed class CreatedSet
    {
        public ElementId Id;
        public string Name;
        /// <summary>Radio nominal de la barra (pies).</summary>
        public double Radius;
        /// <summary>Barra longitudinal: se comprueba solo el tramo dentro de la altura de la columna (puede sobresalir a proposito).</summary>
        public bool Longitudinal;
    }

    /// <summary>Resultado del armado de un elemento.</summary>
    public sealed class BuildResult
    {
        public List<CreatedSet> Created = new List<CreatedSet>();
        /// <summary>Barras que quedarian fuera del hormigon. Si hay alguna, el elemento entero se deshace.</summary>
        public List<string> Rejected = new List<string>();
        /// <summary>Conjuntos que Revit no pudo crear.</summary>
        public List<string> Failed = new List<string>();
        public List<string> Warnings = new List<string>();
        public int Bars, StirrupSets, TieSets;
        public string Summary => Bars + " longitudinales, " + StirrupSets + " tramos de estribos" + (TieSets > 0 ? ", " + TieSets + " tramos de grapas" : "");
        public bool Safe => Rejected.Count == 0;
    }

    public static class RebarGenerator
    {
        /// <summary>Valores de "ARBA - Codigo" que escribe este add-in (contrato ARBA-comun): barras verticales, estribo N y grapas.</summary>
        public const string CodeLongitudinal = "longitudinal", CodeStirrup = "estribo", CodeTie = "grapa";

        private static double Mm(double mm) => ColumnSection.Mm(mm);
        private static double ToMm(double ft) => ColumnSection.ToMm(ft);
        private const double MinSeg = 0.003;   // ~1 mm en pies
        /// <summary>Longitud de barra que se tolera fuera del solido al comprobar (pies, ~1 mm).</summary>
        private const double InsideTol = 0.0033;

        private sealed class Ctx
        {
            public Document Doc;
            public HostAnalysis Item;
            public ColumnSection S;
            public AppConfig Cfg;
            public BuildResult Result;
            public ColumnPlan Plan;
            public List<StirrupRun> Runs;
            public double Tol;
            /// <summary>Orientacion de los ganchos de estribos y grapas (se invierte sola si queda fuera del hormigon).</summary>
            public bool StirrupHookLeft, TieHookLeft, StirrupHookChecked, TieHookChecked;
            public ElementId StirrupHook = ElementId.InvalidElementId, TieHook = ElementId.InvalidElementId;
        }

        // =================================================================
        // Armado de una columna
        // =================================================================
        public static BuildResult Build(Document doc, HostAnalysis item, AppConfig cfg)
        {
            var c = new Ctx
            {
                Doc = doc, Item = item, S = item.Section, Cfg = cfg, Result = new BuildResult(),
                Tol = Mm(cfg.PrismCheckToleranceMm),
                StirrupHookLeft = cfg.HookLeft, TieHookLeft = cfg.TieHookLeft
            };

            RebarBarType btLong = FindBarType(doc, cfg.Longitudinal.BarTypeName, "longitudinales de esquina");
            RebarBarType btInter = string.IsNullOrWhiteSpace(cfg.Longitudinal.IntermediateBarTypeName)
                ? btLong : FindBarType(doc, cfg.Longitudinal.IntermediateBarTypeName, "longitudinales intermedias");
            RebarBarType btStirrup = FindBarType(doc, cfg.Stirrups.BarTypeName, "estribos");
            RebarBarType btTie = cfg.Crossties.Enabled ? FindBarType(doc, cfg.Crossties.BarTypeName, "grapas") : null;
            c.StirrupHook = FindHookType(doc, cfg.Stirrups.HookTypeName, c.Result.Warnings);
            c.TieHook = cfg.Crossties.Enabled ? FindHookType(doc, cfg.Crossties.HookTypeName, c.Result.Warnings) : ElementId.InvalidElementId;

            c.Plan = PlanFor(item, cfg, btLong.BarNominalDiameter, btInter.BarNominalDiameter, btStirrup.BarNominalDiameter,
                             btTie?.BarNominalDiameter ?? 0);
            if (c.Plan.Error != null) throw new InvalidOperationException(c.Plan.Error);
            if (c.Plan.InteriorError != null) throw new InvalidOperationException(c.Plan.InteriorError);
            c.Result.Warnings.AddRange(c.Plan.Warnings);

            c.Runs = RunsFor(item, cfg, out string warn);
            if (warn != null) c.Result.Warnings.Add(warn);
            if (c.Runs.Count == 0) throw new InvalidOperationException("la distribucion de estribos no produce ningun estribo");

            Longitudinals(c, btLong, btInter);
            if (!c.Result.Safe) return c.Result;
            Stirrups(c, btStirrup);
            if (!c.Result.Safe) return c.Result;
            if (btTie != null) Ties(c, btTie);
            return c.Result;
        }

        /// <summary>Armado de la seccion con esta configuracion (lo mismo que dibuja la ventana).</summary>
        public static ColumnPlan PlanFor(HostAnalysis item, AppConfig cfg, double dbCornerFt, double dbInterFt, double dsFt, double dtFt)
        {
            ColumnSection s = item.Section;
            var o = new PlanOptions
            {
                Cover = Mm(cfg.CoverMm), Ds = dsFt, DbCorner = dbCornerFt, DbInter = dbInterFt,
                Fill = cfg.Longitudinal.FillMode,
                Rows = item.RowOverrides, Cols = item.ColOverrides, Inner = item.InnerStirrups,
                TiesU = cfg.Crossties.Enabled && cfg.TiesU, TiesV = cfg.Crossties.Enabled && cfg.TiesV, Dt = dtFt,
                Tol = Mm(cfg.PrismCheckToleranceMm)
            };
            return ColumnPlan.Build(s.Polygon, s.Rects, o);
        }

        /// <summary>Tramos de estribos de esta columna con esta configuracion. Lanza si la distribucion no se entiende.</summary>
        public static List<StirrupRun> RunsFor(HostAnalysis item, AppConfig cfg, out string warning)
        {
            List<StirrupGroup> groups = StirrupLayout.Parse(item.Distribution(cfg), out string err);
            if (groups == null) throw new InvalidOperationException("distribucion de estribos \"" + item.Distribution(cfg) + "\": " + err);
            return StirrupLayout.Runs(item.Section.Height, Mm(cfg.Stirrups.BottomOffsetMm), Mm(cfg.Stirrups.TopOffsetMm),
                                      groups, cfg.Stirrups.Symmetric, out warning);
        }

        // -----------------------------------------------------------------
        // Longitudinales: una fila equiespaciada = un conjunto con array a lo largo de u
        // -----------------------------------------------------------------
        private static void Longitudinals(Ctx c, RebarBarType btCorner, RebarBarType btInter)
        {
            ColumnSection s = c.S;
            LongitudinalCfg L = c.Cfg.Longitudinal;
            double zBot = s.ZBase - Mm(L.BottomExtensionMm);
            double zTop = s.ZTop + Mm(L.TopExtensionMm);
            double leg = L.BottomExtensionMm > 0 ? Mm(L.BottomLegMm) : 0;
            if (L.BottomLegMm > 0 && L.BottomExtensionMm <= 0)
                c.Result.Warnings.Add("la patilla inferior se ignora porque las barras no sobresalen por debajo de la base");
            if (zTop - zBot < MinSeg) { c.Result.Rejected.Add("longitudinales sin longitud"); return; }
            Pt centroid = Rectilinear.Centroid(s.Polygon);
            // patilla hacia fuera (lo normal en el arranque) o hacia el centro de la seccion
            double sign = L.LegOutward ? -1 : 1;

            foreach ((Pt first, int count, double step, bool required) in c.Plan.ArrayRows(c.Tol))
            {
                RebarBarType bt = required ? btCorner : btInter;
                XYZ normal;
                XYZ legDir = null;
                if (count > 1)
                {
                    normal = s.DirU;
                    if (leg > 0) legDir = s.DirV * (sign * (centroid.V >= first.V ? 1 : -1));
                }
                else
                {
                    double du = centroid.U - first.U, dv = centroid.V - first.V;
                    if (leg > 0 && Math.Abs(du) > Math.Abs(dv)) { legDir = s.DirU * (sign * (du >= 0 ? 1 : -1)); normal = s.DirV; }
                    else { if (leg > 0) legDir = s.DirV * (sign * (dv >= 0 ? 1 : -1)); normal = s.DirU; }
                }

                XYZ bottom = s.World(first.U, first.V, zBot);
                XYZ top = s.World(first.U, first.V, zTop);
                var curves = new List<Curve>();
                if (leg > 0) AddLine(curves, bottom + legDir * leg, bottom);
                AddLine(curves, bottom, top);

                string name = (required ? "longitudinal esquina" : "longitudinal intermedia") + " v=" + ToMm(first.V) +
                              (count > 1 ? " (" + count + " barras cada " + ToMm(step) + " mm)" : " u=" + ToMm(first.U));
                Place(c, name, bt, RebarStyle.Standard, ElementId.InvalidElementId, true, normal, curves, count, step,
                      longitudinal: true, stirrup: "", code: CodeLongitudinal);
                c.Result.Bars += count;
            }
        }

        // -----------------------------------------------------------------
        // Estribos cerrados: un conjunto por rectangulo (y por estribo interior) y tramo de la distribucion
        // -----------------------------------------------------------------
        private static void Stirrups(Ctx c, RebarBarType bt)
        {
            ColumnSection s = c.S;
            foreach (PlanStirrup st in c.Plan.Stirrups)
            {
                Rect r = st.Line;
                foreach (StirrupRun run in c.Runs)
                {
                    double z = s.ZBase + run.Z0;
                    var curves = new List<Curve>();
                    if (st.Octagonal)
                    {
                        // octogonal: sus vertices ya vienen antihorarios empezando donde van los ganchos
                        for (int i = 0; i < st.Path.Count; i++)
                        {
                            Pt a = st.Path[i], b = st.Path[(i + 1) % st.Path.Count];
                            AddLine(curves, s.World(a.U, a.V, z), s.World(b.U, b.V, z));
                        }
                    }
                    else
                    {
                        // antihorario visto desde arriba, empezando y acabando en la esquina superior izquierda (ahi van los ganchos)
                        XYZ p1 = s.World(r.U1, r.V2, z), p2 = s.World(r.U1, r.V1, z);
                        XYZ p3 = s.World(r.U2, r.V1, z), p4 = s.World(r.U2, r.V2, z);
                        AddLine(curves, p1, p2); AddLine(curves, p2, p3); AddLine(curves, p3, p4); AddLine(curves, p4, p1);
                    }
                    string name = "estribo " + (st.Index + 1) + (st.Octagonal ? " (octogonal)" : st.Interior ? " (interior)" : "") + " " + run.Label;
                    bool ok = Place(c, name, bt, RebarStyle.StirrupTie, c.StirrupHook, c.StirrupHookLeft, XYZ.BasisZ, curves,
                                    run.Count, run.Spacing, longitudinal: false, stirrup: (st.Index + 1).ToString(),
                                    code: CodeStirrup + " " + (st.Index + 1),
                                    checkHooks: c.StirrupHook != ElementId.InvalidElementId && !c.StirrupHookChecked,
                                    flip: () => c.StirrupHookLeft = !c.StirrupHookLeft);
                    if (c.StirrupHook != ElementId.InvalidElementId) c.StirrupHookChecked = true;
                    if (!ok) return;
                    c.Result.StirrupSets++;
                }
            }
        }

        // -----------------------------------------------------------------
        // Grapas: una por par de intermedias enfrentadas, en cada cota de estribo
        // -----------------------------------------------------------------
        private static void Ties(Ctx c, RebarBarType bt)
        {
            ColumnSection s = c.S;
            int n = 0;
            foreach (PlanTie t in c.Plan.Ties)
            {
                n++;
                foreach (StirrupRun run in c.Runs)
                {
                    double z = s.ZBase + run.Z0;
                    var curves = new List<Curve>();
                    AddLine(curves, s.World(t.A.U, t.A.V, z), s.World(t.B.U, t.B.V, z));
                    string name = "grapa " + n + " estribo " + (t.Stirrup + 1) + " " + run.Label;
                    bool ok = Place(c, name, bt, RebarStyle.StirrupTie, c.TieHook, c.TieHookLeft, XYZ.BasisZ, curves,
                                    run.Count, run.Spacing, longitudinal: false, stirrup: (t.Stirrup + 1).ToString(),
                                    code: CodeTie,
                                    checkHooks: c.TieHook != ElementId.InvalidElementId && !c.TieHookChecked,
                                    flip: () => c.TieHookLeft = !c.TieHookLeft);
                    if (c.TieHook != ElementId.InvalidElementId) c.TieHookChecked = true;
                    if (!ok) return;
                    c.Result.TieSets++;
                }
            }
        }

        // =================================================================
        // Colocacion con red de seguridad
        // =================================================================

        /// <summary>
        /// Comprueba que la barra (y todas las posiciones del array) queda dentro del
        /// hormigon y solo entonces la crea. Con "checkHooks", tras crearla lee su geometria
        /// real (ganchos incluidos); si los ganchos asoman, la borra, invierte la orientacion
        /// (flip) y la vuelve a crear. False si algo se rechazo.
        /// </summary>
        private static bool Place(Ctx c, string name, RebarBarType bt, RebarStyle style, ElementId hook, bool hookLeft,
                                  XYZ normal, List<Curve> curves, int count, double spacing, bool longitudinal, string stirrup,
                                  string code, bool checkHooks = false, Action flip = null)
        {
            normal = normal.Normalize();
            bool array = count >= 2 && spacing > MinSeg;
            double r = bt.BarNominalDiameter * 0.5;

            // --- RED DE SEGURIDAD (1): geometria planificada, antes de crear nada ---
            for (int k = 0; k < (array ? count : 1); k++)
            {
                IList<Curve> moved = curves;
                if (k > 0)
                {
                    Transform t = Transform.CreateTranslation(normal * (k * spacing));
                    moved = curves.Select(cv => cv.CreateTransformed(t)).ToList();
                }
                if (!BarInside(c.S, moved, r, longitudinal, out string why))
                {
                    c.Result.Rejected.Add(name + (k > 0 ? " (posicion " + (k + 1) + " del array)" : "") + ": " + why);
                    return false;
                }
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                Rebar rb = Create(c.Doc, c.S.Host, bt, style, hook, hookLeft, normal, curves, out string err);
                if (rb == null) { c.Result.Failed.Add(name + ": Revit no pudo crear la barra (" + err + ")"); return true; }

                if (array) rb.GetShapeDrivenAccessor().SetLayoutAsFixedNumber(count, (count - 1) * spacing, true, true, true);
                else rb.GetShapeDrivenAccessor().SetLayoutAsSingle();

                if (checkHooks && flip != null)
                {
                    // RED DE SEGURIDAD (1b): los ganchos solo existen en la geometria real
                    c.Doc.Regenerate();
                    if (!RealInside(c.S, rb, r, longitudinal, out string why))
                    {
                        c.Doc.Delete(rb.Id);
                        if (attempt == 0)
                        {
                            flip();
                            hookLeft = !hookLeft;
                            c.Result.Warnings.Add(name + ": los ganchos quedaban fuera del hormigon, se ha invertido su orientacion");
                            continue;
                        }
                        c.Result.Rejected.Add(name + ": " + why + " (con las dos orientaciones de gancho)");
                        return false;
                    }
                }

                Finish(c.Doc, rb, c.S.Host, c.Item.Partition(c.Cfg, SetName(name), stirrup), code);
                c.Result.Created.Add(new CreatedSet { Id = rb.Id, Name = name, Radius = r, Longitudinal = longitudinal });
                return true;
            }
            return false;
        }

        /// <summary>
        /// RED DE SEGURIDAD (2): tras crear y regenerar, se lee la geometria REAL de cada
        /// barra de cada conjunto tal y como la ha colocado Revit (radios de doblado, ganchos
        /// y todas las posiciones del array) y se comprueba contra el solido. Las
        /// longitudinales se comprueban solo en la altura de la columna (pueden sobresalir
        /// a proposito por arriba y por abajo).
        /// </summary>
        public static void VerifyCreated(Document doc, ColumnSection s, BuildResult res)
        {
            foreach (CreatedSet cs in res.Created)
            {
                var rb = doc.GetElement(cs.Id) as Rebar;
                if (rb == null) { res.Rejected.Add(cs.Name + ": el conjunto no existe tras regenerar"); continue; }
                if (!RealInside(s, rb, cs.Radius, cs.Longitudinal, out string why)) res.Rejected.Add(cs.Name + ": " + why);
            }
        }

        private static bool RealInside(ColumnSection s, Rebar rb, double r, bool longitudinal, out string why)
        {
            why = null;
            int n;
            try { n = rb.NumberOfBarPositions; }
            catch (Exception ex) { why = "no se pudo leer el conjunto (" + ex.Message + ")"; return false; }
            for (int k = 0; k < n; k++)
            {
                IList<Curve> cl;
                try
                {
                    if (!rb.DoesBarExistAtPosition(k)) continue;
                    cl = rb.GetCenterlineCurves(false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, k);
                }
                catch (Exception ex) { why = "barra " + (k + 1) + " de " + n + ": no se pudo leer su geometria (" + ex.Message + ")"; return false; }
                if (cl == null || cl.Count == 0) { why = "barra " + (k + 1) + " de " + n + ": sin geometria"; return false; }
                if (!BarInside(s, cl, r, longitudinal, out string w)) { why = "barra " + (k + 1) + " de " + n + ": " + w; return false; }
            }
            return true;
        }

        /// <summary>
        /// True si toda la barra queda dentro del solido. Ademas del eje se comprueban fibras
        /// extremas (eje desplazado +-r en cada direccion local; en las longitudinales solo en
        /// el plano de la seccion), asi una barra tangente a una cara o con medio diametro
        /// fuera tambien falla. Las longitudinales se recortan a la altura de la columna.
        /// </summary>
        private static bool BarInside(ColumnSection s, IList<Curve> curves, double r, bool longitudinal, out string why)
        {
            why = null;
            var shifts = new List<XYZ> { XYZ.Zero, s.DirU * r, s.DirU * -r, s.DirV * r, s.DirV * -r };
            if (!longitudinal) { shifts.Add(XYZ.BasisZ * r); shifts.Add(XYZ.BasisZ * -r); }

            IEnumerable<Curve> toCheck = longitudinal
                ? curves.SelectMany(cv => ClipToRange(cv, s.ZBase + InsideTol, s.ZTop - InsideTol))
                : curves;
            foreach (Curve cv in toCheck)
                foreach (XYZ sh in shifts)
                {
                    Curve probe = sh.IsZeroLength() ? cv : cv.CreateTransformed(Transform.CreateTranslation(sh));
                    if (!CurveInside(s.HostSolid, probe, out double outside))
                    {
                        why = "queda fuera del hormigon (" + ToMm(outside) + " mm de barra fuera; segmento de " +
                              s.LocalMm(cv.GetEndPoint(0)) + " a " + s.LocalMm(cv.GetEndPoint(1)) + ")";
                        return false;
                    }
                }
            return true;
        }

        /// <summary>Trozos de la curva (como lineas) con z entre z0 y z1; lo que queda fuera de ese rango no se comprueba.</summary>
        private static List<Curve> ClipToRange(Curve cv, double z0, double z1)
        {
            var result = new List<Curve>();
            IList<XYZ> pts = cv.Tessellate();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                XYZ a = pts[i], b = pts[i + 1];
                if (Math.Max(a.Z, b.Z) < z0 || Math.Min(a.Z, b.Z) > z1) continue;
                XYZ lo = a, hi = b;
                if (Math.Abs(b.Z - a.Z) > 1e-12)
                {
                    double ta = Math.Max(0, Math.Min(1, (z0 - a.Z) / (b.Z - a.Z)));
                    double tb = Math.Max(0, Math.Min(1, (z1 - a.Z) / (b.Z - a.Z)));
                    double t0 = Math.Min(ta, tb), t1 = Math.Max(ta, tb);
                    if (a.Z < z0 || a.Z > z1) lo = a + (b - a) * (a.Z < b.Z ? t0 : t1);
                    if (b.Z < z0 || b.Z > z1) hi = a + (b - a) * (a.Z < b.Z ? t1 : t0);
                }
                if (lo.DistanceTo(hi) > MinSeg) result.Add(Line.CreateBound(lo, hi));
            }
            return result;
        }

        /// <summary>Longitud de la curva que queda fuera del solido; no verificable cuenta como fuera.</summary>
        private static bool CurveInside(Solid solid, Curve cv, out double outsideLen)
        {
            outsideLen = cv.Length;
            try
            {
                var opt = new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside };
                SolidCurveIntersection ix = solid.IntersectWithCurve(cv, opt);
                double inside = 0;
                if (ix != null)
                    for (int i = 0; i < ix.SegmentCount; i++) inside += ix.GetCurveSegment(i).Length;
                outsideLen = Math.Max(0, cv.Length - inside);
                return outsideLen <= InsideTol;
            }
            catch { return false; }
        }

        // =================================================================
        // Utilidades
        // =================================================================
        private static string SetName(string name) =>
            System.Text.RegularExpressions.Regex.Replace(name, @"\s+(v=|u=|\(|inf|sup|resto).*$", "").Trim();

        private static void AddLine(List<Curve> list, XYZ a, XYZ b)
        {
            if (a.DistanceTo(b) > MinSeg) list.Add(Line.CreateBound(a, b));
        }

        private static Rebar Create(Document doc, Element host, RebarBarType bt, RebarStyle style, ElementId hook, bool hookLeft,
                                    XYZ normal, IList<Curve> curves, out string err)
        {
            err = null;
            try
            {
                // Revit 2027: ganchos y tratamientos de extremo van agrupados en BarTerminationsData.
                using (BarTerminationsData term = new BarTerminationsData(doc))
                {
                    if (hook != null && hook != ElementId.InvalidElementId)
                    {
                        term.HookTypeIdAtStart = hook;
                        term.HookTypeIdAtEnd = hook;
                    }
                    RebarTerminationOrientation o = hookLeft ? RebarTerminationOrientation.Left : RebarTerminationOrientation.Right;
                    term.TerminationOrientationAtStart = o;
                    term.TerminationOrientationAtEnd = o;
                    return Rebar.CreateFromCurves(doc, style, bt, host, normal.Normalize(), curves, term, true, true);
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Marca el conjunto recien creado segun el contrato ARBA-comun: Particion (parametro predefinido,
        /// con respaldo por nombre en ingles y espanol; LookupParameter("Partition") no escribia nada en
        /// Revit en espanol), "ARBA - Origen" = COLUMNAS, "ARBA - Codigo" (longitudinal / estribo N / grapa)
        /// y "Metrado - Elemento" = COLUMNAS. Los parametros compartidos los asegura el comando antes de armar.
        /// </summary>
        private static void Finish(Document doc, Rebar r, Element host, string partition, string code)
        {
            ArbaPartition.Write(r, partition);
            ArbaOrigin.WriteFor(r, host, ArbaContract.Columnas, code);
            try { r.SetUnobscuredInView(doc.ActiveView, true); } catch { }
        }

        public static RebarBarType FindBarType(Document doc, string name, string use)
        {
            var all = AllBarTypes(doc);
            if (all.Count == 0)
                throw new InvalidOperationException("El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.");
            string match = NameMatch.First(all.Select(b => b.Name), name);
            if (match == null)
                throw new InvalidOperationException("el tipo de barra de " + use + " \"" + name + "\" no existe en este proyecto; elige uno de los cargados en la ventana");
            return all.First(b => b.Name == match);
        }

        /// <summary>
        /// Catalogo de ganchos de estilo Estribo/atadura, el unico que Revit admite en estribos y grapas: 90 grados
        /// con prolongacion de 6 diametros, 135 con 6, 135 sismico con 8 y 180 con 4 (los mismos que trae Revit).
        /// La ventana ofrece los de este catalogo cuyo angulo no tenga el proyecto entre sus ganchos de ese estilo,
        /// y el tipo se crea en el proyecto al armar (FindHookType).
        /// </summary>
        public static readonly (string Name, double AngleDeg, double Multiplier)[] HookCatalog =
        {
            ("Estribo - 90", 90, 6),
            ("Estribo - 135", 135, 6),
            ("Estribo sismico - 135", 135, 8),
            ("Estribo - 180", 180, 4),
        };

        /// <summary>
        /// Id del tipo de gancho, o InvalidElementId si el nombre esta vacio. Si el proyecto no lo tiene pero es
        /// del catalogo, lo crea (se llama dentro de la transaccion del armado) y lo anota en "notes". Lanza si
        /// el nombre no existe o es un gancho de estilo Estandar.
        /// </summary>
        public static ElementId FindHookType(Document doc, string name, List<string> notes = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
            var all = AllHookTypes(doc);
            string match = NameMatch.First(all.Select(h => h.Name), name);
            if (match != null) return all.First(h => h.Name == match).Id;

            string entry = NameMatch.First(HookCatalog.Select(e => e.Name), name);
            if (entry != null) return CreateCatalogHook(doc, HookCatalog.First(e => e.Name == entry), notes);

            // Puede que el nombre exista pero sea un gancho de estilo Estandar: Revit no lo admite en barras de
            // estilo Estribo/atadura (falla al crear la barra), asi que se avisa claro.
            string other = NameMatch.First(HookTypesOf(doc).Select(h => h.Name), name);
            if (other != null)
                throw new InvalidOperationException("el tipo de gancho \"" + other + "\" es de estilo Estandar y Revit no lo admite en estribos ni grapas (estilo Estribo/atadura); elige uno de estilo Estribo/atadura (p. ej. \"Estribo - 135\") o deja el gancho vacio");
            throw new InvalidOperationException("el tipo de gancho \"" + name + "\" no existe en este proyecto; elige uno de los cargados en la ventana o deja el gancho vacio");
        }

        /// <summary>Crea en el proyecto el gancho Estribo/atadura del catalogo, admitido por todos los tipos de barra.</summary>
        private static ElementId CreateCatalogHook(Document doc, (string Name, double AngleDeg, double Multiplier) e, List<string> notes)
        {
            RebarHookType h = RebarHookType.Create(doc, e.AngleDeg * Math.PI / 180, e.Multiplier);
            try { h.Style = RebarStyle.StirrupTie; } catch { }
            try { h.Name = e.Name; } catch { }
            foreach (RebarBarType bt in AllBarTypes(doc))
                try { if (!bt.GetHookPermission(h.Id)) bt.SetHookPermission(h.Id, true); } catch { }
            notes?.Add("creado en el proyecto el tipo de gancho \"" + h.Name + "\" (Estribo/atadura, " + e.AngleDeg + " grados, prolongacion " + e.Multiplier + " diametros)");
            return h.Id;
        }

        public static List<RebarBarType> AllBarTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
                .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>
        /// Tipos de gancho de estilo Estribo/atadura, los unicos que Revit admite en los estribos y las grapas
        /// (se crean con RebarStyle.StirrupTie). Los de estilo Estandar se excluyen: asignados a una barra de
        /// estilo Estribo/atadura, Revit falla al crearla.
        /// </summary>
        public static List<RebarHookType> AllHookTypes(Document doc) =>
            HookTypesOf(doc).Where(IsStirrupHook)
                .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Todos los tipos de gancho del proyecto, de cualquier estilo.</summary>
        private static List<RebarHookType> HookTypesOf(Document doc)
        {
            var list = new FilteredElementCollector(doc).OfClass(typeof(RebarHookType)).Cast<RebarHookType>().ToList();
            // respaldo: recorrer los tipos del proyecto por si el filtro por clase no los devuelve
            if (list.Count == 0) list = new FilteredElementCollector(doc).WhereElementIsElementType().OfType<RebarHookType>().ToList();
            return list;
        }

        /// <summary>True si el gancho es de estilo Estribo/atadura (si la API no lo dice, se admite).</summary>
        public static bool IsStirrupHook(RebarHookType h)
        {
            try { return h.Style == RebarStyle.StirrupTie; } catch { return true; }
        }
    }
}
