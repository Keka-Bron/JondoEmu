using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Jondo.Unity.Launcher.UI;
using MSFT = Microsoft.Data.Sqlite;

namespace Jondo.Unity.Admin
{
    /// <summary>
    /// El panel de Jondo: explorar las bases y mandar sobre el servidor en marcha.
    ///
    /// Dos pestañas y nada más:
    ///
    ///   Base de datos    world.db y auth.db por dentro: tablas, filas, edición directa con la
    ///                    clave primaria de cada tabla, borrado y una consola de SQL para lo demás.
    ///   Servidor en vivo el puente con el canal de mando: quién está dentro, expulsar, ejecutar
    ///                    comandos sobre un personaje conectado y difundir líneas de chat.
    ///
    /// Pinta con la paleta del contrato —la misma del lanzador y de la ventana del servidor—, así
    /// que se reconoce como de la familia sin copiar un solo color.
    ///
    /// Lo que se edita en la base lo lee el servidor al CARGAR: para tocar a un personaje que está
    /// conectado está la pestaña en vivo, que le llega en el acto por su propia sesión.
    /// </summary>
    internal sealed class AdminWindow : Form
    {
        private readonly AdminClient _client = new();

        // ─── El escalado ──────────────────────────────────────────────────────────────────────
        //
        // Lo mismo que el lanzador y la ventana del servidor: en una pantalla de muchas pulgadas
        // con la escala de Windows al 100%, el dpi se queda en 96 y todo sale diminuto. El
        // ampliador (A– / A+, abajo a la derecha) crece POR ENCIMA del dpi, se aplica en el acto y
        // se recuerda entre arranques; la primera vez hereda el que ya se haya elegido en el
        // lanzador o en el servidor, que es la misma pantalla.

        private float _escala = 1f;
        private int E(int px) => (int)Math.Round(px * _escala);

        /// <summary>El cuerpo con el que se creó cada fuente, para rehacerla al cambiar el ampliador.</summary>
        private sealed class Fuente
        {
            public float Cuerpo;
            public FontStyle Estilo = FontStyle.Regular;
            public bool Mono;
        }

        /// <summary>Apunta un control con el cuerpo de su fuente, para poder rehacerla luego.</summary>
        private static void Marcar(Control control, float cuerpo, bool negrita = false, bool mono = false)
            => control.Tag = new Fuente
            {
                Cuerpo = cuerpo,
                Estilo = negrita ? FontStyle.Bold : FontStyle.Regular,
                Mono = mono,
            };

        /// <summary>Vuelve a crear la fuente de cada control marcado, con el ampliador de ahora.</summary>
        private void AplicarFuentes(Control.ControlCollection controles)
        {
            foreach (Control control in controles)
            {
                if (control.Tag is Fuente fuente)
                    control.Font = fuente.Mono
                        ? LauncherTheme.CreateMonoFont(fuente.Cuerpo, fuente.Estilo)
                        : LauncherTheme.CreateFont(fuente.Cuerpo, fuente.Estilo);
                if (control is Button boton) boton.Height = E(28);
                AplicarFuentes(control.Controls);
            }
        }

        // ─── La pestaña de base de datos ──────────────────────────────────────────────────────

        private readonly ComboBox _dbChoice = new();
        private readonly Label _dbPath = new();
        private readonly ListBox _tables = new();
        private readonly DataGridView _grid = new();
        private readonly TextBox _filter = new();
        private readonly Button _btnRefresh = new();
        private readonly Button _btnPrev = new();
        private readonly Button _btnNext = new();
        private readonly Label _pageInfo = new();
        private readonly TextBox _sql = new();
        private readonly Button _btnSql = new();

        // Las piezas del layout con alto fijo, que también siguen al ampliador.
        private FlowLayoutPanel _dbTop = null!;
        private Panel _dbPathPanel = null!;
        private Label _sqlHint = null!;
        private Panel _sqlPanel = null!;

        private const int PageSize = 200;
        private int _page;
        private long _rowCount;
        private string _table = "";
        private List<(string Nombre, bool EsPk)> _columns = new();

        /// <summary>El valor de la celda tal como estaba antes de la edición en curso.</summary>
        private string? _valorAntes;

        /// <summary>
        /// Si la tabla no declara clave primaria, se ancla por el rowid: la columna oculta que
        /// SQLite le da a toda fila y que siempre es única. Así se puede editar CUALQUIER tabla,
        /// no sólo las bien declaradas.
        /// </summary>
        private bool _usaRowid;

        /// <summary>El nombre con el que se pide el rowid por SELECT, y la columna que lo lleva.</summary>
        private const string ColumnaRowid = "__rowid__";

        /// <summary>El Enter del control de edición: guarda y se queda, sin bajar de fila.</summary>
        private void EditarConEnter(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            try { _grid.EndEdit(); } catch { }
        }

        /// <summary>
        /// Vacia la rejilla sin romperla: se cierra la edición que hubiera abierta y se suelta la
        /// celda actual ANTES de quitar columnas y filas. Quitar columnas con una celda puesta o
        /// una edición en marcha es lo que reventaba con la llamada reentrante y dejaba la
        /// rejilla en blanco con la cruz roja.
        /// </summary>
        private void VaciarRejilla()
        {
            try { _grid.EndEdit(); } catch { }
            try { _grid.CurrentCell = null; } catch { }
            _grid.Rows.Clear();
            _grid.Columns.Clear();
        }

        // Los valores de las claves primarias tal como se CARGARON, una lista por fila: es con lo
        // que se identifica la fila al editarla o borrarla, aunque alguien cambie la clave dentro
        // de la propia tabla.
        private List<Dictionary<string, string?>> _pkValues = new();

        // ─── La pestaña del servidor en vivo ──────────────────────────────────────────────────

        private readonly TextBox _host = new();
        private readonly TextBox _user = new();
        private readonly TextBox _pass = new();
        private readonly Button _btnSignIn = new();
        private readonly Label _sessionInfo = new();
        private readonly DataGridView _sessions = new();
        private readonly Button _btnKick = new();
        private readonly TextBox _command = new();
        private readonly Button _btnCommand = new();
        private readonly TextBox _broadcast = new();
        private readonly Button _btnBroadcast = new();
        private readonly RichTextBox _liveLog = new();
        private readonly System.Windows.Forms.Timer _poll = new();
        private long _lastLogId;
        private FlowLayoutPanel _liveTop = null!;
        private Panel _liveInfoPanel = null!;
        private FlowLayoutPanel _liveActions = null!;
        private Label _commandHint = null!;
        private SplitContainer _mitad = null!;

        // ─── Comunes ──────────────────────────────────────────────────────────────────────────

        private TabControl _tabs = null!;
        private readonly StatusStrip _statusStrip = new();
        private readonly ToolStripStatusLabel _status = new();
        private readonly ToolStripButton _zoomOut = new();
        private readonly ToolStripButton _zoomIn = new();

        public AdminWindow()
        {
            // Antes de crear nada: el ampliador decide el tamaño de las letras que se crean ahora.
            LauncherTheme.UiZoom = AdminPreferences.Zoom;
            _escala = DeviceDpi / 96f;

            Text = "Jondo Admin";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(E(1000), E(640));
            Size = new Size(E(1280), E(800));
            WindowState = FormWindowState.Maximized;
            BackColor = LauncherTheme.Background;
            ForeColor = LauncherTheme.BaseText;
            Font = LauncherTheme.CreateFont(10.5f);

            BuildDatabaseTab();
            BuildLiveTab();

            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.FlatButtons,
                BackColor = LauncherTheme.Background,
                Font = LauncherTheme.CreateFont(11f, FontStyle.Bold),
                Padding = new Point(14, 6),
            };
            _tabs.TabPages.Add(DatabasePage());
            _tabs.TabPages.Add(LivePage());
            Controls.Add(_tabs);
            _tabs.SelectedIndex = 0;

            _statusStrip.SizingGrip = false;
            _statusStrip.BackColor = Color.FromArgb(24, 16, 10);
            _status.Font = LauncherTheme.CreateFont(9.5f);
            _status.ForeColor = LauncherTheme.MutedGold;
            _status.Spring = true;
            _statusStrip.Items.Add(_status);

            // La ampliación, al final de la barra de estado: el mismo par de botones que el
            // lanzador y la ventana del servidor, para pantallas donde todo sale diminuto.
            _zoomOut.Text = "A–";
            _zoomIn.Text = "A+";
            foreach (var zoom in new[] { _zoomOut, _zoomIn })
            {
                zoom.Font = LauncherTheme.CreateFont(10f, FontStyle.Bold);
                zoom.ForeColor = LauncherTheme.SoftGold;
                zoom.BackColor = Color.FromArgb(24, 16, 10);
            }
            _zoomOut.Click += (s, e) => CambiarZoom(-0.25f);
            _zoomIn.Click += (s, e) => CambiarZoom(+0.25f);
            _statusStrip.Items.Add(_zoomOut);
            _statusStrip.Items.Add(_zoomIn);
            Controls.Add(_statusStrip);

            Say("Ready.");

            _poll.Interval = 4000;
            _poll.Tick += (s, e) => PollLive();
        }

        private void Say(string texto) => _status.Text = texto;

        // ═══════════════════════════════════════════════════════════════════════
        //  La base de datos
        // ═══════════════════════════════════════════════════════════════════════

        private void BuildDatabaseTab()
        {
            _dbChoice.DropDownStyle = ComboBoxStyle.DropDownList;
            _dbChoice.Items.Add("world.db — the world");
            _dbChoice.Items.Add("auth.db — the accounts");
            _dbChoice.SelectedIndex = 0;
            _dbChoice.SelectedIndexChanged += (s, e) => LoadTables();
            ThemeCombo(_dbChoice);

            _dbPath.AutoSize = true;
            _dbPath.ForeColor = LauncherTheme.MutedGold;
            _dbPath.Font = LauncherTheme.CreateFont(9f);
            Marcar(_dbPath, 9f);

            ThemeButton(_btnRefresh, "Load tables");
            _btnRefresh.Click += (s, e) => LoadTables();

            ThemeButton(_btnPrev, "<");
            _btnPrev.Click += (s, e) => { if (_page > 0) { _page--; LoadRows(); } };
            ThemeButton(_btnNext, ">");
            _btnNext.Click += (s, e) =>
            {
                if ((_page + 1) * PageSize < _rowCount) { _page++; LoadRows(); }
            };

            _pageInfo.AutoSize = true;
            _pageInfo.ForeColor = LauncherTheme.MutedGold;
            _pageInfo.Font = LauncherTheme.CreateFont(9.5f);
            Marcar(_pageInfo, 9.5f);

            _filter.ForeColor = LauncherTheme.FieldText;
            _filter.BackColor = LauncherTheme.ConsoleBackground;
            _filter.BorderStyle = BorderStyle.FixedSingle;
            _filter.Font = LauncherTheme.CreateMonoFont(10f);
            Marcar(_filter, 10f, mono: true);
            _filter.TextChanged += (s, e) => { _page = 0; LoadRows(); };

            _tables.BorderStyle = BorderStyle.FixedSingle;
            _tables.BackColor = LauncherTheme.ConsoleBackground;
            _tables.ForeColor = LauncherTheme.BaseText;
            _tables.Font = LauncherTheme.CreateMonoFont(10f);
            Marcar(_tables, 10f, mono: true);
            _tables.HorizontalScrollbar = true;
            _tables.SelectedIndexChanged += (s, e) =>
            {
                _table = _tables.SelectedItem?.ToString() ?? "";
                _page = 0;
                LoadRows();
            };

            ThemeGrid(_grid);
            _grid.AllowUserToDeleteRows = true;
            // Doble clic para editar, y Enter guarda lo editado. La edición se abre FUERA del
            // evento, con BeginInvoke: cambiar la celda actual DENTRO del doble clic —mientras la
            // rejilla sigue procesando el clic, que ya mueve la celda actual por su cuenta— es la
            // llamada reentrante a SetCurrentCellAddressCore que rompía la rejilla entera (el «Read
            // failed», la cruz roja y el cierre a la fuerza). Diferido, el clic ya ha terminado
            // cuando la celda se mueve.
            _grid.EditMode = DataGridViewEditMode.EditProgrammatically;
            _grid.CellMouseDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.ReadOnly) return;
                var celda = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                BeginInvoke(new Action(() =>
                {
                    try { _grid.CurrentCell = celda; _grid.BeginEdit(true); }
                    catch { /* la rejilla pudo recargarse entremedias; no pasa nada */ }
                }));
            };
            _grid.CellBeginEdit += (s, e) =>
            {
                _valorAntes = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
            };
            _grid.CellEndEdit += (s, e) => SaveEdit(e.RowIndex, e.ColumnIndex);
            _grid.UserDeletingRow += (s, e) => { DeleteRow(e.Row.Index); };

            // Enter guarda y se queda en la celda. Su comportamiento de fábrica es guardar Y
            // bajar a la fila siguiente, que en un editor de tablas es una sorpresa: se edita una
            // fila, Enter, y de pronto la selección se ha ido a otra parte. Aquí se intercepta el
            // Enter del control de edición —con Escape no se toca nada, que cancele como siempre—
            // y se cierra la edición en su sitio.
            _grid.EditingControlShowing += (s, e) =>
            {
                if (e.Control is System.Windows.Forms.TextBox control)
                {
                    control.KeyDown -= EditarConEnter;
                    control.KeyDown += EditarConEnter;
                }
            };

            _sql.Multiline = true;
            _sql.ForeColor = LauncherTheme.FieldText;
            _sql.BackColor = LauncherTheme.ConsoleBackground;
            _sql.BorderStyle = BorderStyle.FixedSingle;
            _sql.Font = LauncherTheme.CreateMonoFont(10f);
            Marcar(_sql, 10f, mono: true);
            _sql.AcceptsTab = true;

            ThemeButton(_btnSql, "Run SQL");
            _btnSql.Click += (s, e) => RunSql();
        }

        /// <summary>Las dos rejillas van iguales: mismo fondo, mismas cabeceras, mismo cuerpo mono.</summary>
        private void ThemeGrid(DataGridView grid)
        {
            grid.BackgroundColor = LauncherTheme.ConsoleBackground;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Color.FromArgb(60, 44, 26);
            grid.EnableHeadersVisualStyles = false;
            // AutoSize: sin esto, la fila de cabeceras no crece al ampliar y las cabeceras nuevas
            // se quedan cortadas por abajo.
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(40, 26, 14);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = LauncherTheme.SoftGold;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(40, 26, 14);
            grid.ColumnHeadersDefaultCellStyle.Font = LauncherTheme.CreateFont(9.5f, FontStyle.Bold);
            grid.DefaultCellStyle.BackColor = LauncherTheme.ConsoleBackground;
            grid.DefaultCellStyle.ForeColor = LauncherTheme.BaseText;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(89, 60, 29);
            grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.DefaultCellStyle.Font = LauncherTheme.CreateMonoFont(9.5f);
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.ReadOnly = true;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.RowTemplate.Height = E(26);
        }

        private TabPage DatabasePage()
        {
            var page = NewPage("Database");

            _dbTop = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = E(46),
                BackColor = Color.Transparent,
                WrapContents = false,
                Padding = new Padding(10, 8, 10, 0),
            };
            _dbTop.Controls.Add(_dbChoice);
            _dbTop.Controls.Add(LabelOf("  WHERE  "));
            _dbTop.Controls.Add(_filter);
            _dbTop.SetFlowBreak(_filter, true);
            _dbTop.Controls.Add(_btnRefresh);
            _dbTop.Controls.Add(_btnPrev);
            _dbTop.Controls.Add(LabelOf("  "));
            _dbTop.Controls.Add(_pageInfo);
            _dbTop.Controls.Add(LabelOf("   "));
            _dbTop.Controls.Add(_btnNext);
            _filter.Width = E(320);

            _dbPathPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = E(24),
                BackColor = Color.Transparent,
                WrapContents = false,
                Padding = new Padding(12, 2, 12, 0),
            };
            _dbPathPanel.Controls.Add(_dbPath);

            var centro = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                SplitterDistance = E(240),
                BackColor = Color.Transparent,
            };
            centro.Panel1.Controls.Add(_tables);
            centro.Panel2.Controls.Add(_grid);
            _tables.Dock = DockStyle.Fill;
            _grid.Dock = DockStyle.Fill;

            _sqlPanel = new Panel { Dock = DockStyle.Bottom, Height = E(112), BackColor = Color.Transparent };
            _sql.Dock = DockStyle.Fill;
            var botonSql = new Panel { Dock = DockStyle.Right, Width = E(130), BackColor = Color.Transparent };
            _btnSql.Dock = DockStyle.Fill;
            _btnSql.Padding = new Padding(10);
            botonSql.Controls.Add(_btnSql);
            _sqlHint = new Label
            {
                Dock = DockStyle.Top,
                Height = E(22),
                Text = "Free-form SQL. A SELECT fills the table; anything else runs as-is.",
                ForeColor = LauncherTheme.MutedGold,
                Font = LauncherTheme.CreateFont(9f),
                Padding = new Padding(12, 4, 0, 0),
            };
            Marcar(_sqlHint, 9f);
            _sqlPanel.Controls.Add(_sql);
            _sqlPanel.Controls.Add(botonSql);
            _sqlPanel.Controls.Add(_sqlHint);

            page.Controls.Add(centro);
            page.Controls.Add(_dbTop);
            page.Controls.Add(_dbPathPanel);
            page.Controls.Add(_sqlPanel);
            return page;
        }

        private string CurrentDbPath => _dbChoice.SelectedIndex == 1
            ? Jondo.Unity.Launcher.Paths.AuthDb
            : Jondo.Unity.Launcher.Paths.WorldDb;

        private string CurrentDbConnection => "Data Source=" + CurrentDbPath.Replace('\\', '/') + ";Default Timeout=5";

        private void LoadTables()
        {
            _tables.Items.Clear();
            _table = "";
            VaciarRejilla();
            _dbPath.Text = CurrentDbPath;
            _columns.Clear();
            _pkValues.Clear();

            try
            {
                using var con = new MSFT.SqliteConnection(CurrentDbConnection);
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' " +
                                  "AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) _tables.Items.Add(reader.GetString(0));
                Say($"{_tables.Items.Count} tables in {System.IO.Path.GetFileName(CurrentDbPath)}.");
            }
            catch (Exception ex)
            {
                Say("Could not open the database: " + ex.Message);
            }
        }

        private void LoadColumns()
        {
            _columns.Clear();
            using (var con = new MSFT.SqliteConnection(CurrentDbConnection))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info(\"{_table.Replace("\"", "\"\"")}\");";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    // La columna pk trae la POSICIÓN dentro de la clave (1, 2...) o 0 si no es.
                    long pk = reader.GetInt64(5);
                    _columns.Add((reader.GetString(1), pk > 0));
                }
            }

            // Sin clave declarada, el rowid hace de clave: se añade como primera columna, oculta
            // en la rejilla pero presente en cada fila, y las ediciones y borrados se anclan en
            // ella igual que en las que sí tienen clave. Así se puede editar CUALQUIER tabla.
            _usaRowid = _columns.All(c => !c.EsPk);
            if (_usaRowid) _columns.Insert(0, (ColumnaRowid, true));
            _grid.ReadOnly = false;
        }

        private void LoadRows()
        {
            if (_table.Length == 0) return;
            LoadColumns();

            string where = _filter.Text.Trim();
            string sqlWhere = where.Length > 0 ? " WHERE " + where : "";

            try
            {
                using var con = new MSFT.SqliteConnection(CurrentDbConnection);
                con.Open();

                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM \"{_table}\"{sqlWhere};";
                    _rowCount = (long)cmd.ExecuteScalar()!;
                }

                VaciarRejilla();
                foreach (var (nombre, _) in _columns)
                    _grid.Columns.Add(nombre, nombre);

                // El rowid, si lo hay, va escondido: sirve para anclar, no para mirar. Y la
                // columna entera es de sólo lectura, que cambiarla sería cambiar la identidad de
                // la fila por otra que puede no existir.
                if (_usaRowid)
                {
                    _grid.Columns[0].Visible = false;
                    _grid.Columns[0].ReadOnly = true;
                }

                _pkValues.Clear();
                using (var cmd = con.CreateCommand())
                {
                    string lista = _usaRowid ? $"rowid AS \"{ColumnaRowid}\", *" : "*";
                    cmd.CommandText = $"SELECT {lista} FROM \"{_table}\"{sqlWhere} " +
                                      $"LIMIT {PageSize} OFFSET {_page * PageSize};";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var fila = new string?[reader.FieldCount];
                        for (int i = 0; i < reader.FieldCount; i++)
                            fila[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
                        _grid.Rows.Add(fila);

                        var claves = new Dictionary<string, string?>();
                        for (int i = 0; i < reader.FieldCount && i < _columns.Count; i++)
                            if (_columns[i].EsPk) claves[_columns[i].Nombre] = fila[i];
                        _pkValues.Add(claves);
                    }
                }

                long first = _rowCount == 0 ? 0 : _page * PageSize + 1;
                long last = Math.Min(_rowCount, (_page + 1) * PageSize);
                _pageInfo.Text = $"{first}–{last} of {_rowCount:N0}";
                Say($"'{_table}' loaded. Double-click a cell to edit; Enter saves, Esc cancels; Del deletes the row.");
            }
            catch (Exception ex)
            {
                Say("Read failed: " + ex.Message);
            }
        }

        private void SaveEdit(int fila, int columna)
        {
            if (fila < 0 || fila >= _pkValues.Count || _grid.ReadOnly) return;
            string col = _grid.Columns[columna].Name;
            object? valor = _grid.Rows[fila].Cells[columna].Value;
            string? texto = valor == null ? null : valor.ToString();

            // Sin cambios no hay UPDATE: así el Escape —que devuelve el valor viejo— y el doble
            // clic que se cierra sin tocar nada no escriben en la base.
            if (texto == _valorAntes) return;

            // La fila se identifica por la clave PRIMARIA tal como se cargó, no por lo que acabe de
            // escribirse en ella: si alguien cambia una clave, las demás columnas de esa fila se
            // siguen tocando por la clave vieja hasta la próxima carga.
            Dictionary<string, string?> claves = _pkValues[fila];

            try
            {
                using var con = new MSFT.SqliteConnection(CurrentDbConnection);
                con.Open();
                using var cmd = con.CreateCommand();

                // El SET por un lado y el WHERE por el OTRO. Aquí estaba el fallo que hacía que
                // Enter «no guardara»: las condiciones de la clave se añadían a la MISMA lista
                // del SET, con lo que el SQL salía sin WHERE —«SET Role = @valor, Id = @pk0»— y
                // tocaba TODAS las filas de la tabla. Con dos o más filas eso pisa la clave
                // primaria a todas con la misma y SQLite lo rechaza (UNIQUE constraint failed),
                // el catch recarga la tabla y la celda vuelve al valor viejo: parece que Enter
                // no guarda nada, cuando lo que pasa es que el UPDATE estaba mal escrito.
                var asignaciones = new List<string> { $"\"{col}\" = @valor" };
                var condiciones = new List<string>();
                cmd.Parameters.AddWithValue("@valor", (object?)texto ?? DBNull.Value);
                AnclarClaves(cmd, condiciones, claves);
                cmd.CommandText = $"UPDATE \"{_table}\" SET {string.Join(", ", asignaciones)} " +
                                  $"WHERE {string.Join(" AND ", condiciones)};";
                int cuantas = cmd.ExecuteNonQuery();
                Say(cuantas == 1
                    ? $"'{col}' set to '{texto ?? "NULL"}' in {_table}."
                    : $"The UPDATE changed {cuantas} rows; reloading the table.");

                // Si se ha editado una columna de la clave, el ancla de esta fila pasa a ser el
                // valor nuevo: la próxima edición sobre la misma fila buscará por donde está, no
                // por donde estaba.
                if (cuantas == 1 && _columns.Count > columna && _columns[columna].EsPk)
                    _pkValues[fila][col] = texto;
            }
            catch (Exception ex)
            {
                Say("Could not save: " + ex.Message);
                LoadRows();
            }
        }

        private void DeleteRow(int fila)
        {
            if (fila < 0 || fila >= _pkValues.Count || _grid.ReadOnly) return;
            Dictionary<string, string?> claves = _pkValues[fila];
            try
            {
                using var con = new MSFT.SqliteConnection(CurrentDbConnection);
                con.Open();
                using var cmd = con.CreateCommand();
                var condiciones = new List<string>();
                AnclarClaves(cmd, condiciones, claves);
                cmd.CommandText = $"DELETE FROM \"{_table}\" WHERE {string.Join(" AND ", condiciones)};";
                int cuantas = cmd.ExecuteNonQuery();
                Say(cuantas == 1 ? "Row deleted." : $"The DELETE removed {cuantas} rows; reloading the table.");
                _rowCount -= cuantas;
            }
            catch (Exception ex)
            {
                Say("Could not delete: " + ex.Message);
            }
        }

        /// <summary>
        /// Añade a la lista los «"col" = @pkN» (o «IS NULL») y liga sus valores.
        ///
        /// El rowid viaja por la rejilla con nombre prestado —por si la tabla tuviera una columna
        /// que se llamara igual—, pero contra la tabla se pregunta por el rowid de verdad: una
        /// columna «__rowid__» no existe en ella, y el WHERE la rechazaría con un «no such
        /// column». Es lo que arregla el WHERE del UPDATE y del DELETE en las tablas sin clave.
        /// </summary>
        private static void AnclarClaves(MSFT.SqliteCommand cmd, List<string> trozo,
                                         Dictionary<string, string?> claves)
        {
            int n = 0;
            foreach (var (nombre, valor) in claves)
            {
                string columna = nombre == ColumnaRowid ? "rowid" : $"\"{nombre}\"";
                if (valor == null) trozo.Add($"{columna} IS NULL");
                else
                {
                    string param = "@pk" + n++;
                    cmd.Parameters.AddWithValue(param, valor);
                    trozo.Add($"{columna} = {param}");
                }
            }
        }

        private void RunSql()
        {
            string sql = _sql.Text.Trim();
            if (sql.Length == 0) return;

            try
            {
                using var con = new MSFT.SqliteConnection(CurrentDbConnection);
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = sql;

                if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                    sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) ||
                    sql.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = cmd.ExecuteReader();
                    VaciarRejilla();
                    for (int i = 0; i < reader.FieldCount; i++)
                        _grid.Columns.Add(reader.GetName(i), reader.GetName(i));
                    int filas = 0;
                    while (reader.Read() && filas < 2000)
                    {
                        var fila = new string?[reader.FieldCount];
                        for (int i = 0; i < reader.FieldCount; i++)
                            fila[i] = reader.IsDBNull(i) ? null : reader.GetValue(i).ToString();
                        _grid.Rows.Add(fila);
                        filas++;
                    }
                    _pkValues.Clear();
                    _grid.ReadOnly = true;
                    _pageInfo.Text = $"{filas} rows";
                    Say("Query executed.");
                }
                else
                {
                    int cuantas = cmd.ExecuteNonQuery();
                    Say($"{cuantas} rows affected.");
                    if (_table.Length > 0) LoadRows();
                }
            }
            catch (Exception ex)
            {
                Say("The SQL failed: " + ex.Message);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  El servidor en vivo
        // ═══════════════════════════════════════════════════════════════════════

        private void BuildLiveTab()
        {
            ThemeField(_host, "127.0.0.1");
            _host.Width = E(120);
            ThemeField(_user, "");
            _user.Width = E(150);
            ThemeField(_pass, "");
            _pass.Width = E(150);
            _pass.UseSystemPasswordChar = true;

            ThemeButton(_btnSignIn, "Sign in");
            _btnSignIn.Click += (s, e) => SignIn();

            _sessionInfo.AutoSize = true;
            _sessionInfo.ForeColor = LauncherTheme.MutedGold;
            _sessionInfo.Font = LauncherTheme.CreateFont(9.5f);
            Marcar(_sessionInfo, 9.5f);
            _sessionInfo.Text = "No session. Sign in with an administrator account.";

            ThemeGrid(_sessions);
            _sessions.Columns.Add("cuenta", "Account");
            _sessions.Columns.Add("personaje", "Character");
            _sessions.Columns.Add("nivel", "Level");
            _sessions.Columns.Add("mapa", "Map");
            _sessions.Columns.Add("casilla", "Cell");
            _sessions.Columns.Add("enElMundo", "In world");
            _sessions.Columns.Add("enCombate", "In combat");
            _sessions.Columns.Add("conectado", "Connected");

            ThemeButton(_btnKick, "Kick selected");
            _btnKick.Click += (s, e) => KickSelected();

            ThemeField(_command, ".kamas 1000000");
            _command.Width = E(320);
            ThemeButton(_btnCommand, "Run");
            _btnCommand.Click += (s, e) => RunCommand();

            ThemeField(_broadcast, "");
            _broadcast.Width = E(320);
            ThemeButton(_btnBroadcast, "Broadcast");
            _btnBroadcast.Click += (s, e) => SendBroadcast();

            _liveLog.ReadOnly = true;
            _liveLog.BackColor = LauncherTheme.ConsoleBackground;
            _liveLog.ForeColor = LauncherTheme.LogNormal;
            _liveLog.BorderStyle = BorderStyle.None;
            _liveLog.Font = LauncherTheme.CreateMonoFont(9f);
            Marcar(_liveLog, 9f, mono: true);
            _liveLog.DetectUrls = false;
        }

        private TabPage LivePage()
        {
            var page = NewPage("Live server");

            _liveTop = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = E(46),
                BackColor = Color.Transparent,
                WrapContents = false,
                Padding = new Padding(10, 8, 10, 0),
            };
            _liveTop.Controls.Add(LabelOf("Server "));
            _liveTop.Controls.Add(_host);
            _liveTop.Controls.Add(LabelOf("   User "));
            _liveTop.Controls.Add(_user);
            _liveTop.Controls.Add(LabelOf("   Password "));
            _liveTop.Controls.Add(_pass);
            _liveTop.Controls.Add(LabelOf("   "));
            _liveTop.Controls.Add(_btnSignIn);

            _liveInfoPanel = new Panel { Dock = DockStyle.Top, Height = E(24), BackColor = Color.Transparent };
            _sessionInfo.Dock = DockStyle.Fill;
            _sessionInfo.Padding = new Padding(12, 4, 0, 0);
            _liveInfoPanel.Controls.Add(_sessionInfo);

            _liveActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = E(46),
                BackColor = Color.Transparent,
                WrapContents = false,
                Padding = new Padding(10, 6, 10, 0),
            };
            _liveActions.Controls.Add(_btnKick);
            _liveActions.Controls.Add(LabelOf("   Command "));
            _liveActions.Controls.Add(_command);
            _liveActions.Controls.Add(LabelOf("  "));
            _liveActions.Controls.Add(_btnCommand);
            _liveActions.Controls.Add(LabelOf("   Broadcast "));
            _liveActions.Controls.Add(_broadcast);
            _liveActions.Controls.Add(LabelOf("  "));
            _liveActions.Controls.Add(_btnBroadcast);

            _commandHint = new Label
            {
                Dock = DockStyle.Top,
                Height = E(22),
                Text = "Commands run on the selected character: .kamas <n> · .level <n> · .teleport [x,y] · .size <n> · .shop",
                ForeColor = LauncherTheme.MutedGold,
                Font = LauncherTheme.CreateFont(9f),
                Padding = new Padding(12, 3, 0, 0),
            };
            Marcar(_commandHint, 9f);

            _mitad = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                FixedPanel = FixedPanel.Panel2,
                SplitterDistance = Math.Max(E(200), ClientSize.Height - E(300)),
                BackColor = Color.Transparent,
            };
            _sessions.Dock = DockStyle.Fill;
            _liveLog.Dock = DockStyle.Fill;
            _mitad.Panel1.Controls.Add(_sessions);
            _mitad.Panel2.Controls.Add(_liveLog);

            page.Controls.Add(_mitad);
            page.Controls.Add(_commandHint);
            page.Controls.Add(_liveActions);
            page.Controls.Add(_liveInfoPanel);
            page.Controls.Add(_liveTop);
            return page;
        }

        private void SignIn()
        {
            _client.Host = _host.Text.Trim().Length > 0 ? _host.Text.Trim() : "127.0.0.1";
            string? fallo = _client.SignIn(_user.Text.Trim(), _pass.Text);
            if (fallo != null)
            {
                _sessionInfo.Text = "Could not sign in: " + fallo + ".";
                Say("No session with the server.");
                return;
            }

            _pass.Text = "";
            _sessionInfo.Text = $"Signed in as {_client.Nickname} (account {_client.AccountId}). " +
                                "Sessions refresh on their own.";
            _poll.Start();
            PollLive();
            Say("Administration session open.");
        }

        private void PollLive()
        {
            if (!_client.Connected) return;

            if (_client.Status() == null)
            {
                _sessionInfo.Text = "The server is not answering on " + _client.Host + ".";
                return;
            }

            RefreshSessions();
            RefreshLiveLog();
        }

        private void RefreshSessions()
        {
            var raiz = _client.Sessions();
            if (raiz == null || !raiz.Value.TryGetProperty("sesiones", out var lista)) return;

            var vivas = new List<(long Cuenta, string Personaje, string Nivel, string Mapa,
                                  string Casilla, string Mundo, string Combate, string Cuando)>();
            foreach (var s in lista.EnumerateArray())
            {
                vivas.Add((
                    s.TryGetProperty("cuenta", out var c) ? c.GetInt64() : 0,
                    s.TryGetProperty("personaje", out var p) ? p.GetString() ?? "" : "",
                    s.TryGetProperty("nivel", out var n) ? n.GetInt32().ToString() : "",
                    s.TryGetProperty("mapa", out var m) ? m.GetInt64().ToString() : "",
                    s.TryGetProperty("casilla", out var ca) ? ca.GetInt32().ToString() : "",
                    s.TryGetProperty("enElMundo", out var en) && en.GetBoolean() ? "yes" : "no",
                    s.TryGetProperty("enCombate", out var co) && co.GetBoolean() ? "yes" : "no",
                    s.TryGetProperty("conectado", out var cu) && cu.ValueKind == JsonValueKind.String
                        ? cu.GetString() ?? "" : ""));
            }

            long seleccionada = SelectedAccount();
            _sessions.Rows.Clear();
            foreach (var v in vivas)
                _sessions.Rows.Add(v.Cuenta, v.Personaje, v.Nivel, v.Mapa, v.Casilla, v.Mundo, v.Combate, v.Cuando);

            if (seleccionada > 0)
                foreach (DataGridViewRow fila in _sessions.Rows)
                    if ((long)fila.Cells[0].Value == seleccionada) { fila.Selected = true; break; }

            _sessionInfo.Text = $"{vivas.Count} live session(s). The log refreshes every 4 s.";
        }

        private long SelectedAccount()
        {
            if (_sessions.SelectedRows.Count == 0) return 0;
            return _sessions.SelectedRows[0].Cells[0].Value is long l ? l : 0;
        }

        private void KickSelected()
        {
            long cuenta = SelectedAccount();
            if (cuenta <= 0) { Say("Select a session first."); return; }

            var (bien, motivo) = _client.Kick(cuenta);
            Say(bien ? $"Account {cuenta} has been kicked." : "Could not kick: " + motivo + ".");
            RefreshSessions();
        }

        private void RunCommand()
        {
            long cuenta = SelectedAccount();
            string orden = _command.Text.Trim();
            if (cuenta <= 0) { Say("Select a session first."); return; }
            if (orden.Length == 0) { Say("Type a command, for example .kamas 1000000."); return; }

            var (bien, motivo) = _client.Command(cuenta, orden);
            Say(bien ? $"'{orden}' executed on account {cuenta}." : "Not executed: " + motivo + ".");
        }

        private void SendBroadcast()
        {
            string texto = _broadcast.Text.Trim();
            if (texto.Length == 0) { Say("Type what you want to broadcast."); return; }

            var (bien, cuantos) = _client.Broadcast(texto);
            Say(bien ? $"Broadcast to {cuantos} in the world." : "Could not broadcast.");
            _broadcast.Text = "";
        }

        private void RefreshLiveLog()
        {
            string json = _client.ServerLog(_lastLogId);
            if (json.Length == 0) return;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("logs", out var lista)) return;
                foreach (var linea in lista.EnumerateArray())
                {
                    long id = linea.TryGetProperty("id", out var i) ? i.GetInt64() : 0;
                    if (id > _lastLogId) _lastLogId = id;
                    string hora = linea.TryGetProperty("time", out var h) ? h.GetString() ?? "" : "";
                    string msg = linea.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";

                    _liveLog.SelectionStart = _liveLog.TextLength;
                    _liveLog.SelectionLength = 0;
                    _liveLog.SelectionColor = LauncherTheme.LogTime;
                    _liveLog.AppendText(hora.Length > 0 ? hora + "  " : "");
                    _liveLog.SelectionColor = ColorDeLinea(msg);
                    _liveLog.AppendText(msg + "\n");
                }
                if (_liveLog.TextLength > 400_000)
                {
                    _liveLog.Select(0, _liveLog.TextLength - 300_000);
                    _liveLog.SelectedText = "";
                }
                _liveLog.SelectionStart = _liveLog.TextLength;
                _liveLog.ScrollToCaret();
            }
            catch { }
        }

        private static Color ColorDeLinea(string linea)
        {
            if (linea.Contains("[!]") || linea.Contains("Error") || linea.Contains("error"))
                return LauncherTheme.LogError;
            if (linea.Contains("[HAAPI]")) return LauncherTheme.LogHaapi;
            if (linea.Contains("[Zaap")) return LauncherTheme.LogZaap;
            if (linea.Contains("[+]")) return LauncherTheme.LogSuccess;
            if (linea.Contains("[Combate]") || linea.Contains("[FightHandler]")) return LauncherTheme.LogServer;
            if (linea.Contains("[Admin]") || linea.Contains("[Control]")) return LauncherTheme.HighlightText;
            return LauncherTheme.LogNormal;
        }

        // ═══════════════════════════════════════════════════════════════════════
        //  Lo común
        // ═══════════════════════════════════════════════════════════════════════

        private static TabPage NewPage(string titulo)
        {
            var page = new TabPage(titulo)
            {
                BackColor = LauncherTheme.Background,
                Padding = new Padding(4),
            };
            return page;
        }

        private static Label LabelOf(string texto) => new()
        {
            Text = texto,
            AutoSize = true,
            ForeColor = LauncherTheme.SoftGold,
            Font = LauncherTheme.CreateFont(10f),
            Padding = new Padding(0, 8, 0, 0),
            BackColor = Color.Transparent,
            Tag = new Fuente { Cuerpo = 10f },
        };

        private void ThemeButton(Button boton, string texto)
        {
            boton.Text = texto;
            boton.FlatStyle = FlatStyle.Flat;
            boton.FlatAppearance.BorderColor = LauncherTheme.BorderBrown;
            boton.FlatAppearance.MouseOverBackColor = Color.FromArgb(89, 60, 29);
            boton.BackColor = Color.FromArgb(40, 26, 14);
            boton.ForeColor = LauncherTheme.SoftGold;
            boton.Font = LauncherTheme.CreateFont(10f, FontStyle.Bold);
            boton.Tag = new Fuente { Cuerpo = 10f, Estilo = FontStyle.Bold };
            boton.Cursor = Cursors.Hand;
            boton.Height = E(28);
            boton.Padding = new Padding(8, 0, 8, 0);
            boton.Margin = new Padding(4, 4, 4, 4);
        }

        private void ThemeCombo(ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat;
            combo.BackColor = Color.FromArgb(40, 26, 14);
            combo.ForeColor = LauncherTheme.BaseText;
            combo.Font = LauncherTheme.CreateFont(10f);
            combo.Tag = new Fuente { Cuerpo = 10f };
            combo.Width = E(200);
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.Margin = new Padding(4, 4, 4, 4);
        }

        private void ThemeField(TextBox campo, string texto)
        {
            campo.Text = texto;
            campo.ForeColor = LauncherTheme.FieldText;
            campo.BackColor = LauncherTheme.ConsoleBackground;
            campo.BorderStyle = BorderStyle.FixedSingle;
            campo.Font = LauncherTheme.CreateFont(10f);
            campo.Tag = new Fuente { Cuerpo = 10f };
            campo.Margin = new Padding(4, 4, 4, 4);
        }

        // ─── La ampliación ────────────────────────────────────────────────────────────────────
        //
        // Cambiar el tamaño en caliente rehace las fuentes (cada control lleva apuntado el cuerpo
        // con el que nació), los altos fijos del layout y el alto de las filas de las dos
        // rejillas —que no lo siguen solo—, y queda guardado para el próximo arranque.

        private void CambiarZoom(float paso)
        {
            float antes = AdminPreferences.Zoom;
            float despues = MathF.Max(0.5f, MathF.Min(3f, antes + paso));
            if (MathF.Abs(despues - antes) < 0.01f) return;

            AdminPreferences.Zoom = despues;
            AplicarZoom();
        }

        private void AplicarZoom()
        {
            LauncherTheme.UiZoom = AdminPreferences.Zoom;
            _escala = DeviceDpi / 96f;
            MinimumSize = new Size(E(1000), E(640));

            Font = LauncherTheme.CreateFont(10.5f);
            _tabs.Font = LauncherTheme.CreateFont(11f, FontStyle.Bold);
            _zoomOut.Font = _zoomIn.Font = LauncherTheme.CreateFont(10f, FontStyle.Bold);
            AplicarFuentes(Controls);

            // Las dos rejillas: sus fuentes no van por Tag porque cada una lleva tres distintas, y
            // las filas ya añadidas no crecen solas al crecer la letra.
            _grid.ColumnHeadersDefaultCellStyle.Font = LauncherTheme.CreateFont(9.5f, FontStyle.Bold);
            _grid.DefaultCellStyle.Font = LauncherTheme.CreateMonoFont(9.5f);
            _sessions.ColumnHeadersDefaultCellStyle.Font = LauncherTheme.CreateFont(9.5f, FontStyle.Bold);
            _sessions.DefaultCellStyle.Font = LauncherTheme.CreateMonoFont(9.5f);
            _grid.RowTemplate.Height = E(26);
            _sessions.RowTemplate.Height = E(26);
            foreach (DataGridViewRow fila in _grid.Rows) fila.Height = E(26);
            foreach (DataGridViewRow fila in _sessions.Rows) fila.Height = E(26);

            // Los altos fijos del layout siguen al ampliador, o los controles dejan de caber.
            _dbTop.Height = E(46);
            _dbPathPanel.Height = E(24);
            _sqlHint.Height = E(22);
            _sqlPanel.Height = E(112);
            _liveTop.Height = E(46);
            _liveInfoPanel.Height = E(24);
            _liveActions.Height = E(46);
            _commandHint.Height = E(22);
            _mitad.SplitterDistance = Math.Max(E(200), Math.Max(E(240), ClientSize.Height - E(300)));

            _dbChoice.Width = E(200);
            _filter.Width = E(320);
            _host.Width = E(120);
            _user.Width = E(150);
            _pass.Width = E(150);
            _command.Width = E(320);
            _broadcast.Width = E(320);

            Invalidate(true);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            LoadTables();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _poll.Stop();
            base.OnFormClosed(e);
        }
    }
}
