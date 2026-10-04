using System;
using System.Collections.Generic;
using Arba.Comun;
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

        /// <summary>Estribos interiores de esta columna (ademas del de cada rectangulo), con las barras que abraza cada uno.</summary>
        public List<InnerStirrupSpec> InnerStirrups = new List<InnerStirrupSpec>();

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

        /// <summary>
        /// Particion del contrato ARBA-comun para una barra de esta columna: la categoria la deduce
        /// del anfitrion (COLUMNAS), el prefijo es el del add-in (COL) y la marca (o el Id) la del
        /// anfitrion; <paramref name="stirrup"/> (numero de estribo) va en {codigo} / {estribo}.
        /// Con la plantilla por defecto da "COLUMNAS - COL-C3".
        /// </summary>
        public string Partition(AppConfig cfg, string setName, string stirrup)
        {
            return ArbaPartition.BuildFor(Host, ArbaContract.Columnas, cfg.PartitionTemplate, new PartitionName.Source
            {
                Mark = Mark, TypeName = TypeName, FamilyName = FamilyName, SetName = setName, Code = stirrup
            });
        }

        /// <summary>Hay conjuntos de este add-in (ARBA - Origen = COLUMNAS) alojados en la columna.</summary>
        public bool HasOwnRebar;
        /// <summary>Hay conjuntos anteriores al contrato (particion COL-… sin ARBA - Origen) alojados en la columna.</summary>
        public bool HasLegacyRebar;

        /// <summary>Lee si la columna ya tiene armadura de este add-in o anterior al contrato (solo lectura).</summary>
        public void ScanExisting(Document doc)
        {
            HasOwnRebar = false;
            HasLegacyRebar = false;
            if (Host == null) return;
            try { HasOwnRebar = ArbaOrigin.Find(doc, ArbaContract.Columnas, Host).Count > 0; } catch (Exception) { }
            try { HasLegacyRebar = ArbaMigration.HasLegacy(doc, Host, ArbaContract.Columnas); } catch (Exception) { }
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
