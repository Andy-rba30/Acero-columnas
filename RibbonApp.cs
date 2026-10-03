using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Arba.Comun;
using Autodesk.Revit.UI;

namespace ColumnRebar
{
    /// <summary>
    /// Entrada de la aplicacion de cinta para ColumnRebar en Revit.
    /// Anade el boton "Columnas" al desplegable "Acero" del panel "Acero" en la pestana "ARBA",
    /// compartidos con el resto de add-ins ARBA a traves de <see cref="ArbaRibbon"/> (ARBA-comun).
    /// </summary>
    public class RibbonApp : IExternalApplication
    {
        /// <summary>Nombre interno del boton segun el contrato ARBA-comun (no cambia entre versiones).</summary>
        public const string ButtonName = "ARBA_Acero_Columnas";

        public Result OnStartup(UIControlledApplication app)
        {
            try
            {
                ArbaRibbon.Ensure(app);

                string assembly = Assembly.GetExecutingAssembly().Location;
                var data = new PushButtonData(ButtonName, "Columnas", assembly, typeof(ArmarColumnaCommand).FullName)
                {
                    ToolTip = "Genera el armado de columnas de seccion rectangular, L, T, U o cruz: longitudinales, estribos y grapas",
                    LongDescription = "Selecciona una o varias columnas estructurales y pulsa el boton. Se abre la ventana " +
                                      "para elegir los tipos de barra, la separacion de longitudinales, la distribucion de " +
                                      "estribos y los ganchos, con un esquema de la seccion y del alzado. Si no hay nada " +
                                      "seleccionado, el comando pide que elijas las columnas. Las barras llevan la particion " +
                                      "COLUMNAS - COL-{marca} y los parametros ARBA - Origen / Codigo del contrato ARBA-comun " +
                                      ArbaContract.Version + ".",
                    LargeImage = IconColumnas(32),
                    Image = IconColumnas(16)
                };

                ArbaRibbon.AddAcero(app, data);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("ARBA", "No se pudo anadir el boton Columnas a la cinta: " + ex.Message +
                                "\nEl comando sigue disponible en Complementos > Herramientas externas.");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        /// <summary>
        /// Icono propio del boton Columnas: seccion rectangular de hormigon con un estribo cerrado
        /// (ganchos a 135 grados en la esquina superior izquierda), cuatro barras de esquina y dos
        /// intermedias en los lados largos. Dibujado en codigo, como los iconos de Vigas y Zapatas.
        /// </summary>
        public static BitmapSource IconColumnas(int size)
        {
            double s = size / 32.0;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var concrete = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
                var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), 1.2 * s);
                var stirrup = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0x7A)), 1.7 * s)
                {
                    LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round
                };
                var corner = new SolidColorBrush(Color.FromRgb(0x8B, 0x2E, 0x2E));
                var inter = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x2E));

                // hormigon: seccion rectangular (mas alta que ancha, como una columna en planta)
                dc.DrawRectangle(concrete, edge, new System.Windows.Rect(4 * s, 2 * s, 24 * s, 28 * s));

                // estribo cerrado a "recubrimiento" de las caras
                double x1 = 8 * s, y1 = 6 * s, x2 = 24 * s, y2 = 26 * s;
                dc.DrawRectangle(null, stirrup, new System.Windows.Rect(x1, y1, x2 - x1, y2 - y1));

                // ganchos a 135 grados hacia el interior, en la esquina superior izquierda
                dc.DrawLine(stirrup, new Point(x1, y1), new Point(x1 + 3.2 * s, y1 + 3.2 * s));
                dc.DrawLine(stirrup, new Point(x1 + 0.9 * s, y1 - 0.9 * s), new Point(x1 + 4.2 * s, y1 + 2.4 * s));

                // barras de esquina (obligadas) e intermedias en los lados largos
                double rc = 2.0 * s, ri = 1.7 * s;
                foreach (Point p in new[] { new Point(x1, y1), new Point(x2, y1), new Point(x2, y2), new Point(x1, y2) })
                    dc.DrawEllipse(corner, null, p, rc, rc);
                double ym = (y1 + y2) / 2;
                dc.DrawEllipse(inter, null, new Point(x1, ym), ri, ri);
                dc.DrawEllipse(inter, null, new Point(x2, ym), ri, ri);
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
