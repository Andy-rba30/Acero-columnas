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
        /// <summary>Barras y reparto por fila (de arriba abajo) y por vertical (de izquierda a derecha) de esta columna; null = general.</summary>
        public List<LineSpec> RowOverrides = new List<LineSpec>();
        public List<LineSpec> ColOverrides = new List<LineSpec>();

        public List<LineSpec> Overrides(bool horizontal) => horizontal ? RowOverrides : ColOverrides;

        /// <summary>Eleccion propia de una linea, o null si usa el general.</summary>
        public LineSpec Own(bool horizontal, int index)
        {
            List<LineSpec> list = Overrides(horizontal);
            return index < list.Count ? list[index] : null;
        }

        public void SetOwn(bool horizontal, int index, LineSpec spec)
        {
            List<LineSpec> list = Overrides(horizontal);
            while (list.Count <= index) list.Add(null);
            list[index] = spec;
        }

        public bool CanBuild => Error == null && Section != null;

        public string Kind => Error != null ? "SIN ARMAR" : "Columna " + Section.KindName;

        public string Detail(AppConfig cfg) => Error ?? Section.Describe();

        public string Distribution(AppConfig cfg) =>
            string.IsNullOrWhiteSpace(DistributionOverride) ? cfg.Stirrups.Distribution : DistributionOverride;

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
