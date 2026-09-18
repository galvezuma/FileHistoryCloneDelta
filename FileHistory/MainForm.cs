using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FileHistory
{
    public partial class MainForm : Form
    {
        Settings _settings;
        IBackupDb _db;
        Crawler _crawler;
        ILoggerFactory _loggerFactory;
        ILogger _logger;
        Task _fileCountUpdateTask;
        Task _fileCountCrawlingTask;
        CancellationTokenSource _cts;
        public static MainForm Instance;

        /// <summary>
        /// コンストラクタ
        /// </summary>
        public MainForm(Settings settings, IBackupDb db, ILoggerFactory loggerFactory, Crawler crawler)
        {
            _settings = settings;
            _db = db;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<MainForm>();
            _cts = new CancellationTokenSource();
            _crawler = crawler;
            Instance = this;

            InitializeComponent();
            ApplyLocalization();
        }

        /// <summary>
        /// フォーム破棄後のInvokeによる例外を防ぎつつUIスレッドで実行する
        /// </summary>
        void SafeInvoke(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                Invoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// アプリのバージョン表記 ("1.0.1")。ビルドメタデータ(+以降)は表示しない。
        /// (Application.ProductVersion はエントリアセンブリ依存のため自アセンブリから取る)
        /// </summary>
        static string AppVersion =>
            (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                typeof(MainForm).Assembly)?.InformationalVersion ?? Application.ProductVersion).Split('+')[0];

        /// <summary>
        /// UI文字列をリソースから設定
        /// </summary>
        void ApplyLocalization()
        {
            Text = $"{Strings.Get("MainForm_Title")} - v{AppVersion}";
            label1.Text = Strings.Get("MainForm_BackedUpFileCount");
            label2.Text = Strings.Get("MainForm_CrawledFileCount");
            menuTools.Text = Strings.Get("MainForm_MenuTools");
            menuItemSettings.Text = Strings.Get("MainForm_MenuSettings");
            menuItemOpenConfig.Text = Strings.Get("MainForm_MenuOpenConfig");
            menuItemCleanup.Text = Strings.Get("MainForm_MenuCleanup");
            menuHelp.Text = Strings.Get("MainForm_MenuHelp");
            menuItemAbout.Text = Strings.Get("MainForm_MenuAbout");
        }

        /// <summary>設定画面を開く(保存されたら再起動を促す)</summary>
        private void menuItemSettings_Click(object sender, EventArgs e)
        {
            _logger.LogInformation("Menu-OpenSettings by user operation.");
            Program.OpenSettings();
        }

        /// <summary>エクスプローラーで appsettings.json の場所を開く</summary>
        private void menuItemOpenConfig_Click(object sender, EventArgs e)
        {
            try
            {
                _logger.LogInformation("Menu-OpenConfigLocation by user operation.");
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Program.ConfigPath}\"");
            }
            catch (Exception ex)
            {
                _logger.LogError("Exception caught in menuItemOpenConfig_Click(): {ex}", ex);
            }
        }

        /// <summary>バージョン情報を表示する</summary>
        private void menuItemAbout_Click(object sender, EventArgs e)
        {
            MessageBox.Show(this,
                Strings.Format("MainForm_AboutText", Application.ProductName, AppVersion, Program.ConfigPath),
                Strings.Get("MainForm_AboutTitle"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// 初期化
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainForm_Load(object sender, EventArgs e)
        {
            // 「バックアップ済みファイル数」更新タスク登録
            _fileCountUpdateTask = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        if (_db != null)
                        {
                            var count = await _db.FileCount(_cts.Token).ConfigureAwait(false);
                            SafeInvoke(() => { fileCount.Text = count.ToString("#,0"); });
                        }
                        await Task.Delay(10000, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!_cts.IsCancellationRequested)
                            _logger.LogError($"Exception caught in FileCountUpdateTask: {ex}");
                        break;
                    }
                }
                _logger.LogDebug($"FileCountUpdateTask End");
            });

            // 「クローリング済みファイル数」更新タスク登録
            _fileCountCrawlingTask = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        if (_crawler != null)
                        {
                            var count = _crawler.FileCount();
                            SafeInvoke(() => { crawlingCount.Text = count.ToString("#,0"); });
                        }
                        await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (!_cts.IsCancellationRequested)
                            _logger.LogError($"Exception caught in FileCountCrawlingTask: {ex}");
                        break;
                    }
                }
                _logger.LogDebug($"FileCountCrawlingTask End");
            });

            // TreeView初期化
            var node = new TreeNode("BackupFiles");
            node.Tag = new DirectoryDbEntry { Id = -1 };
            node.Nodes.Add("");
            treeView.Nodes.Add(node);
            treeView.BeforeExpand += TreeView_BeforeExpand;
            treeView.NodeMouseClick += TreeView_NodeMouseClick;

            // ListView初期化
            listView.Columns.Add(new ColumnHeader { Text = "#", Width = 60, TextAlign = HorizontalAlignment.Right });
            listView.Columns.Add(new ColumnHeader { Text = Strings.Get("MainForm_ColBackupTime"), Width = 200, TextAlign = HorizontalAlignment.Right });
            listView.Columns.Add(new ColumnHeader { Text = Strings.Get("MainForm_ColSize"), Width = 200, TextAlign = HorizontalAlignment.Right });
            listView.Columns.Add(new ColumnHeader { Text = Strings.Get("MainForm_ColLastWrite"), Width = 200, TextAlign = HorizontalAlignment.Right });
            listView.FullRowSelect = true;
            listView.MouseClick += ListView_MouseClick;
        }

        /// <summary>
        /// Mostrar el menú contextual al hacer clic con el botón derecho sobre un archivo.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ListView_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            if (listView.SelectedItems.Count == 0) return;

            var item = treeView.SelectedNode?.Tag as FileDbEntry;
            if (item == null) return;
            var menu = new ContextMenuStrip();
            var openItem = new ToolStripMenuItem(Strings.Format("MainForm_OpenVersion", item.Name));
            openItem.Click += FileOpenTempMenuItem_Click;
            menu.Items.Add(openItem);
            var menuItem = new ToolStripMenuItem(Strings.Format("MainForm_RestoreFile", item.Name));
            menuItem.Click += FileSubMenuItem_Click;
            menu.Items.Add(menuItem);
            menu.Show(Cursor.Position);
        }

        /// <summary>
        /// Copiar la versión seleccionada a un archivo temporal y abrirla con la aplicación predeterminada (vista previa).
        /// </summary>
        private void FileOpenTempMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                var fileDbEntry = treeView.SelectedNode?.Tag as FileDbEntry;
                var attrDbEntry = listView.SelectedItems.Count > 0 ? listView.SelectedItems[0].Tag as AttributeDbEntry : null;
                if (fileDbEntry == null || attrDbEntry == null)
                {
                    MessageBox.Show(Strings.Get("MainForm_FileNotFound"), Strings.Get("Common_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Copiar y abrir el archivo en una carpeta temporal única,
                // conservando su nombre de archivo original.
                var tempDir = Path.Combine(Path.GetTempPath(), "FileHistoryClone", Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(tempDir);

                var originalFileName = Path.GetFileName(fileDbEntry.Name);
                var tempFile = Path.Combine(tempDir, originalFileName);

                // Funciona tanto para:
                // - paquetes FHC: checkout o cadena de deltas;
                // - backups legacy, si RestoreHelper conserva esa compatibilidad.
                using (var reconstructed = RestoreHelper.ReconstructAttribute(_db, _settings, attrDbEntry, _loggerFactory))
                using (var output = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    reconstructed.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                // Establecerlo como de solo lectura para indicar que cualquier edición no se guardará ni se reflejará en el ar
                try {
                    var attributes = File.GetAttributes(tempFile);
                    File.SetAttributes(tempFile, attributes | FileAttributes.ReadOnly);
                } catch (Exception ex) {
                    _logger.LogDebug(
                        ex,
                        "No se pudo establecer el atributo ReadOnly en {TempFile}.",
                        tempFile);
                }

                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(tempFile)
                    {
                        UseShellExecute = true
                    });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception caught in FileOpenTempMenuItem_Click(): {ex}");
                MessageBox.Show(Strings.Get("MainForm_FileNotFound"), Strings.Get("Common_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Restaurar el archivo.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FileSubMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                // Comprobar el archivo de origen.
                var fileDbEntry = treeView.SelectedNode?.Tag as FileDbEntry;
                var attrDbEntry = listView.SelectedItems.Count > 0
                    ? listView.SelectedItems[0].Tag as AttributeDbEntry
                    : null;

                if (fileDbEntry == null || attrDbEntry == null)
                {
                    MessageBox.Show(Strings.Get("MainForm_FileNotFound"), Strings.Get("Common_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Especificar el directorio de restauración.
                // La carpeta inicial es la ubicación original del archivo.
                var backupFileDir = _db.GetFileDir(fileDbEntry.Id);
                string distDir;

                using (var fbd = new FolderBrowserDialog
                {
                    Description = Strings.Get("MainForm_SelectRestoreFolder"),
                    UseDescriptionForTitle = true
                })
                {
                    if (Directory.Exists(backupFileDir)) fbd.SelectedPath = backupFileDir;
                    if (fbd.ShowDialog() != DialogResult.OK) return;
                    distDir = fbd.SelectedPath;
                }

                if (string.IsNullOrWhiteSpace(distDir)) return;

                Directory.CreateDirectory(distDir);

                // Path.GetFileName evita que un valor inesperado de Name pueda
                // crear subdirectorios fuera del directorio elegido.
                var targetFileName = Path.GetFileName(fileDbEntry.Name);

                if (string.IsNullOrWhiteSpace(targetFileName))
                {
                    throw new InvalidDataException("El nombre del archivo a restaurar no es válido.");
                }

                var targetPath = Path.Combine(distDir, targetFileName);
                Stream? restoredStream = null;
                try
                {
                    if (!string.IsNullOrEmpty(attrDbEntry.StoredFileName))
                    {
                        // Restauración desde el formato FHC actual.
                        var fhcPath = Path.Combine(_settings.DataDir, attrDbEntry.StoredFileName);
                        if (!File.Exists(fhcPath))
                        {
                            _logger.LogError("Restore failed - stored .fhc not found: {Path}", fhcPath);

                            MessageBox.Show(
                                Strings.Format(
                                    "MainForm_FileNotFoundWithPath",
                                    fhcPath),
                                Strings.Get("Common_Error"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);

                            return;
                        }

                        using var fhcFs = File.OpenRead(fhcPath);
                        var (meta, payload) = FhcPackage.ExtractPackageAsync(fhcFs, CancellationToken.None).GetAwaiter().GetResult();
                        using (payload)
                        {
                            if (string.Equals(
                                meta.Type,
                                "checkout",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                // En este diseño el payload de checkout es el
                                // contenido original completo del archivo.
                                restoredStream = new MemoryStream();
                                if (payload.CanSeek) payload.Seek(0, SeekOrigin.Begin);

                                payload.CopyTo(restoredStream);
                                restoredStream.Seek(0, SeekOrigin.Begin);
                            }
                            else if (string.Equals(meta.Type, "delta", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!attrDbEntry.BaseAttributeId.HasValue)
                                {
                                    throw new InvalidDataException($"El delta {attrDbEntry.Id} no tiene BaseAttributeId.");
                                }

                                var attrs = _db.GetAttributes(fileDbEntry.Id).OrderBy(a => a.BackupTime).ToList();
                                var baseAttr = attrs.FirstOrDefault(a => a.Id == attrDbEntry.BaseAttributeId.Value);
                                if (baseAttr == null || string.IsNullOrEmpty(baseAttr.StoredFileName))
                                {
                                    throw new InvalidDataException($"No se encontró el checkout base " + $"para el delta {attrDbEntry.Id}.");
                                }

                                var baseFhcPath = Path.Combine(_settings.DataDir,baseAttr.StoredFileName);
                                if (!File.Exists(baseFhcPath))
                                {
                                    throw new FileNotFoundException("No se encontró el paquete FHC del checkout base.", baseFhcPath);
                                }

                                Stream? current = null;

                                try
                                {
                                    using (var baseFs = File.OpenRead(baseFhcPath))
                                    {
                                        var (baseMeta, basePayload) =
                                            FhcPackage.ExtractPackageAsync(
                                                baseFs,
                                                CancellationToken.None)
                                            .GetAwaiter()
                                            .GetResult();

                                        using (basePayload)
                                        {
                                            if (!string.Equals(baseMeta.Type, "checkout", StringComparison.OrdinalIgnoreCase))
                                            {
                                                throw new InvalidDataException($"El paquete base {baseFhcPath} " + $"no es un checkout; Type='{baseMeta.Type ?? "<null>"}'.");
                                            }

                                            current = new MemoryStream();

                                            if (basePayload.CanSeek) basePayload.Seek(0, SeekOrigin.Begin);

                                            basePayload.CopyTo(current);
                                            current.Seek(0, SeekOrigin.Begin);
                                        }
                                    }

                                    var deltasToApply = attrs
                                        .Where(a =>
                                            a.BaseAttributeId == baseAttr.Id &&
                                            a.Type == "delta")
                                        .OrderBy(a => a.DeltaIndex)
                                        .ToList();

                                    using var deltaService = new DeltaService(
                                        _loggerFactory.CreateLogger<DeltaService>(),
                                        _settings.AllowOctodiffFallback);

                                    bool foundRequestedDelta = false;

                                    foreach (var deltaAttr in deltasToApply)
                                    {
                                        if (string.IsNullOrWhiteSpace(deltaAttr.StoredFileName))
                                        {
                                            throw new InvalidDataException($"El delta {deltaAttr.Id} no tiene StoredFileName.");
                                        }

                                        var deltaFhcPath = Path.Combine(_settings.DataDir, deltaAttr.StoredFileName);

                                        if (!File.Exists(deltaFhcPath))
                                        {
                                            throw new FileNotFoundException("No se encontró el paquete FHC del delta.", deltaFhcPath);
                                        }

                                        using var deltaFs = File.OpenRead(deltaFhcPath);

                                        var (deltaMeta, deltaPayload) =
                                            FhcPackage.ExtractPackageAsync(
                                                deltaFs,
                                                CancellationToken.None)
                                            .GetAwaiter()
                                            .GetResult();

                                        using (deltaPayload)
                                        {
                                            if (!string.Equals(deltaMeta.Type, "delta", StringComparison.OrdinalIgnoreCase))
                                            {
                                                throw new InvalidDataException($"El paquete {deltaFhcPath} " + $"no es un delta; Type='{deltaMeta.Type ?? "<null>"}'.");
                                            }

                                            if (deltaPayload.CanSeek) deltaPayload.Seek(0, SeekOrigin.Begin);

                                            if (current.CanSeek) current.Seek(0, SeekOrigin.Begin);

                                            var next = deltaService.ApplyDeltaAsync(
                                                current,
                                                deltaPayload,
                                                CancellationToken.None)
                                                .GetAwaiter()
                                                .GetResult();

                                            current.Dispose();
                                            current = next;

                                            if (current.CanSeek)
                                                current.Seek(0, SeekOrigin.Begin);
                                        }

                                        if (deltaAttr.Id == attrDbEntry.Id)
                                        {
                                            foundRequestedDelta = true;
                                            break;
                                        }
                                    }

                                    if (!foundRequestedDelta)
                                    {
                                        throw new InvalidDataException(
                                            $"No se encontró el delta solicitado " +
                                            $"{attrDbEntry.Id} dentro de la cadena " +
                                            $"del checkout {baseAttr.Id}.");
                                    }

                                    restoredStream = current;
                                    current = null;
                                }
                                finally
                                {
                                    current?.Dispose();
                                }
                            }
                            else
                            {
                                throw new InvalidDataException(
                                    $"Tipo FHC desconocido: '{meta.Type ?? "<null>"}'.");
                            }
                        }
                    }
                    else
                    {
                        // Compatibilidad con el formato legacy: backup como fichero
                        // ordinario, fuera de un paquete FHC.
                        var backupFileDir2 = _db.GetFileDir(fileDbEntry.Id);

                        var backupFileFullPath = BackupDb.BackupFileName(
                            _settings.DataDir,
                            Path.Combine(backupFileDir2, fileDbEntry.Name),
                            attrDbEntry.BackupTime);

                        if (!File.Exists(backupFileFullPath))
                        {
                            _logger.LogError(
                                "Restore failed - legacy backup not found: {Path}",
                                backupFileFullPath);

                            MessageBox.Show(
                                Strings.Format(
                                    "MainForm_FileNotFoundWithPath",
                                    backupFileFullPath),
                                Strings.Get("Common_Error"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);

                            return;
                        }

                        restoredStream = File.OpenRead(backupFileFullPath);
                    }

                    if (restoredStream == null)
                    {
                        throw new InvalidDataException("No se pudo obtener un stream restaurado.");
                    }

                    // Confirmar la sobrescritura del archivo de destino.
                    if (File.Exists(targetPath))
                    {
                        var overwriteResult = MessageBox.Show(
                            Strings.Get("MainForm_OverwriteFile"),
                            Strings.Get("MainForm_OverwriteTitle"),
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning);

                        if (overwriteResult != DialogResult.Yes) return;
                    }

                    // Escribir el contenido restaurado.
                    if (restoredStream.CanSeek) restoredStream.Seek(0, SeekOrigin.Begin);

                    using (var outFs = new FileStream(
                        targetPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        restoredStream.CopyTo(outFs);
                        outFs.Flush(flushToDisk: true);
                    }

                    // Establecer las marcas de tiempo del archivo de destino.
                    File.SetCreationTime(targetPath, attrDbEntry.CreationTime);
                    File.SetLastWriteTime(targetPath, attrDbEntry.LastWriteTime);
                    File.SetLastAccessTime(targetPath, attrDbEntry.LastAccessTime);
                }
                finally
                {
                    restoredStream?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Exception caught in FileSubMenuItem_Click().");

                MessageBox.Show(
                    Strings.Get("MainForm_FileNotFound"),
                    Strings.Get("Common_Error"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// ディレクトリを右クリックした際にコンテキストメニュー表示
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right || !(e.Node.Tag is DirectoryDbEntry)) return;

            treeView.SelectedNode = e.Node;
            var menu = new ContextMenuStrip();
            var menuItem = new ToolStripMenuItem(Strings.Format("MainForm_RestoreDirectory", e.Node.Text));
            menuItem.Click += DirectorySubMenuItem_Click;
            menu.Items.Add(menuItem);
            menu.Show(Cursor.Position);
        }

        /// <summary>
        /// ディレクトリをリストア
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DirectorySubMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
            // コピー元ディレクトリ確認
            var dirDbEntry = treeView.SelectedNode?.Tag as DirectoryDbEntry;
            if (dirDbEntry == null)
            {
                MessageBox.Show(Strings.Get("MainForm_DirectoryNotFound"), Strings.Get("Common_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 復元先ディレクトリ指定(初期フォルダは元ディレクトリの親)
            var origParent = Path.GetDirectoryName(_db.GetDirPath(dirDbEntry.Id));
            var distDir = "";
            using (var fbd = new FolderBrowserDialog()
            {
                Description = Strings.Get("MainForm_SelectRestoreFolder"),
                UseDescriptionForTitle = true,
            })
            {
                if (!string.IsNullOrEmpty(origParent) && Directory.Exists(origParent)) fbd.SelectedPath = origParent;
                if (fbd.ShowDialog() != DialogResult.OK) return;
                distDir = fbd.SelectedPath;
            }

            if (Directory.Exists(Path.Combine(distDir, dirDbEntry.Name)))
            {
                if (DialogResult.Yes != MessageBox.Show(Strings.Get("MainForm_OverwriteDirectory"), Strings.Get("MainForm_OverwriteTitle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning)) return;
                Directory.Delete(Path.Combine(distDir, dirDbEntry.Name), true);
            }
            RestoreDirectory(dirDbEntry, distDir);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception caught in DirectorySubMenuItem_Click(): {ex}");
                MessageBox.Show(Strings.Get("MainForm_DirectoryNotFound"), Strings.Get("Common_Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RestoreDirectory(DirectoryDbEntry dirDbEntry, string distDir)
        {
            // ディレクトリ作成
            Directory.CreateDirectory(Path.Combine(distDir, dirDbEntry.Name));

            // ファイルコピー
            foreach (var file in _db.GetChildFiles(dirDbEntry.Id))
            {
                var attr = _db.GetAttributes(file.Id).OrderBy(m => m.BackupTime).LastOrDefault();
                if (attr == null) continue;
                var backupFileDir = _db.GetFileDir(file.Id);
                var backupFileFullPath = BackupDb.BackupFileName(_settings.DataDir, Path.Combine(backupFileDir, file.Name), attr.BackupTime);
                var destFileFullPath = Path.Combine(distDir, dirDbEntry.Name, file.Name);

                try
                {
                    if (!File.Exists(backupFileFullPath))
                    {
                        _logger.LogWarning("RestoreDirectory: source backup missing: {path}", backupFileFullPath);
                        continue;
                    }
                    // ファイルコピー
                    File.Copy(backupFileFullPath, destFileFullPath);
                    // コピー先タイムスタンプ設定
                    File.SetCreationTime(destFileFullPath, attr.CreationTime);
                    File.SetLastWriteTime(destFileFullPath, attr.LastWriteTime);
                    File.SetLastAccessTime(destFileFullPath, attr.LastAccessTime);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RestoreDirectory: failed copying {src} to {dst}", backupFileFullPath, destFileFullPath);
                }
            }

            // ディレクトリコピー
            foreach (var dir in _db.GetChildDirectories(dirDbEntry.Id))
            {
                RestoreDirectory(dir, Path.Combine(distDir, dirDbEntry.Name));
            }
        }

        /// <summary>
        /// ディレクトリ展開
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void TreeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            e.Node.Nodes.Clear();
            foreach (var ch in _db.GetChildDirectories((e.Node.Tag as DirectoryDbEntry).Id).OrderBy(m => m.Name).ToArray())
            {
                var chNode = new TreeNode(ch.Name);
                chNode.Tag = ch;
                chNode.Nodes.Add("");
                e.Node.Nodes.Add(chNode);
            }
            foreach (var ch in _db.GetChildFiles((e.Node.Tag as DirectoryDbEntry).Id).OrderBy(m => m.Name).ToArray())
            {
                var chNode = new TreeNode(ch.Name);
                chNode.Tag = ch;
                e.Node.Nodes.Add(chNode);
            }
        }

        /// <summary>
        /// クリーンアップ
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            _logger.LogInformation($"MainForm_FormClosing() called");
            _cts.Cancel();
            Instance = null;
        }

        /// <summary>
        /// ディレクトリ選択
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void treeView_BeforeSelect(object sender, TreeViewCancelEventArgs e)
        {
            listView.Items.Clear();
            if (e.Node.Tag is FileDbEntry)
            {
                var attributes = _db.GetAttributes((e.Node.Tag as FileDbEntry).Id).OrderByDescending(m => m.BackupTime).ToList();
                for (int i = 0; i < attributes.Count; i++)
                {
                    var entry = new ListViewItem(new string[] {
                        (i+1).ToString(),
                        attributes[i].BackupTime.ToString(),
                        attributes[i].Size.ToString("#,0"),
                        attributes[i].LastWriteTime.ToString(),
                    });
                    entry.Tag = attributes[i];
                    listView.Items.Add(entry);
                }
            }
        }

        /// <summary>
        /// バックアップ整理(ツールメニュー)
        /// </summary>
        private void menuItemCleanup_Click(object sender, EventArgs e)
        {
            try
            {
                _logger.LogTrace("Enter menuItemCleanup_Click()");
                new CleanupForm(_settings, _db, _crawler, _loggerFactory).ShowDialog();
                treeView.Nodes[0].Collapse();
                listView.Items.Clear();
            }
            catch(Exception ex)
            {
                _logger.LogError($"Exception caught in menuItemCleanup_Click(): {ex}");
            }
            finally
            {
                _logger.LogTrace("Leave menuItemCleanup_Click()");
            }
        }
    }
}
