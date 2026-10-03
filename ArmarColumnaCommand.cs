using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Arba.Comun;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace ColumnRebar
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ArmarColumnaCommand : IExternalCommand
    {
        /// <summary>Que hacer con la armadura que este add-in ya creo en una columna (ARBA - Origen = COLUMNAS).</summary>
        private enum ExistingAction { Delete, Keep }

        /// <summary>Que hacer con las barras anteriores al contrato (particion COL-… sin ARBA - Origen).</summary>
        private enum LegacyAction { MigrateOnly, MigrateAndRebuild, Keep }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            AppConfig cfg;
            try { cfg = AppConfig.Load(); }
            catch (Exception ex)
            {
                message = "No se pudo leer config.json (" + AppConfig.ConfigPath() + "): " + ex.Message;
                return Result.Failed;
            }

            IList<Element> hosts;
            try { hosts = GetHosts(uidoc); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

            if (hosts.Count == 0)
            {
                message = "No se selecciono ninguna columna estructural.";
                return Result.Cancelled;
            }

            var allTypes = RebarGenerator.AllBarTypes(doc);
            List<string> barTypes = allTypes.Select(b => b.Name).ToList();
            var diametersMm = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (RebarBarType bt in allTypes)
                diametersMm[bt.Name] = UnitUtils.ConvertFromInternalUnits(bt.BarNominalDiameter, UnitTypeId.Millimeters);
            if (barTypes.Count == 0)
            {
                message = "El proyecto no tiene ningun tipo de barra (RebarBarType). Carga una familia de armadura primero.";
                return Result.Failed;
            }
            var allHooks = RebarGenerator.AllHookTypes(doc);
            List<string> hookTypes = allHooks.Select(h => h.Name).ToList();
            // angulo de cada gancho (grados) para dibujarlo en el esquema de la seccion
            var hookAngles = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (RebarHookType h in allHooks)
            {
                double deg = 135;
                try { deg = Math.Round(h.HookAngle * 180 / Math.PI); } catch { }
                hookAngles[h.Name] = deg;
            }

            // --- 1. Analisis geometrico de cada elemento (solo lectura, sin transaccion) ---
            //        y armadura ya existente: propia del add-in (ARBA - Origen) o anterior al contrato (COL-… sin origen)
            var items = hosts.Select(h => HostAnalysis.Analyze(doc, h, cfg)).ToList();
            foreach (HostAnalysis item in items)
                if (item.CanBuild) item.ScanExisting(doc);

            // --- 2. Interfaz: el usuario revisa que se ha detectado y elige el armado ---
            var win = new RebarOptionsWindow(cfg.Clone(), barTypes, diametersMm, hookTypes, hookAngles, items);
            try { new WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle; } catch { }
            bool? ok = win.ShowDialog();
            if (ok != true || win.Result == null) return Result.Cancelled;
            cfg = win.Result;

            // --- 3. Borrar y rearmar / conservar; migrar las barras anteriores al contrato ---
            int withLegacy = items.Count(i => i.CanBuild && i.HasLegacyRebar);
            int withOwn = items.Count(i => i.CanBuild && i.HasOwnRebar);

            LegacyAction legacy = LegacyAction.Keep;
            if (withLegacy > 0 && !AskLegacy(withLegacy, out legacy)) return Result.Cancelled;

            ExistingAction existing = ExistingAction.Keep;
            if (withOwn > 0 && !AskExisting(withOwn, out existing)) return Result.Cancelled;

            // --- 4. Armado ---
            var log = new List<string>();
            var avisos = new List<string>();
            int total = 0, armed = 0, rejected = 0, migratedOnly = 0, deletedSets = 0;

            using (Transaction tx = new Transaction(doc, "Armar columnas"))
            {
                tx.Start();

                // Parametros compartidos del contrato (ARBA - Origen, ARBA - Codigo, Metrado - Elemento) vinculados a
                // las armaduras antes de la primera subtransaccion; los avisos (homonimos migrados, etc.) van al informe.
                ArbaSharedParams.Ensure(doc, new[] { ArbaContract.Origen, ArbaContract.Codigo, ArbaContract.Elemento }, avisos);
                doc.Regenerate();

                foreach (HostAnalysis item in items)
                {
                    string tag = item.Tag;
                    if (!item.CanBuild)
                    {
                        rejected++;
                        log.Add(tag + "SIN ARMAR -> " + item.Detail(cfg));
                        continue;
                    }

                    bool migrate = item.HasLegacyRebar && legacy != LegacyAction.Keep;
                    bool delete = (item.HasOwnRebar && existing == ExistingAction.Delete) ||
                                  (migrate && legacy == LegacyAction.MigrateAndRebuild);

                    // Cada elemento se arma dentro de una subtransaccion. Si cualquier barra
                    // queda fuera del hormigon (red de seguridad), se deshace TODO lo creado
                    // (y lo borrado) para ese elemento: o se arma entero y bien, o no se arma.
                    using (SubTransaction sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        BuildResult res = null;
                        string error = null;
                        int migratedSets = 0, deleted = 0, deletedBars = 0;
                        try
                        {
                            if (migrate)
                            {
                                ArbaMigrationResult mr = ArbaMigration.MigrateHost(doc, item.Host, ArbaContract.Columnas);
                                migratedSets = mr.Migradas;
                                foreach (string a in mr.Avisos) avisos.Add(tag + a);
                                if (legacy == LegacyAction.MigrateOnly)
                                {
                                    sub.Commit();
                                    migratedOnly++;
                                    log.Add(tag + "MIGRADO sin rearmar -> " + migratedSets + " conjunto(s) con particion " +
                                            item.Partition(cfg, "", "") + ", ARBA - Origen = " + ArbaContract.Columnas.Origin +
                                            " y Metrado - Elemento. No se ha creado ni borrado ninguna barra.");
                                    continue;
                                }
                                doc.Regenerate();
                            }

                            if (delete)
                            {
                                deleted = ArbaOrigin.Delete(doc, ArbaContract.Columnas, item.Host, out deletedBars);
                                if (deleted > 0) doc.Regenerate();
                            }

                            res = RebarGenerator.Build(doc, item, cfg);
                            if (res.Safe && res.Created.Count > 0)
                            {
                                doc.Regenerate();
                                RebarGenerator.VerifyCreated(doc, item.Section, res);
                            }
                        }
                        catch (Exception ex)
                        {
                            error = ex.Message;
                        }

                        bool keep = error == null && res != null && res.Safe;
                        string desc = item.Detail(cfg);
                        if (keep)
                        {
                            sub.Commit();
                            armed++;
                            total += res.Created.Count;
                            deletedSets += deleted;
                            string line = tag + desc + "  ->  " + res.Summary + " (" + res.Created.Count + " conjuntos, particion " +
                                          item.Partition(cfg, "", "") + ")";
                            if (migratedSets > 0)
                                line += "  MIGRADOS " + migratedSets + " conjunto(s) anteriores al contrato";
                            if (deleted > 0)
                                line += "  BORRADOS " + deleted + " conjunto(s) anteriores del add-in (" + deletedBars + " barras)";
                            else if (item.HasOwnRebar || (item.HasLegacyRebar && legacy == LegacyAction.Keep))
                                line += "  AVISO: se ha armado encima de la armadura anterior (queda duplicada)";
                            if (res.Failed.Count > 0)
                                line += "  INCOMPLETO, no se pudieron crear: " + string.Join(" | ", res.Failed);
                            if (res.Warnings.Count > 0)
                                line += "  AVISOS: " + string.Join(" | ", res.Warnings);
                            log.Add(line);
                        }
                        else
                        {
                            sub.RollBack();
                            rejected++;
                            string undone = delete || migrate
                                ? " Se ha deshecho todo lo creado, borrado y migrado para este elemento (su armadura anterior se conserva tal cual)."
                                : " Se ha deshecho todo lo creado para este elemento.";
                            if (error != null)
                                log.Add(tag + "SIN ARMAR -> ERROR: " + error + "." + undone);
                            else
                                log.Add(tag + "SIN ARMAR -> " + desc + ": barras fuera del hormigon (" + res.Rejected.Count + "): " +
                                        string.Join(" | ", res.Rejected) + "." + undone);
                        }
                    }
                }
                tx.Commit();
            }

            // --- 5. Informe ---
            var td = new TaskDialog("Armado de columnas")
            {
                MainInstruction = total + " conjuntos de armadura creados en " + armed + " de " + hosts.Count + " elemento(s).",
                MainContent = string.Join(Environment.NewLine, log),
                FooterText = "Contrato ARBA-comun " + ArbaContract.Version + ": particion " + AppConfig.DefaultPartitionTemplate +
                             ", ARBA - Origen = " + ArbaContract.Columnas.Origin + ", ARBA - Codigo = " +
                             RebarGenerator.CodeLongitudinal + " / " + RebarGenerator.CodeStirrup + " N / " + RebarGenerator.CodeTie + "."
            };
            if (deletedSets > 0)
                td.MainInstruction += Environment.NewLine + deletedSets + " conjunto(s) anteriores del add-in borrados antes de rearmar.";
            if (migratedOnly > 0)
                td.MainInstruction += Environment.NewLine + migratedOnly + " elemento(s) migrados al contrato sin rearmar.";
            if (rejected > 0)
            {
                td.MainInstruction += Environment.NewLine + "ATENCION: " + rejected +
                                      " elemento(s) SIN ARMAR (ver detalle). No se ha creado ninguna barra en ellos.";
                td.MainIcon = TaskDialogIcon.TaskDialogIconWarning;
            }
            if (avisos.Count > 0)
                td.ExpandedContent = "Avisos de los parametros compartidos y la migracion:" + Environment.NewLine +
                                     string.Join(Environment.NewLine, avisos.Select(a => "  - " + a));
            td.Show();
            return Result.Succeeded;
        }

        /// <summary>
        /// Pregunta una sola vez que hacer con la armadura que este add-in ya creo en las columnas elegidas.
        /// False si el usuario cancela.
        /// </summary>
        private static bool AskExisting(int count, out ExistingAction action)
        {
            action = ExistingAction.Keep;
            var td = new TaskDialog("Armar columnas")
            {
                TitleAutoPrefix = false,
                MainIcon = TaskDialogIcon.TaskDialogIconInformation,
                MainInstruction = count + " columna(s) ya tienen armadura creada por este add-in (ARBA - Origen = " +
                                  ArbaContract.Columnas.Origin + ").",
                MainContent = "Se reconoce por el parametro compartido ARBA - Origen de cada conjunto, aunque la particion se haya editado a mano. " +
                              "La armadura modelada a mano o la de otros add-ins no se toca.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.CommandLink1
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Borrar la armadura del add-in y rearmar",
                              "Elimina los conjuntos con origen " + ArbaContract.Columnas.Origin + " alojados en cada columna y la arma de nuevo. " +
                              "Si el nuevo armado falla, la columna se deshace entera y conserva su armadura anterior.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Conservar y armar encima",
                              "No borra nada: las barras nuevas se anaden a las existentes (quedan duplicadas).");
            TaskDialogResult r = td.Show();
            if (r == TaskDialogResult.CommandLink1) { action = ExistingAction.Delete; return true; }
            if (r == TaskDialogResult.CommandLink2) { action = ExistingAction.Keep; return true; }
            return false;
        }

        /// <summary>
        /// Pregunta una sola vez que hacer con las barras anteriores al contrato ARBA-comun (particion COL-marca
        /// sin ARBA - Origen) alojadas en las columnas elegidas. False si el usuario cancela.
        /// </summary>
        private static bool AskLegacy(int count, out LegacyAction action)
        {
            action = LegacyAction.Keep;
            var td = new TaskDialog("Armar columnas")
            {
                TitleAutoPrefix = false,
                MainIcon = TaskDialogIcon.TaskDialogIconInformation,
                MainInstruction = count + " columna(s) tienen barras de una version anterior de este add-in (particion COL-marca, sin ARBA - Origen).",
                MainContent = "Migrarlas reescribe su particion a la forma del contrato ARBA-comun " + ArbaContract.Version +
                              " (COLUMNAS - COL-marca) y rellena ARBA - Origen, ARBA - Codigo y Metrado - Elemento, sin crear ni borrar barras; " +
                              "a partir de entonces el add-in las reconoce como propias y el plugin de metrados las agrupa con las demas.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.CommandLink1
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Migrar sin rearmar",
                              "Solo actualiza particion y parametros de las barras existentes. Esas columnas no se arman de nuevo.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Migrar, borrar y rearmar",
                              "Migra las barras antiguas, las borra como propias del add-in y arma la columna de nuevo con lo elegido en la ventana.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Conservar y armar encima",
                              "No toca las barras antiguas: las nuevas se anaden a las existentes (quedan duplicadas).");
            TaskDialogResult r = td.Show();
            if (r == TaskDialogResult.CommandLink1) { action = LegacyAction.MigrateOnly; return true; }
            if (r == TaskDialogResult.CommandLink2) { action = LegacyAction.MigrateAndRebuild; return true; }
            if (r == TaskDialogResult.CommandLink3) { action = LegacyAction.Keep; return true; }
            return false;
        }

        private static IList<Element> GetHosts(UIDocument uidoc)
        {
            Document doc = uidoc.Document;
            var sel = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(IsCandidate)
                .ToList();
            if (sel.Count > 0) return sel;

            IList<Reference> refs = uidoc.Selection.PickObjects(
                ObjectType.Element, new HostFilter(),
                "Selecciona las columnas estructurales a armar y pulsa Finalizar");
            return refs.Select(r => doc.GetElement(r)).ToList();
        }

        private static bool IsCandidate(Element e)
        {
            if (e == null || e.Category == null) return false;
            return e.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns;
        }

        private class HostFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => IsCandidate(e);
            public bool AllowReference(Reference r, XYZ p) => false;
        }
    }
}
