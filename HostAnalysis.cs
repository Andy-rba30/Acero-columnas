using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace ColumnRebar
{
    /// <summary>
    /// Resultado del analisis de un elemento seleccionado, antes de armar nada: la seccion
    /// deducida o el motivo del rechazo, mas las elecciones por elemento hechas en la
    /// ventana (distribucion de estribos y separacion de longitudinales propias).
    /// </summary>
    public sealed class HostAnalysis
    {
        public Element Host;
        public string Tag;
        public string Mark = "", TypeName = "", FamilyName = "";

        public ColumnSection Section;
        public string Error;

        /// <summary>Distribucion de estribos propia de este elemento ("" = la de la configuracion).</summary>
        public string DistributionOverride = "";
        /// <summary>Separacion maxima de longitudinales propia (0 = la de la configuracion).</summary>
        public double SpacingOverride = 0;
        /// <summary>Modo por numero: barras de cada estribo de esta columna (indice = rectangulo; null = valores generales).</summary>
        public List<BarCounts> Counts = new List<BarCounts>();

        /// <summary>Valores generales de la configuracion.</summary>
        public static BarCounts General(AppConfig cfg) =>
            new BarCounts { Top = cfg.Longitudinal.TopCount, Bottom = cfg.Longitudinal.BottomCount, Side = cfg.Longitudinal.SideCount };

        /// <summary>
        /// Barras del estribo dado: el valor general es el minimo; las propias de la columna
        /// solo pueden subirlo (si el general sube por encima, manda el general).
        /// </summary>
        public BarCounts CountsFor(AppConfig cfg, int stirrup)
        {
            BarCounts g = General(cfg);
            if (stirrup >= Counts.Count || Counts[stirrup] == null) return g;
            BarCounts own = Counts[stirrup];
            return new BarCounts { Top = Math.Max(g.Top, own.Top), Bottom = Math.Max(g.Bottom, own.Bottom), Side = Math.Max(g.Side, own.Side) };
        }

        public bool CanBuild => Error == null && Section != null;

        public string Kind => Error != null ? "SIN ARMAR" : "Columna " + Section.KindName;

        public string Detail(AppConfig cfg) => Error ?? Section.Describe();

        public string Distribution(AppConfig cfg) =>
            string.IsNullOrWhiteSpace(DistributionOverride) ? cfg.Stirrups.Distribution : DistributionOverride;

        public double MaxSpacingMm(AppConfig cfg) => SpacingOverride > 0 ? SpacingOverride : cfg.Longitudinal.MaxSpacingMm;

        public string Partition(AppConfig cfg, string setName, string stirrup)
        {
            return PartitionName.Expand(cfg.PartitionTemplate, new PartitionName.Source
            {
                Mark = Mark, Id = Host.Id.ToString(), TypeName = TypeName, FamilyName = FamilyName,
                SetName = setName, Stirrup = stirrup
            });
        }

        public static HostAnalysis Analyze(Document doc, Element host, AppConfig cfg)
        {
            var a = new HostAnalysis { Host = host, Tag = "[" + host.Id + " " + host.Name + "] " };
            try
            {
                a.Mark = host.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                a.TypeName = ColumnSection.TypeNameOf(doc, host) ?? "";
                if (host is FamilyInstance fi) a.FamilyName = fi.Symbol?.Family?.Name ?? "";
                else a.FamilyName = host.Category?.Name ?? "";

                RebarHostData hd = RebarHostData.GetRebarHostData(host);
                if (hd == null || !hd.IsValidHost())
                {
                    a.Error = "no admite armadura. Revisa que el material sea hormigon y que sea un pilar estructural.";
                    return a;
                }

                a.Section = ColumnSection.Probe(doc, host, cfg);
                if (a.Section == null)
                    a.Error = "RECHAZADO, " + (ColumnSection.LastError ?? "no se pudo deducir la seccion (motivo desconocido)") +
                              ". No se ha creado ninguna barra.";
            }
            catch (Exception ex)
            {
                a.Error = "ERROR: " + ex.Message;
            }
            return a;
        }
    }
}
