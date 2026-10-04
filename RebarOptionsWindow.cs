using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Arba.Comun;

namespace ColumnRebar
{
    /// <summary>
    /// Ventana previa al armado: muestra que se ha detectado en cada columna seleccionada
    /// (forma de la seccion, numero de estribos, altura, o el motivo del rechazo) y deja
    /// elegir el armado: tipos de barra, separacion de longitudinales, prolongaciones,
    /// distribucion de estribos (global y por elemento), ganchos, grapas y recubrimiento,
    /// con un esquema de la seccion y del alzado que se redibuja con cada cambio.
    /// Los valores iniciales vienen de config.json y se pueden guardar como nuevos valores
    /// por defecto. Construida en codigo (sin XAML).
    /// </summary>
    public sealed class RebarOptionsWindow : Window
    {
        private readonly AppConfig _cfg;
        private readonly IList<string> _barTypes;
        private readonly IDictionary<string, double> _diametersMm;
        private readonly Dictionary<string, string> _typeByDisplay = new Dictionary<string, string>();
        private readonly IList<string> _hookTypes;
        /// <summary>Angulo (grados) de cada tipo de gancho, para el esquema.</summary>
        private readonly IDictionary<string, double> _hookAngles;
        private readonly IList<HostAnalysis> _items;

        /// <summary>Configuracion final si el usuario pulso "Armar"; null si cancelo.</summary>
        public AppConfig Result { get; private set; }

        private ComboBox _longLegDir;
        private ComboBox _longType, _longTypeInter, _stType, _stHook, _stOrient, _tieType, _tieHook, _tieOrient, _tieDir;
        private TextBox _longBottom, _longTop, _longLeg;
        private Grid _linesGrid;
        private TextBlock _linesCaption;
        /// <summary>Entradas del cuadro de barras por linea (fila o vertical) de la columna seleccionada.</summary>
        private readonly List<(bool horizontal, int index, TextBox count, ComboBox fill)> _lineRows = new List<(bool, int, TextBox, ComboBox)>();
        private static readonly string[] FillModes = { "auto", "left", "right", "center" };
        private static readonly string[] FillLabels = { "huecos mas grandes", "hacia la izquierda", "hacia la derecha", "simetrico" };
        private static readonly string[] FillLabelsV = { "huecos mas grandes", "hacia abajo", "hacia arriba", "simetrico" };
        private HostAnalysis _linesFor;
        private int _linesRowCount, _linesColCount;
        private bool _refreshingLines;
        /// <summary>Cuadro de estribos interiores de la columna seleccionada.</summary>
        private TextBlock _innerCaption;
        private StackPanel _innerList;
        private ScrollViewer _innerScroll;
        private Button _innerAdd, _innerAddOctagon;
        private readonly List<(InnerStirrupSpec spec, ComboBox shape, TextBox[] boxes)> _innerRows = new List<(InnerStirrupSpec, ComboBox, TextBox[])>();
        private static readonly string[] InnerShapes = { InnerStirrupSpec.ShapeRect, InnerStirrupSpec.ShapeOctagon };
        private static readonly string[] InnerShapeLabels = { "rectangular", "octogonal" };
        private HostAnalysis _innerFor;
        private int _innerCount = -1;
        private ColumnPlan _innerPlan;
        private bool _refreshingInner;
        private TextBox _stDist, _stBottomOff, _stTopOff, _cover, _partition;
        private CheckBox _stSym, _tieOn;
        private TextBlock _message, _partitionPreview, _previewCaption;
        private Button _buildButton;
        private TextBlock _partitionWarning;
        private SectionPreview _preview;
        private ElevationPreview _elevation;

        private readonly Dictionary<HostAnalysis, (System.Windows.Documents.Run kind, System.Windows.Documents.Run detail)> _itemRuns
            = new Dictionary<HostAnalysis, (System.Windows.Documents.Run, System.Windows.Documents.Run)>();
        private readonly Dictionary<HostAnalysis, Border> _itemRows = new Dictionary<HostAnalysis, Border>();
        /// <summary>Marca de agua de la caja de distribucion propia de cada columna (muestra la general).</summary>
        private readonly Dictionary<HostAnalysis, TextBlock> _distHints = new Dictionary<HostAnalysis, TextBlock>();
        private HostAnalysis _selected;
        private bool _building = true;
        private bool _strictTypes;

        private const string NoHook = "(sin gancho)";
        private const string SameAsCorner = "(igual que las de esquina)";
        private static readonly Thickness Pad = new Thickness(4, 2, 4, 2);
        private static readonly Brush SelectedBrush = RevitTheme.Selection;

        public RebarOptionsWindow(AppConfig cfg, IList<string> barTypes, IDictionary<string, double> diametersMm,
                                  IList<string> hookTypes, IDictionary<string, double> hookAngles, IList<HostAnalysis> items)
        {
            _hookAngles = hookAngles ?? new Dictionary<string, double>();
            _cfg = cfg;
            _cfg.Normalize();
            _diametersMm = diametersMm;
            _barTypes = barTypes.OrderBy(n => diametersMm.TryGetValue(n, out double mm) ? mm : 0)
                                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (string n in _barTypes) _typeByDisplay[TypeDisplay(n)] = n;
            _hookTypes = hookTypes;
            _items = items;

            Title = "Armar columnas";
            Width = 1180;
            Height = 820;
            MinWidth = 980;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            FontSize = 12;

            RevitTheme.Apply(this);
            Content = BuildRoot();
            _selected = _items.FirstOrDefault(i => i.CanBuild);
            if (_selected != null) SelectItem(_selected);
            _building = false;
            Refresh();
        }

        // ------------------------------------------------------------------
        // Construccion de la interfaz
        // ------------------------------------------------------------------
        private UIElement BuildRoot()
        {
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            UIElement elements = BuildElements();
            Grid.SetRow(elements, 0);
            root.Children.Add(elements);

            var body = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });

            var left = new StackPanel();
            left.Children.Add(BuildLongitudinal());
            left.Children.Add(BuildStirrups());
            left.Children.Add(BuildTies());
            left.Children.Add(BuildGeneral());
            var scroll = new ScrollViewer
            {
                Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(scroll, 0);
            body.Children.Add(scroll);

            UIElement previews = BuildPreviews();
            Grid.SetColumn(previews, 1);
            body.Children.Add(previews);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            UIElement buttons = BuildButtons();
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);
            return root;
        }

        private UIElement BuildElements()
        {
            int ok = _items.Count(i => i.CanBuild);
            var group = new GroupBox
            {
                Header = "Columnas seleccionadas: " + _items.Count + " (" + ok + " armables). Haz clic en una para verla en el esquema. " +
                         "A la derecha, la distribucion de estribos propia de cada columna (vacio = la general).",
                Padding = new Thickness(4)
            };
            var panel = new StackPanel();
            foreach (HostAnalysis item in _items)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                text.Inlines.Add(new System.Windows.Documents.Run(item.Tag) { FontWeight = FontWeights.Bold });
                var kindRun = new System.Windows.Documents.Run(item.Kind + ": ")
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = item.CanBuild ? RevitTheme.Ok : RevitTheme.Error
                };
                var detailRun = new System.Windows.Documents.Run(item.Detail(_cfg));
                text.Inlines.Add(kindRun);
                text.Inlines.Add(detailRun);
                _itemRuns[item] = (kindRun, detailRun);
                Grid.SetColumn(text, 0);
                row.Children.Add(text);

                if (item.CanBuild)
                {
                    var side = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    side.Children.Add(new TextBlock { Text = "Estribos de esta columna:", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
                    var dist = new TextBox
                    {
                        Width = 170, Text = item.DistributionOverride, Background = Brushes.Transparent,
                        ToolTip = "Distribucion de estribos propia de esta columna, como \"1@50, 8@100, R@200\" (por ejemplo, mas estribos en " +
                                  "la columna del primer piso). Vacio = se usa la distribucion general del apartado Estribos."
                    };
                    // marca de agua: la distribucion general, en gris, mientras la caja esta vacia
                    var hint = new TextBlock { Foreground = RevitTheme.Hint, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                    var box = new Grid { Width = 170 };
                    box.Children.Add(new Border { Background = RevitTheme.Input });
                    box.Children.Add(hint);
                    box.Children.Add(dist);
                    _distHints[item] = hint;
                    HostAnalysis captured = item;
                    dist.TextChanged += (s, e) => { captured.DistributionOverride = dist.Text; hint.Visibility = dist.Text.Length == 0 ? Visibility.Visible : Visibility.Hidden; Refresh(); };
                    side.Children.Add(box);
                    Grid.SetColumn(side, 1);
                    row.Children.Add(side);
                }

                var border = new Border { Child = row, Padding = new Thickness(4, 2, 4, 2), CornerRadius = new CornerRadius(3) };
                if (item.CanBuild)
                {
                    border.Cursor = Cursors.Hand;
                    HostAnalysis captured = item;
                    border.MouseLeftButtonDown += (s, e) => { SelectItem(captured); Refresh(); };
                }
                _itemRows[item] = border;
                panel.Children.Add(border);
            }
            group.Content = new ScrollViewer
            {
                Content = panel, MaxHeight = 150,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            return group;
        }

        private void SelectItem(HostAnalysis item)
        {
            _selected = item;
            foreach (var kv in _itemRows)
                kv.Value.Background = kv.Key == item ? SelectedBrush : Brushes.Transparent;
        }

        /// <summary>
        /// Las demas columnas armables con la misma seccion que la dada (mismas medidas y rectangulos).
        /// Comparten barras por linea y estribos interiores: al armar varias iguales a la vez se arman igual.
        /// </summary>
        private List<HostAnalysis> Twins(HostAnalysis item)
        {
            if (item == null || !item.CanBuild) return new List<HostAnalysis>();
            double tol = ColumnSection.Mm(_cfg.PrismCheckToleranceMm);
            return _items.Where(i => !ReferenceEquals(i, item) && i.CanBuild && i.Section.SameSectionAs(item.Section, tol)).ToList();
        }

        /// <summary>Lleva las barras por linea y los estribos interiores de la columna a sus iguales.</summary>
        private void Propagate(HostAnalysis item)
        {
            foreach (HostAnalysis twin in Twins(item)) twin.CopyLinesAndInnerFrom(item);
        }

        private static string TwinsNote(int n) => n == 0 ? "" : " y " + n + (n == 1 ? " columna igual" : " columnas iguales");

        private UIElement BuildLongitudinal()
        {
            var group = new GroupBox { Header = "Barras longitudinales", Padding = new Thickness(4) };
            var panel = new StackPanel();
            var grid = FormGrid();
            int r = 0;
            _longType = TypeCombo(_cfg.Longitudinal.BarTypeName);
            AddRow(grid, r++, "Barras de esquina:", _longType,
                   "Tipo de barra de las barras obligadas: las de las esquinas de cada estribo y las de los cruces entre estribos.");
            _longTypeInter = TypeCombo(_cfg.Longitudinal.IntermediateBarTypeName);
            _longTypeInter.Items.Insert(0, SameAsCorner);
            if (NameMatch.First(_barTypes, _cfg.Longitudinal.IntermediateBarTypeName) == null) _longTypeInter.SelectedIndex = 0;
            else _longTypeInter.SelectedIndex = _longTypeInter.SelectedIndex + 1;
            AddRow(grid, r++, "Barras intermedias:", _longTypeInter,
                   "Tipo de barra de las intermedias (las que van entre las obligadas a lo largo de cada lado). Puede ser otro diametro.");

            _longBottom = NumBox(_cfg.Longitudinal.BottomExtensionMm);
            AddRow(grid, r++, "Prolongacion inferior (mm):", _longBottom,
                   "Cuanto sobresalen las barras por debajo de la base de la columna (anclaje en la cimentacion o en el piso inferior). 0 = empiezan en la base.");
            _longTop = NumBox(_cfg.Longitudinal.TopExtensionMm);
            AddRow(grid, r++, "Prolongacion superior (mm):", _longTop,
                   "Cuanto sobresalen por encima de la coronacion (empalme con el piso siguiente). 0 = terminan en la coronacion.");
            var legRow = new StackPanel { Orientation = Orientation.Horizontal };
            _longLeg = NumBox(_cfg.Longitudinal.BottomLegMm);
            _longLegDir = new ComboBox { Margin = Pad, Width = 150 };
            _longLegDir.Items.Add("hacia fuera");
            _longLegDir.Items.Add("hacia el centro");
            _longLegDir.SelectedIndex = _cfg.Longitudinal.LegOutward ? 0 : 1;
            legRow.Children.Add(_longLeg);
            legRow.Children.Add(_longLegDir);
            Hook(_longLegDir);
            AddRow(grid, r++, "Patilla inferior (mm):", legRow,
                   "Patilla horizontal a 90 grados en el extremo inferior, hacia fuera de la seccion (lo normal en el arranque sobre la zapata) " +
                   "o hacia el centro. Necesita prolongacion inferior mayor que 0 (la patilla queda por debajo de la base, dentro de la " +
                   "cimentacion). 0 = sin patilla.");
            panel.Children.Add(grid);

            // cuadro por linea de la columna seleccionada
            _linesCaption = new TextBlock { Margin = new Thickness(4, 6, 4, 2), FontWeight = FontWeights.SemiBold };
            panel.Children.Add(_linesCaption);
            _linesGrid = new Grid { Margin = new Thickness(4, 0, 4, 2) };
            panel.Children.Add(_linesGrid);
            group.Content = panel;
            return group;
        }

        private static TextBox CountBox(int v) => new TextBox { Text = v.ToString(CultureInfo.InvariantCulture), Width = 40, Margin = Pad };

        private ComboBox FillCombo(string mode, bool withGeneral, bool vertical = false)
        {
            var cb = new ComboBox { Margin = Pad, Width = 150, HorizontalAlignment = HorizontalAlignment.Left };
            if (withGeneral) cb.Items.Add("(general)");
            foreach (string l in vertical ? FillLabelsV : FillLabels) cb.Items.Add(l);
            int idx = Array.IndexOf(FillModes, (mode ?? "").Trim().ToLowerInvariant());
            cb.SelectedIndex = withGeneral ? (idx < 0 ? 0 : idx + 1) : Math.Max(0, idx);
            if (!withGeneral) Hook(cb);
            return cb;
        }

        private static string FillOf(ComboBox cb, bool withGeneral)
        {
            int i = withGeneral ? cb.SelectedIndex - 1 : cb.SelectedIndex;
            return i < 0 || i >= FillModes.Length ? "" : FillModes[i];
        }

        /// <summary>
        /// Reconstruye (si cambia la columna o su numero de lineas) o actualiza el cuadro de
        /// barras por linea de la columna seleccionada: una entrada por fila (F1, F2... de
        /// arriba abajo) y por vertical (V1, V2... de izquierda a derecha), que la propia
        /// seccion decide. El general es el minimo; aqui solo se puede subir.
        /// </summary>
        private void RefreshLines(AppConfig scratch, ColumnPlan plan)
        {
            HostAnalysis item = _selected != null && _selected.CanBuild ? _selected : null;
            int rows = plan?.Rows.Count ?? 0, cols = plan?.Cols.Count ?? 0;
            bool rebuild = !ReferenceEquals(_linesFor, item) || _linesRowCount != rows || _linesColCount != cols;
            _refreshingLines = true;
            try
            {
                if (rebuild)
                {
                    _linesFor = item; _linesRowCount = rows; _linesColCount = cols;
                    _lineRows.Clear();
                    _linesGrid.Children.Clear();
                    _linesGrid.RowDefinitions.Clear();
                    _linesGrid.ColumnDefinitions.Clear();
                    _linesCaption.Text = item == null || plan == null ? "Barras por linea: selecciona una columna armable en la lista"
                        : "Barras por linea de " + item.Tag.Trim() + TwinsNote(Twins(item).Count) + ": " + rows + " filas y " + cols + " verticales (minimo: sus esquinas y cruces; aqui solo se sube)";
                    if (item == null || plan == null) return;
                    foreach (double w in new[] { 120, 50, 150, 64 })
                        _linesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
                    int gridRow = 0;
                    foreach (bool horizontal in new[] { true, false })
                    {
                        List<PlanLine> lines = horizontal ? plan.Rows : plan.Cols;
                        _linesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        foreach ((string h, int c) in new[] { (horizontal ? "Filas (arriba-abajo)" : "Verticales (izq-der)", 0), ("barras", 1), ("reparto", 2) })
                        {
                            var tb = new TextBlock { Text = h, Foreground = RevitTheme.Muted, Margin = new Thickness(4, horizontal ? 2 : 8, 4, 2) };
                            Grid.SetRow(tb, gridRow); Grid.SetColumn(tb, c);
                            _linesGrid.Children.Add(tb);
                        }
                        gridRow++;
                        for (int i = 0; i < lines.Count; i++)
                        {
                            int row = gridRow++, idx = i;
                            bool hz = horizontal;
                            PlanLine line = lines[i];
                            _linesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                            var lb = new TextBlock
                            {
                                Text = line.Name + "  (" + ColumnSection.ToMm(line.Coord) + " mm)", Margin = Pad, VerticalAlignment = VerticalAlignment.Center,
                                ToolTip = (horizontal ? "Fila a v = " : "Vertical a u = ") + ColumnSection.ToMm(line.Coord) + " mm (eje de las barras de esquina)"
                            };
                            Grid.SetRow(lb, row); Grid.SetColumn(lb, 0);
                            _linesGrid.Children.Add(lb);
                            TextBox count = CountBox(0);
                            ComboBox fill = FillCombo("auto", false, !horizontal);
                            fill.Width = 140;
                            fill.ToolTip = "Donde van las barras que anadas por encima del minimo cuando otro estribo parte esta linea: al hueco mas " +
                                           "grande, hacia un lado, o simetrico (por pares desde el centro). Si la linea no esta partida, se reparten por igual.";
                            Grid.SetRow(count, row); Grid.SetColumn(count, 1);
                            Grid.SetRow(fill, row); Grid.SetColumn(fill, 2);
                            _linesGrid.Children.Add(count);
                            _linesGrid.Children.Add(fill);
                            count.TextChanged += (sn, e) => { if (!_refreshingLines) { StoreLine(item, hz, idx, count, fill, ReadConfig(out _)); Propagate(item); Refresh(); } };
                            count.LostFocus += (sn, e) => Refresh();   // al salir se muestra el valor efectivo (nunca menor que el minimo)
                            fill.SelectionChanged += (sn, e) => { if (!_refreshingLines) { StoreLine(item, hz, idx, count, fill, ReadConfig(out _)); Propagate(item); Refresh(); } };
                            var reset = new Button { Content = "minimo", Padding = new Thickness(6, 1, 6, 1), Margin = Pad, ToolTip = "Volver al minimo (esquinas y cruces) y al reparto por huecos mas grandes en esta linea" };
                            reset.Click += (sn, e) => { item.SetOwn(hz, idx, null); Propagate(item); Refresh(); };
                            Grid.SetRow(reset, row); Grid.SetColumn(reset, 3);
                            _linesGrid.Children.Add(reset);
                            _lineRows.Add((horizontal, i, count, fill));
                        }
                    }
                }
                if (item == null || plan == null) return;
                foreach ((bool horizontal, int index, TextBox count, ComboBox fill) in _lineRows)
                {
                    List<PlanLine> lines = horizontal ? plan.Rows : plan.Cols;
                    if (index >= lines.Count) continue;
                    PlanLine line = lines[index];
                    LineSpec own = item.Own(horizontal, index);
                    int effective = Math.Max(line.Fixed, own?.Count ?? 0);
                    bool isOwn = own != null && (own.Count > line.Fixed || (!string.IsNullOrEmpty(own.Fill) && own.Fill != "auto"));
                    if (!count.IsFocused) count.Text = effective.ToString(CultureInfo.InvariantCulture);
                    int fi = Array.IndexOf(FillModes, own?.Fill ?? "auto");
                    fill.SelectedIndex = fi < 0 ? 0 : fi;
                    count.Background = isOwn ? RevitTheme.OwnValue : RevitTheme.Input;
                    if (line.Missing > 0)
                    {
                        count.Background = RevitTheme.Invalid;
                        count.ToolTip = "No caben " + line.Missing + " barra(s) mas en esta linea con 1.5 diametros libres";
                    }
                    else count.ToolTip = "Minimo " + line.Fixed + " (esquinas y cruces de estribos); escribe mas para anadir intermedias";
                }
            }
            finally { _refreshingLines = false; }
        }

        /// <summary>Guarda la eleccion propia de una linea (el minimo real, sus obligadas, lo aplica el plan).</summary>
        private static void StoreLine(HostAnalysis item, bool horizontal, int index, TextBox count, ComboBox fill, AppConfig cfg)
        {
            LineSpec spec = item.Own(horizontal, index)?.Clone() ?? new LineSpec();
            if (int.TryParse(count.Text.Trim(), out int n)) spec.Count = Math.Max(0, n);
            spec.Fill = FillOf(fill, false);
            item.SetOwn(horizontal, index, spec);
        }

        private UIElement BuildStirrups()
        {
            var group = new GroupBox { Header = "Estribos (uno cerrado por cada rectangulo de la seccion, mas los interiores)", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _stType = TypeCombo(_cfg.Stirrups.BarTypeName);
            AddRow(grid, r++, "Tipo de barra:", _stType, "Tipo de barra de los estribos.");
            _stHook = HookCombo(_cfg.Stirrups.HookTypeName);
            AddRow(grid, r++, "Gancho:", _stHook, "Tipo de gancho (RebarHookType) en los dos extremos del estribo, normalmente 135 grados. Sin gancho = estribo cerrado sin ganchos.");
            _stOrient = OrientCombo(_cfg.HookLeft);
            AddRow(grid, r++, "Giro del gancho:", _stOrient,
                   "Lado hacia el que giran los ganchos. Si con la orientacion elegida quedan fuera del hormigon, el plugin la invierte solo y lo avisa.");
            _stDist = new TextBox { Text = _cfg.Stirrups.Distribution, Margin = Pad };
            AddRow(grid, r++, "Distribucion:", _stDist,
                   "Como en los planos: \"1@50, 5@100, R@250\" = el primero a 50 mm de la base, cinco mas cada 100 mm y el resto cada 250 mm " +
                   "como maximo (repartidos por igual). Valores menores de 5 se leen en metros (\"1@.05\"). Cada columna puede tener la suya en la lista de arriba.");
            _stSym = new CheckBox { Content = "Repetir los grupos desde la coronacion (confinamiento arriba y abajo)", IsChecked = _cfg.Stirrups.Symmetric, Margin = Pad };
            AddRow(grid, r++, "", _stSym, "Con la casilla marcada, los grupos fijos (1@50, 5@100...) se colocan tambien desde arriba en espejo y el resto va en medio.");
            _stBottomOff = NumBox(_cfg.Stirrups.BottomOffsetMm);
            AddRow(grid, r++, "Desfase en la base (mm):", _stBottomOff, "La distribucion empieza a contar desde la base mas este desfase.");
            _stTopOff = NumBox(_cfg.Stirrups.TopOffsetMm);
            AddRow(grid, r++, "Desfase en coronacion (mm):", _stTopOff, "La distribucion termina en la coronacion menos este desfase (por ejemplo el canto de la losa si el elemento la incluye).");

            // estribos interiores de la columna seleccionada
            var panel = new StackPanel();
            panel.Children.Add(grid);
            _innerCaption = new TextBlock { Margin = new Thickness(4, 6, 4, 2), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_innerCaption);
            _innerList = new StackPanel { Margin = new Thickness(4, 0, 4, 2) };
            // cada fila es mas ancha que el panel: scroll horizontal, y vertical si hay muchos estribos
            _innerScroll = new ScrollViewer
            {
                Content = _innerList, MaxHeight = 170,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            // la rueda se queda aqui solo si este cuadro puede desplazarse en esa direccion; si no, sigue al panel de opciones
            _innerScroll.PreviewMouseWheel += (s, e) =>
            {
                var sv = (ScrollViewer)s;
                bool canScroll = e.Delta < 0 ? sv.VerticalOffset < sv.ScrollableHeight - 0.5 : sv.VerticalOffset > 0.5;
                if (canScroll) return;
                e.Handled = true;
                var up = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sv };
                (sv.Parent as UIElement)?.RaiseEvent(up);
            };
            panel.Children.Add(_innerScroll);
            _innerAdd = new Button
            {
                Content = "Anadir estribo interior", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Estribo cerrado que abraza un grupo de barras de esta columna, por ejemplo las tres del medio de las caras largas. " +
                          "Se ajusta por fuera a las barras elegidas, con la misma distribucion y el mismo gancho que los demas estribos. " +
                          "Vale tambien para las demas columnas seleccionadas con la misma seccion."
            };
            _innerAdd.Click += (s, e) =>
            {
                if (_innerFor == null || _innerPlan == null) return;
                _innerFor.InnerStirrups.Add(InnerStirrupSpec.Centered(_innerPlan.BarUs.Count, _innerPlan.BarVs.Count));
                Propagate(_innerFor);
                Refresh();
            };
            _innerAddOctagon = new Button
            {
                Content = "Anadir estribo octogonal", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 2, 4, 2), HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "Estribo octogonal (el \"1 octogonal 3/8\" de los planos): pasa por dos barras intermedias de cada cara de un estribo " +
                          "rectangular y corta las esquinas en diagonal. En horizontal se eligen las barras de las caras de arriba y abajo; en " +
                          "vertical, las de las caras izquierda y derecha. Se propone por las intermedias mas cercanas a las esquinas. " +
                          "Con una sola barra por cara (misma posicion de ida y vuelta) sale el rombo. Lleva la misma distribucion y el mismo " +
                          "gancho que los demas estribos y vale tambien para las columnas iguales."
            };
            _innerAddOctagon.Click += (s, e) =>
            {
                if (_innerFor == null || _innerPlan == null) return;
                _innerFor.InnerStirrups.Add(InnerStirrupSpec.Octagon(_innerPlan.BarUs.Count, _innerPlan.BarVs.Count));
                Propagate(_innerFor);
                Refresh();
            };
            var addRow = new StackPanel { Orientation = Orientation.Horizontal };
            addRow.Children.Add(_innerAdd);
            addRow.Children.Add(_innerAddOctagon);
            panel.Children.Add(addRow);
            group.Content = panel;
            return group;
        }

        /// <summary>
        /// Reconstruye (si cambia la columna o su numero de estribos interiores) o actualiza el
        /// cuadro de estribos interiores de la columna seleccionada: uno por fila, con las
        /// barras que abraza en horizontal (de izquierda a derecha) y en vertical (de arriba
        /// abajo), numeradas como en el esquema de la seccion.
        /// </summary>
        private void RefreshInner(ColumnPlan plan)
        {
            HostAnalysis item = _selected != null && _selected.CanBuild ? _selected : null;
            int n = item?.InnerStirrups.Count ?? 0;
            bool rebuild = !ReferenceEquals(_innerFor, item) || _innerCount != n;
            _innerPlan = plan;
            _refreshingInner = true;
            try
            {
                _innerAdd.IsEnabled = item != null && plan != null;
                _innerAddOctagon.IsEnabled = item != null && plan != null;
                _innerCaption.Text = item == null ? "Estribos interiores: selecciona una columna armable en la lista"
                    : "Estribos interiores de " + item.Tag.Trim() + TwinsNote(Twins(item).Count) + ": " + (n == 0 ? "ninguno" : n.ToString(CultureInfo.InvariantCulture)) +
                      (plan != null && n > 0 ? " (barras de 1 a " + plan.BarUs.Count + " en horizontal y de 1 a " + plan.BarVs.Count + " en vertical, numeradas en el esquema)" : "");
                if (rebuild)
                {
                    _innerFor = item; _innerCount = n;
                    _innerRows.Clear();
                    _innerList.Children.Clear();
                    if (item == null) return;
                    for (int k = 0; k < n; k++)
                    {
                        InnerStirrupSpec spec = item.InnerStirrups[k];
                        int number = item.Section.Rects.Count + k + 1;
                        var row = new StackPanel { Orientation = Orientation.Horizontal };
                        row.Children.Add(new TextBlock { Text = "E" + number, Width = 30, FontWeight = FontWeights.SemiBold, Foreground = SectionPreview.StirrupBrush(number - 1), Margin = Pad, VerticalAlignment = VerticalAlignment.Center });
                        var shape = new ComboBox { Margin = Pad, Width = 100, ToolTip = "Forma del estribo: rectangular (abraza las barras entre las posiciones) u octogonal (pasa por las barras elegidas de cada cara y corta las esquinas en diagonal)" };
                        foreach (string l in InnerShapeLabels) shape.Items.Add(l);
                        shape.SelectedIndex = spec.Octagonal ? 1 : 0;
                        shape.SelectionChanged += (sn, e) =>
                        {
                            if (_refreshingInner) return;
                            spec.Shape = InnerShapes[Math.Max(0, shape.SelectedIndex)];
                            Propagate(item);
                            Refresh();
                        };
                        row.Children.Add(shape);
                        var boxes = new TextBox[4];
                        string[] labels = { "horizontal: de la", "a la", "   vertical: de la", "a la" };
                        for (int b = 0; b < 4; b++)
                        {
                            row.Children.Add(new TextBlock { Text = labels[b], Margin = Pad, VerticalAlignment = VerticalAlignment.Center });
                            TextBox box = CountBox(0);
                            int field = b;
                            box.TextChanged += (sn, e) =>
                            {
                                if (_refreshingInner || !int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return;
                                if (field == 0) spec.UFrom = v; else if (field == 1) spec.UTo = v; else if (field == 2) spec.VFrom = v; else spec.VTo = v;
                                Propagate(item);
                                Refresh();
                            };
                            box.LostFocus += (sn, e) => Refresh();
                            boxes[b] = box;
                            row.Children.Add(box);
                        }
                        var remove = new Button { Content = "quitar", Padding = new Thickness(6, 1, 6, 1), Margin = Pad, ToolTip = "Quitar este estribo interior" };
                        remove.Click += (sn, e) => { item.InnerStirrups.Remove(spec); Propagate(item); Refresh(); };
                        row.Children.Add(remove);
                        _innerList.Children.Add(row);
                        _innerRows.Add((spec, shape, boxes));
                    }
                }
                for (int k = 0; k < _innerRows.Count; k++)
                {
                    (InnerStirrupSpec spec, ComboBox shape, TextBox[] boxes) = _innerRows[k];
                    int[] values = { spec.UFrom, spec.UTo, spec.VFrom, spec.VTo };
                    string err = plan != null && k < plan.InteriorErrors.Count ? plan.InteriorErrors[k] : null;
                    shape.SelectedIndex = spec.Octagonal ? 1 : 0;
                    for (int b = 0; b < 4; b++)
                    {
                        if (!boxes[b].IsFocused) boxes[b].Text = values[b].ToString(CultureInfo.InvariantCulture);
                        boxes[b].Background = err != null ? RevitTheme.Invalid : RevitTheme.Input;
                        boxes[b].ToolTip = err ?? (spec.Octagonal
                            ? (b < 2 ? "Octogonal: barras de las caras de arriba y abajo por las que pasa, en horizontal de izquierda a derecha (numeros encima del esquema); la misma en las dos casillas = una sola barra por cara"
                                     : "Octogonal: barras de las caras izquierda y derecha por las que pasa, en vertical de arriba abajo (numeros a la izquierda del esquema)")
                            : (b < 2 ? "Barras en horizontal, de izquierda a derecha (numeros encima del esquema)"
                                     : "Barras en vertical, de arriba abajo (numeros a la izquierda del esquema)"));
                    }
                }
            }
            finally { _refreshingInner = false; }
        }

        private UIElement BuildTies()
        {
            var group = new GroupBox { Header = "Grapas entre barras intermedias enfrentadas", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _tieOn = new CheckBox { Content = "Colocar grapas en cada cota de estribo", IsChecked = _cfg.Crossties.Enabled, Margin = Pad };
            AddRow(grid, r++, "", _tieOn,
                   "Una grapa por cada par de barras intermedias enfrentadas de un mismo estribo (salvo donde ya pasa el lado de otro estribo).");
            _tieType = TypeCombo(_cfg.Crossties.BarTypeName);
            AddRow(grid, r++, "Tipo de barra:", _tieType, "Tipo de barra de las grapas.");
            _tieHook = HookCombo(_cfg.Crossties.HookTypeName);
            AddRow(grid, r++, "Gancho:", _tieHook, "Tipo de gancho en los dos extremos de la grapa.");
            _tieOrient = OrientCombo(_cfg.TieHookLeft);
            AddRow(grid, r++, "Giro del gancho:", _tieOrient, "Lado hacia el que giran los ganchos de la grapa (se invierte solo si quedan fuera).");
            _tieDir = new ComboBox { Margin = Pad };
            _tieDir.Items.Add("En las dos direcciones");
            _tieDir.Items.Add("Solo paralelas al lado largo (u)");
            _tieDir.Items.Add("Solo paralelas al lado corto (v)");
            _tieDir.SelectedIndex = _cfg.TiesU && _cfg.TiesV ? 0 : _cfg.TiesU ? 1 : 2;
            AddRow(grid, r++, "Direccion:", _tieDir, "u es el eje del borde mas largo de la seccion; v el perpendicular.");
            group.Content = grid;
            return group;
        }

        private UIElement BuildGeneral()
        {
            var group = new GroupBox { Header = "Recubrimiento y particion", Padding = new Thickness(4) };
            var grid = FormGrid();
            int r = 0;
            _cover = NumBox(_cfg.CoverMm);
            AddRow(grid, r++, "Recubrimiento al estribo (mm):", _cover, "Distancia de cada cara de la columna al borde exterior del estribo.");
            _partition = new TextBox { Text = _cfg.PartitionTemplate, Margin = Pad };
            AddRow(grid, r++, "Particion:", _partition,
                   "Plantilla del parametro Particion de cada barra. Por el contrato ARBA-comun " + ArbaContract.Version +
                   " empieza por \"{categoria} - {prefijo}-\" (por defecto \"" + AppConfig.DefaultPartitionTemplate +
                   "\" da COLUMNAS - COL-C3). Comodines: " + PartitionName.Help);
            _partitionPreview = new TextBlock { Foreground = RevitTheme.Muted, Margin = Pad, TextWrapping = TextWrapping.Wrap };
            AddRow(grid, r++, "", _partitionPreview, null);
            _partitionWarning = new TextBlock
            {
                Foreground = RevitTheme.Error, Margin = Pad, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
                Text = "La plantilla no sigue el contrato ARBA-comun: tiene que empezar por \"{categoria} - {prefijo}-\" para que " +
                       "el plugin de metrados agrupe estas barras con las de los demas add-ins (COLUMNAS - COL-C3)."
            };
            AddRow(grid, r++, "", _partitionWarning, null);
            group.Content = grid;
            return group;
        }

        private UIElement BuildPreviews()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.35, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var secGroup = new GroupBox { Header = "Seccion (rueda: zoom, arrastrar: mover, doble clic: encajar)", Padding = new Thickness(4) };
            var secPanel = new DockPanel();
            _previewCaption = new TextBlock { Foreground = RevitTheme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(_previewCaption, Dock.Top);
            secPanel.Children.Add(_previewCaption);
            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            LegendItem(legend, SectionPreview.RequiredBrush, "barra de esquina o cruce");
            LegendItem(legend, SectionPreview.IntermediateBrush, "barra intermedia");
            LegendItem(legend, SectionPreview.StirrupBrush(0), "estribo 1");
            LegendItem(legend, SectionPreview.StirrupBrush(1), "estribo 2... (interior u octogonal)");
            LegendItem(legend, SectionPreview.TieBrush, "grapa (con sus ganchos)");
            DockPanel.SetDock(legend, Dock.Bottom);
            secPanel.Children.Add(legend);
            _preview = new SectionPreview { MinHeight = 200 };
            secPanel.Children.Add(new Border { BorderBrush = RevitTheme.Border, BorderThickness = new Thickness(1), Child = _preview });
            secGroup.Content = secPanel;
            Grid.SetRow(secGroup, 0);
            grid.Children.Add(secGroup);

            var elvGroup = new GroupBox { Header = "Alzado: distribucion de estribos", Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
            _elevation = new ElevationPreview { MinHeight = 150 };
            elvGroup.Content = new Border { BorderBrush = RevitTheme.Border, BorderThickness = new Thickness(1), Child = _elevation };
            Grid.SetRow(elvGroup, 1);
            grid.Children.Add(elvGroup);
            return grid;
        }

        private static void LegendItem(Panel panel, Brush brush, string text)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, Fill = brush, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(sp);
        }

        private UIElement BuildButtons()
        {
            var panel = new DockPanel();
            var version = new TextBlock
            {
                Text = "Contrato ARBA-comun " + ArbaContract.Version, Foreground = RevitTheme.Hint,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                ToolTip = "Version del contrato ARBA-comun (parametros compartidos, particion y cinta) con la que se compilo este add-in."
            };
            DockPanel.SetDock(version, Dock.Left);
            panel.Children.Add(version);
            _message = new TextBlock { Foreground = RevitTheme.Error, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(buttons, Dock.Right);

            var save = new Button { Content = "Guardar como valores por defecto", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 4, 0) };
            save.ToolTip = "Guarda lo elegido en config.json (" + AppConfig.ConfigPath() + ") para las proximas veces.";
            save.Click += (s, e) =>
            {
                AppConfig c = ReadConfig(out string err);
                if (err != null) { _message.Text = err; return; }
                try { c.Save(); _message.Foreground = RevitTheme.Ok; _message.Text = "Guardado en " + AppConfig.ConfigPath(); }
                catch (Exception ex) { _message.Foreground = RevitTheme.Error; _message.Text = "No se pudo guardar: " + ex.Message; }
            };
            buttons.Children.Add(save);

            _buildButton = new Button { Content = "Armar", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(4, 0, 4, 0), FontWeight = FontWeights.SemiBold, IsDefault = true };
            _buildButton.Click += (s, e) => OnBuild();
            buttons.Children.Add(_buildButton);

            var cancel = new Button { Content = "Cancelar", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(cancel);

            panel.Children.Add(buttons);
            panel.Children.Add(_message);
            return panel;
        }

        // ------------------------------------------------------------------
        // Controles auxiliares
        // ------------------------------------------------------------------
        private static Grid FormGrid()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private void AddRow(Grid grid, int row, string label, FrameworkElement control, string tip)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var lb = new TextBlock { Text = label, Margin = Pad, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lb, row); Grid.SetColumn(lb, 0);
            grid.Children.Add(lb);
            if (tip != null) { control.ToolTip = tip; lb.ToolTip = tip; }
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            grid.Children.Add(control);
            Hook(control);
        }

        private void Hook(FrameworkElement c)
        {
            if (c is TextBox tb) tb.TextChanged += (s, e) => Refresh();
            else if (c is ComboBox cb) cb.SelectionChanged += (s, e) => Refresh();
            else if (c is CheckBox ck) { ck.Checked += (s, e) => Refresh(); ck.Unchecked += (s, e) => Refresh(); }
        }

        private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        private static TextBox NumBox(double v) => new TextBox { Text = Num(v), Width = 80, HorizontalAlignment = HorizontalAlignment.Left, Margin = Pad };

        private string TypeDisplay(string name) =>
            _diametersMm.TryGetValue(name, out double mm) ? name + " (" + Num(mm) + " mm)" : name;

        private ComboBox TypeCombo(string current)
        {
            var cb = new ComboBox { Margin = Pad };
            foreach (string n in _barTypes) cb.Items.Add(TypeDisplay(n));
            string match = NameMatch.First(_barTypes, current);
            cb.SelectedIndex = match == null ? -1 : _barTypes.IndexOf(match);
            return cb;
        }

        private string TypeOf(ComboBox cb) =>
            cb.SelectedItem is string d && _typeByDisplay.TryGetValue(d, out string n) ? n : "";

        private ComboBox HookCombo(string current)
        {
            var cb = new ComboBox { Margin = Pad };
            cb.Items.Add(NoHook);
            foreach (string n in _hookTypes) cb.Items.Add(n);
            string match = NameMatch.First(_hookTypes, current);
            cb.SelectedIndex = match == null ? 0 : _hookTypes.IndexOf(match) + 1;
            return cb;
        }

        private static string HookOf(ComboBox cb) => cb.SelectedIndex <= 0 ? "" : (string)cb.SelectedItem;

        /// <summary>Angulo en grados del tipo de gancho (0 = sin gancho).</summary>
        private double HookAngle(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            string match = NameMatch.First(_hookTypes, name);
            if (match != null && _hookAngles.TryGetValue(match, out double deg) && deg > 0) return deg;
            return 135;
        }

        private static ComboBox OrientCombo(bool left)
        {
            var cb = new ComboBox { Margin = Pad, Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            cb.Items.Add("Izquierda");
            cb.Items.Add("Derecha");
            cb.SelectedIndex = left ? 0 : 1;
            return cb;
        }

        private double DiameterFt(string typeName)
        {
            string match = NameMatch.First(_barTypes, typeName);
            return match != null && _diametersMm.TryGetValue(match, out double mm) ? ColumnSection.Mm(mm) : 0;
        }

        // ------------------------------------------------------------------
        // Lectura de la configuracion desde los controles
        // ------------------------------------------------------------------
        private AppConfig ReadConfig(out string error)
        {
            var errors = new List<string>();
            AppConfig c = _cfg.Clone();

            c.Longitudinal.BarTypeName = TypeOf(_longType);
            c.Longitudinal.IntermediateBarTypeName = _longTypeInter.SelectedIndex <= 0 ? "" : TypeOf(_longTypeInter);
            c.Longitudinal.BottomExtensionMm = ReadNum(_longBottom, "prolongacion inferior", 0, errors);
            c.Longitudinal.TopExtensionMm = ReadNum(_longTop, "prolongacion superior", 0, errors);
            c.Longitudinal.BottomLegMm = ReadNum(_longLeg, "patilla inferior", 0, errors);
            c.Longitudinal.LegDirection = _longLegDir.SelectedIndex == 1 ? "in" : "out";

            c.Stirrups.BarTypeName = TypeOf(_stType);
            c.Stirrups.HookTypeName = HookOf(_stHook);
            c.Stirrups.HookOrientation = _stOrient.SelectedIndex == 1 ? "right" : "left";
            c.Stirrups.Distribution = _stDist.Text.Trim();
            if (StirrupLayout.Parse(c.Stirrups.Distribution, out string derr) == null) errors.Add("distribucion de estribos: " + derr);
            c.Stirrups.Symmetric = _stSym.IsChecked == true;
            c.Stirrups.BottomOffsetMm = ReadNum(_stBottomOff, "desfase en la base", 0, errors);
            c.Stirrups.TopOffsetMm = ReadNum(_stTopOff, "desfase en coronacion", 0, errors);

            c.Crossties.Enabled = _tieOn.IsChecked == true;
            c.Crossties.BarTypeName = TypeOf(_tieType);
            c.Crossties.HookTypeName = HookOf(_tieHook);
            c.Crossties.HookOrientation = _tieOrient.SelectedIndex == 1 ? "right" : "left";
            c.Crossties.Directions = _tieDir.SelectedIndex == 1 ? "u" : _tieDir.SelectedIndex == 2 ? "v" : "both";

            c.CoverMm = ReadNum(_cover, "recubrimiento", 0, errors);
            c.PartitionTemplate = _partition.Text.Trim();
            c.Normalize();

            error = errors.Count == 0 ? null : string.Join(" | ", errors);
            return c;
        }

        private static int ReadInt(TextBox tb, string label, int min, List<string> errors)
        {
            if (!int.TryParse(tb.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v < min)
            {
                errors.Add(label + ": entero no valido (minimo " + min + ")");
                tb.BorderBrush = RevitTheme.Error;
                return min;
            }
            tb.ClearValue(Control.BorderBrushProperty);
            return v;
        }

        private static double ReadNum(TextBox tb, string label, double min, List<string> errors)
        {
            if (!StirrupLayout.TryNumber(tb.Text, out double v) || v < min)
            {
                errors.Add(label + ": numero no valido" + (min > 0 ? " (minimo " + Num(min) + ")" : ""));
                tb.BorderBrush = RevitTheme.Error;
                return min;
            }
            tb.ClearValue(Control.BorderBrushProperty);
            return v;
        }

        /// <summary>Tipos de barra que faltan (obligatorios segun lo activado).</summary>
        private List<string> MissingTypes(AppConfig c)
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(c.Longitudinal.BarTypeName)) missing.Add("longitudinales");
            if (string.IsNullOrEmpty(c.Stirrups.BarTypeName)) missing.Add("estribos");
            if (c.Crossties.Enabled && string.IsNullOrEmpty(c.Crossties.BarTypeName)) missing.Add("grapas");
            return missing;
        }

        private void MarkTypes(AppConfig c)
        {
            foreach ((ComboBox cb, bool required) in new[]
            {
                (_longType, true), (_stType, true), (_tieType, c.Crossties.Enabled)
            })
            {
                if (_strictTypes && required && cb.SelectedIndex < 0) { cb.BorderBrush = RevitTheme.Error; cb.BorderThickness = new Thickness(2); }
                else { cb.ClearValue(Control.BorderBrushProperty); cb.ClearValue(Control.BorderThicknessProperty); }
            }
        }

        // ------------------------------------------------------------------
        // Actualizacion
        // ------------------------------------------------------------------
        private void Refresh()
        {
            if (_building) return;
            AppConfig scratch = ReadConfig(out string error);
            MarkTypes(scratch);
            bool tiesEnabled = scratch.Crossties.Enabled;
            foreach (FrameworkElement fe in new FrameworkElement[] { _tieType, _tieHook, _tieOrient, _tieDir }) fe.IsEnabled = tiesEnabled;

            double db = DiameterFt(scratch.Longitudinal.BarTypeName);
            double dbi = string.IsNullOrEmpty(scratch.Longitudinal.IntermediateBarTypeName) ? db : DiameterFt(scratch.Longitudinal.IntermediateBarTypeName);
            double ds = DiameterFt(scratch.Stirrups.BarTypeName);
            double dt = tiesEnabled ? DiameterFt(scratch.Crossties.BarTypeName) : 0;
            // sin tipo elegido, se dibuja con un diametro orientativo para poder ver el esquema
            double dbDraw = db > 0 ? db : ColumnSection.Mm(16), dsDraw = ds > 0 ? ds : ColumnSection.Mm(8), dtDraw = dt > 0 ? dt : dsDraw;
            double dbiDraw = dbi > 0 ? dbi : dbDraw;

            // estado de cada columna con esta configuracion
            int ok = 0;
            foreach (HostAnalysis item in _items)
            {
                bool good = ItemStatus(item, scratch, dbDraw, dbiDraw, dsDraw, dtDraw, out string text, out _, out _);
                if (good) ok++;
                if (_itemRuns.TryGetValue(item, out var runs))
                {
                    runs.kind.Foreground = good ? RevitTheme.Ok : RevitTheme.Error;
                    runs.detail.Text = text;
                }
            }
            foreach (var kv in _distHints)
            {
                kv.Value.Text = "general: " + scratch.Stirrups.Distribution;
                kv.Value.Visibility = string.IsNullOrEmpty(kv.Key.DistributionOverride) ? Visibility.Visible : Visibility.Hidden;
            }
            _buildButton.Content = "Armar " + ok + " elemento(s)";
            _buildButton.IsEnabled = ok > 0 && error == null;

            // esquema del elemento seleccionado
            if (_selected != null && _selected.CanBuild)
            {
                ItemStatus(_selected, scratch, dbDraw, dbiDraw, dsDraw, dtDraw, out string text, out ColumnPlan plan, out List<StirrupRun> runs);
                _previewCaption.Text = _selected.Tag + _selected.Section.Describe() +
                                       (db <= 0 || ds <= 0 ? "  (sin tipo de barra elegido: diametros orientativos)" : "");
                double hookDeg = HookAngle(scratch.Stirrups.HookTypeName);
                double tieHookDeg = tiesEnabled ? HookAngle(scratch.Crossties.HookTypeName) : 0;
                RefreshLines(scratch, plan != null && plan.Error == null ? plan : null);
                RefreshInner(plan != null && plan.Error == null ? plan : null);
                if (plan != null) _preview.Show(_selected.Section, plan, hookDeg, tieHookDeg); else _preview.Clear(text);
                if (runs != null) _elevation.Show(_selected.Section, plan, runs, scratch); else _elevation.Clear(text);
                _partitionPreview.Text = "Ejemplo: " + _selected.Partition(scratch, "estribo", "1") +
                                         (_selected.HasOwnRebar ? "   (ya tiene armadura de este add-in: al armar se pregunta si borrarla)" : "") +
                                         (_selected.HasLegacyRebar ? "   (tiene barras COL-… anteriores al contrato: al armar se ofrece migrarlas)" : "");
            }
            else
            {
                RefreshLines(scratch, null);
                RefreshInner(null);
                _previewCaption.Text = "";
                _preview.Clear("Sin elemento armable");
                _elevation.Clear("");
                _partitionPreview.Text = "";
            }
            _partitionWarning.Visibility = ArbaPartition.TemplateFollowsContract(scratch.PartitionTemplate) ? Visibility.Collapsed : Visibility.Visible;

            if (error != null) { _message.Foreground = RevitTheme.Error; _message.Text = error; }
            else if (_message.Foreground == RevitTheme.Error) _message.Text = "";
        }

        /// <summary>Estado de una columna con la configuracion dada: true si se puede armar, y el texto para su fila.</summary>
        private static bool ItemStatus(HostAnalysis item, AppConfig cfg, double db, double dbi, double ds, double dt,
                                       out string text, out ColumnPlan plan, out List<StirrupRun> runs)
        {
            plan = null; runs = null;
            if (!item.CanBuild) { text = item.Error; return false; }
            try
            {
                plan = RebarGenerator.PlanFor(item, cfg, db, dbi, ds, dt);
                if (plan.Error != null) { text = item.Section.Describe() + " -> SIN ARMAR: " + plan.Error; return false; }
                runs = RebarGenerator.RunsFor(item, cfg, out string warn);
                if (plan.InteriorError != null) { text = item.Section.Describe() + " -> SIN ARMAR: " + plan.InteriorError; return false; }
                int n = runs.Sum(r => r.Count);
                text = item.Section.Describe() + "; " + plan.Describe() + "; " + n + " estribos por rectangulo" +
                       (warn != null ? " (" + warn + ")" : "");
                return n > 0;
            }
            catch (Exception ex)
            {
                text = item.Section.Describe() + " -> SIN ARMAR: " + ex.Message;
                return false;
            }
        }

        private void OnBuild()
        {
            AppConfig c = ReadConfig(out string error);
            if (error != null) { _message.Foreground = RevitTheme.Error; _message.Text = error; return; }
            List<string> missing = MissingTypes(c);
            if (missing.Count > 0)
            {
                _strictTypes = true;
                MarkTypes(c);
                _message.Foreground = RevitTheme.Error;
                _message.Text = "Elige el tipo de barra de: " + string.Join(", ", missing) + ".";
                return;
            }
            Result = c;
            DialogResult = true;
            Close();
        }
    }
}
