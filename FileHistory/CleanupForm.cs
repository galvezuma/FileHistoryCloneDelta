using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security.Cryptography;

namespace FileHistory
{
    public partial class CleanupForm : Form
    {
        // DI
        readonly Settings _settings;
        readonly IBackupDb _db;
        readonly Crawler _crawler;
        readonly ILogger<CleanupForm> _logger;
        readonly ILoggerFactory _loggerFactory;

        // Worker
        Task _progressWorker;
        Task _cleanupWorker;

        //
        CancellationTokenSource _cts;
        bool _running = false;
        int _scanCount;
        int _filesToScan;
        int _deleteCount;

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="db"></param>
        public CleanupForm(Settings settings, IBackupDb db, Crawler crawler, ILoggerFactory loggerFactory)
        {
            try
            {
                _loggerFactory = loggerFactory;
                _logger = loggerFactory.CreateLogger<CleanupForm>();
                _logger.LogTrace("Enter: CleanupForm()");

                _settings = settings;
                _db = db;
                _crawler = crawler;

                InitializeComponent();

                Text = Strings.Get("Cleanup_Title");
                label1.Text = Strings.Get("Cleanup_ScannedFiles");
                label2.Text = Strings.Get("Cleanup_DeletedFiles");
                buttonClose.Text = Strings.Get("Cleanup_ButtonClose");
                buttonStartStop.Text = Strings.Get("Cleanup_ButtonStart");
                comboBox.Items.Add(Strings.Get("Cleanup_ModeKeepAllLatest"));
                comboBox.Items.Add(Strings.Get("Cleanup_ModeKeepExistingLatest"));
                comboBox.SelectedIndex = 0;
                labelNote.Text = Strings.Get("Cleanup_TemporaryNote");
            }
            catch (Exception ex)
            {
                _logger.LogDebug($"Exception caught in CleanupForm(): {ex}");
                throw;
            }
            finally
            {
                _logger.LogTrace("Exit: CleanupForm()");
            }
        }

        /// <summary>
        /// フォーム破棄後のInvokeによる例外を防ぎつつUIスレッドで実行する
        /// </summary>
        void SafeInvoke(Action action)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            try
            {
                Invoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private Task DeleteBackupAttributeAsync(AttributeDbEntry attribute, CancellationToken token) {
            token.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(attribute.StoredFileName)) {
                throw new InvalidDataException(
                    $"Backup attribute {attribute.Id} has no StoredFileName.");
            }

            var fullPath = Path.Combine(_settings.DataDir, attribute.StoredFileName);
            _logger.LogInformation($"Cleanup {fullPath}");

            if (File.Exists(fullPath)) File.Delete(fullPath);
            _db.DeleteAttribute(attribute.Id);
            _deleteCount++;

            return Task.CompletedTask;
        }

        private async Task PreserveOnlyLatestRecoverableVersionAsync(
                            FileDbEntry file,
                            string originalFullPath,
                            CancellationToken token) {
            token.ThrowIfCancellationRequested();

            var attributes = _db.GetAttributes(file.Id).OrderBy(a => a.BackupTime).ToList();
            if (attributes.Count <= 1) return;

            var latest = attributes[^1];

            // Si el último backup ya es un checkout autocontenido, no hay que reconstruirlo.
            // Solo se eliminan las versiones antiguas.
            if (string.Equals(latest.Type, "checkout", StringComparison.OrdinalIgnoreCase)) {
                foreach (var attr in attributes.Where(a => a.Id != latest.Id)) {
                    token.ThrowIfCancellationRequested();
                    await DeleteBackupAttributeAsync(attr, token).ConfigureAwait(false);
                }
                return;
            }

            if (!string.Equals(latest.Type, "delta", StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidDataException(
                    $"Unknown backup type '{latest.Type}' for attribute {latest.Id}.");
            }

            // El último es un delta. Encontrar el checkout al que está anclada
            // esa cadena de deltas.
            if (latest.BaseAttributeId is null)
                throw new InvalidDataException($"Latest delta attribute {latest.Id} has no BaseAttributeId.");

            var baseCheckout = attributes.SingleOrDefault(a =>
                a.Id == latest.BaseAttributeId.Value &&
                string.Equals(a.Type, "checkout", StringComparison.OrdinalIgnoreCase));

            if (baseCheckout is null) {
                throw new InvalidDataException(
                    $"Base checkout {latest.BaseAttributeId.Value} for delta {latest.Id} was not found.");
            }

            if (string.IsNullOrWhiteSpace(baseCheckout.StoredFileName))
                throw new InvalidDataException($"Checkout attribute {baseCheckout.Id} has no StoredFileName.");

            // Se reconstruye estrictamente la cadena de deltas que pertenece al
            // checkout base y se aplica hasta la última versión.
            var deltas = attributes
                .Where(a =>
                    a.BaseAttributeId == baseCheckout.Id &&
                    string.Equals(a.Type, "delta", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.DeltaIndex)
                .ToList();

            if (deltas.Count == 0 || deltas[^1].Id != latest.Id) {
                throw new InvalidDataException(
                    $"The latest attribute {latest.Id} is not the final delta of checkout {baseCheckout.Id}.");
            }

            for (var expectedIndex = 1; expectedIndex <= deltas.Count; expectedIndex++) {
                if (deltas[expectedIndex - 1].DeltaIndex != expectedIndex) {
                    throw new InvalidDataException(
                        $"Delta chain for checkout {baseCheckout.Id} is incomplete. " +
                        $"Expected DeltaIndex {expectedIndex}, got {deltas[expectedIndex - 1].DeltaIndex}.");
                }

                if (string.IsNullOrWhiteSpace(deltas[expectedIndex - 1].StoredFileName)) {
                    throw new InvalidDataException($"Delta attribute {deltas[expectedIndex - 1].Id} has no StoredFileName.");
                }
            }

            var basePath = Path.Combine(_settings.DataDir, baseCheckout.StoredFileName);
            if (!File.Exists(basePath))
                throw new FileNotFoundException($"Base checkout package was not found: '{basePath}'.", basePath);

            // La nueva versión consolidada debe tener un timestamp nuevo para evitar
            // sobrescribir por accidente un paquete existente cuyo nombre depende de él.
            var consolidatedBackupTime = DateTime.Now;

            var consolidatedPath = BackupDb.BackupFileName(
                _settings.DataDir,
                originalFullPath,
                consolidatedBackupTime);

            var consolidatedDirectory = Path.GetDirectoryName(consolidatedPath)
                ?? throw new InvalidOperationException(
                    $"Could not determine the destination directory for '{consolidatedPath}'.");

            Directory.CreateDirectory(consolidatedDirectory);

            // Si BackupFileName ya devuelve siempre .fhc, este cambio no altera nada.
            consolidatedPath = Path.ChangeExtension(consolidatedPath, ".fhc");

            Stream? currentPayload = null;
            string? newStoredFileName = null;
            var newAttributeInserted = false;

            try {
                // 1. Extraer el checkout de base.
                await using (var baseFhc = new FileStream(
                    basePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read)) {
                    var extracted = await FhcPackage.ExtractPackageAsync(
                        baseFhc,
                        token).ConfigureAwait(false);

                    currentPayload = extracted.Payload;
                }

                // 2. Aplicar todos los deltas en orden, incluido el último.
                using var deltaService = new DeltaService(
                    _loggerFactory.CreateLogger<DeltaService>(),
                    _settings.AllowOctodiffFallback);

                foreach (var delta in deltas) {
                    token.ThrowIfCancellationRequested();

                    var deltaPath = Path.Combine(_settings.DataDir, delta.StoredFileName!);
                    if (!File.Exists(deltaPath)) 
                        throw new FileNotFoundException($"Delta package was not found: '{deltaPath}'.", deltaPath);
                    
                    await using var deltaFhc = new FileStream(
                        deltaPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read);

                    var extractedDelta = await FhcPackage.ExtractPackageAsync(deltaFhc, token).ConfigureAwait(false);
                    using var deltaPayload = extractedDelta.Payload;
                    if (currentPayload.CanSeek) currentPayload.Seek(0, SeekOrigin.Begin);
                    if (deltaPayload.CanSeek) deltaPayload.Seek(0, SeekOrigin.Begin);

                    var nextPayload = await deltaService.ApplyDeltaAsync(currentPayload, deltaPayload, token).ConfigureAwait(false);
                    currentPayload.Dispose();
                    currentPayload = nextPayload;

                    if (currentPayload.CanSeek) currentPayload.Seek(0, SeekOrigin.Begin);
                }

                token.ThrowIfCancellationRequested();

                // 3. Crear un hash del contenido reconstruido. No reutilizamos el hash
                // almacenado ciegamente: así el checkout consolidado describe sus bytes.
                string sha256;
                if (currentPayload.CanSeek) currentPayload.Seek(0, SeekOrigin.Begin);
                using (var hash = SHA256.Create()) {
                    var hashBytes = await hash.ComputeHashAsync(currentPayload, token).ConfigureAwait(false);
                    sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                }

                if (currentPayload.CanSeek) currentPayload.Seek(0, SeekOrigin.Begin);

                // 4. Crear el paquete autocontenido.
                var metadata = new FhcMetadata {
                    Type = "checkout",
                    BaseBackupId = null,
                    Sha256 = sha256,
                    OriginalFileName = Path.GetFileName(originalFullPath),
                    OriginalSize = latest.Size,
                    DeltaIndex = 0,
                    CreatedAt = DateTimeOffset.UtcNow,
                    FormatVersion = 1,
                };

                await using (var output = new FileStream(
                    consolidatedPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true)) {
                    await FhcPackage.CreatePackageAsync(
                        currentPayload,
                        metadata,
                        output,
                        token).ConfigureAwait(false);
                }

                // El paquete está cerrado. Conserva las fechas del último punto de
                // restauración que representa este checkout consolidado.
                File.SetCreationTime(consolidatedPath, latest.CreationTime);
                File.SetLastWriteTime(consolidatedPath, latest.LastWriteTime);
                File.SetLastAccessTime(consolidatedPath, latest.LastAccessTime);

                // 5. Registrar el nuevo checkout antes de borrar nada de la cadena vieja.
                newStoredFileName = Path.GetRelativePath(
                    _settings.DataDir,
                    consolidatedPath);

                _db.InsertAttributeExtended(
                    file.Id,
                    consolidatedBackupTime,
                    latest.CreationTime,
                    latest.LastWriteTime,
                    latest.LastAccessTime,
                    latest.Size,
                    metadata.Type,
                    newStoredFileName,
                    metadata.Sha256,
                    null,
                    metadata.DeltaIndex,
                    metadata.CreatedAt);

                newAttributeInserted = true;

                // 6. Solo ahora se eliminan todos los atributos/paquetes antiguos.
                foreach (var attr in attributes) {
                    token.ThrowIfCancellationRequested();
                    await DeleteBackupAttributeAsync(attr, token).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    $"Consolidated {deltas.Count} delta(s) for '{originalFullPath}' " +
                    $"into checkout '{consolidatedPath}'.");
            } catch {
                // La cadena anterior todavía existe, porque solo empezamos a borrarla
                // tras un empaquetado e inserción correctos. Limpiamos únicamente el
                // paquete/fila nuevos, si llegaron a crearse.
                if (newAttributeInserted) {
                    try {
                        var newAttribute = _db.GetAttributes(file.Id)
                            .FirstOrDefault(a =>
                                string.Equals(
                                    a.StoredFileName,
                                    newStoredFileName,
                                    StringComparison.OrdinalIgnoreCase));

                        if (newAttribute is not null) _db.DeleteAttribute(newAttribute.Id);
                    } catch (Exception cleanupEx) {
                        _logger.LogWarning(
                            cleanupEx,
                            $"Could not remove the new DB attribute for '{consolidatedPath}'.");
                    }
                }

                try {
                    if (File.Exists(consolidatedPath)) File.Delete(consolidatedPath);
                } catch (Exception cleanupEx) {
                    _logger.LogWarning(cleanupEx, $"Could not remove incomplete consolidated checkout '{consolidatedPath}'.");
                }

                throw;
            } finally {
                currentPayload?.Dispose();
            }
        }

        /// <summary>
        /// Trabajador de limpieza de archivos
        /// (Conserva solo los más recientes.)
        /// </summary>
        async Task CleanupWorker_PreserveAllLatest() {
            try {
                _logger.LogTrace("Enter: CleanupWorker_PreserveAllLatest()");
                Interlocked.Increment(ref _crawler.CrawlingSuspended);
                _filesToScan = await _db.FileCount(_cts.Token);

                foreach (var file in _db.FindAllFiles()) {
                    _cts.Token.ThrowIfCancellationRequested();
                    _scanCount++;
                    var backupFileDir = _db.GetFileDir(file.Id);
                    var originalFullPath = Path.Combine(backupFileDir, file.Name);

                    try {
                        await PreserveOnlyLatestRecoverableVersionAsync(file, originalFullPath, _cts.Token);
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        // Un fallo al consolidar este fichero no debe impedir que
                        // se procesen los demás.
                        _logger.LogError(
                            $"Exception caught while preserving the latest backup " +
                            $"for \"{originalFullPath}\": {ex}");
                    }
                }
            } catch (OperationCanceledException) {
                _logger.LogInformation("OperationCanceledException in CleanupWorker_PreserveAllLatest()");
            } catch (Exception ex) {_logger.LogError($"Exception caught in CleanupWorker_PreserveAllLatest(): {ex}");
            } finally {
                // Cleanup terminado.
                _cleanupWorker = null;
                await TaskCancelAndCleanupGui();
                Interlocked.Decrement(ref _crawler.CrawlingSuspended);
                _logger.LogTrace("Leave: CleanupWorker_PreserveAllLatest()");
            }
        }

        private async Task DeleteAllBackupVersionsAsync(
            FileDbEntry file,
            CancellationToken token) {
            var attributes = _db.GetAttributes(file.Id).ToList();

            foreach (var attribute in attributes) {
                token.ThrowIfCancellationRequested();

                await DeleteBackupAttributeAsync(attribute, token);
            }

            _db.DeleteFile(file.Id);
            _db.DeleteDirectoryIfEmpty(file.DirectoryId);
        }

        /// <summary>
        /// Trabajador de limpieza de archivos
        /// (Conserva únicamente la última versión de los archivos existentes)
        /// </summary>
        async Task CleanupWorker_PreserveExistingLatest() {
            try {
                _logger.LogTrace("Enter: CleanupWorker_PreserveExistingLatest()");
                Interlocked.Increment(ref _crawler.CrawlingSuspended);

                _filesToScan = await _db.FileCount(_cts.Token);

                foreach (var file in _db.FindAllFiles()) {
                    _cts.Token.ThrowIfCancellationRequested();
                    _scanCount++;

                    var backupFileDir = _db.GetFileDir(file.Id);
                    var filePath = Path.Combine(backupFileDir, file.Name);

                    var fileExists = File.Exists(filePath);
                    var isExcluded = _settings.IsExcluded(backupFileDir);

                    _logger.LogTrace(
                        $"Checking {filePath}, FileExists = {fileExists}, " +
                        $"IsExcluded = {isExcluded}");

                    if (fileExists && !isExcluded) {
                        // El fichero original sigue existiendo y no está excluido:
                        // preservar únicamente una versión restaurable.
                        try {
                            await PreserveOnlyLatestRecoverableVersionAsync(file, filePath, _cts.Token);
                        } catch (OperationCanceledException) {
                            throw;
                        } catch (Exception ex) {
                            // Un fallo de un fichero no debe impedir que se procesen
                            // los restantes.
                            _logger.LogError($"Exception caught while preserving the latest backup " + $"for \"{filePath}\": {ex}");
                        }
                    } else {
                        // El fichero ya no existe o está excluido:
                        // se elimina la cadena completa de backups.
                        try {
                            await DeleteAllBackupVersionsAsync(file, _cts.Token);
                        } catch (OperationCanceledException) {
                            throw;
                        } catch (Exception ex) {
                            _logger.LogError($"Exception caught while deleting all backups for " + $"\"{filePath}\": {ex}");
                        }
                    }
                }
            } catch (OperationCanceledException) {
                _logger.LogInformation("OperationCanceledException in " + "CleanupWorker_PreserveExistingLatest()");
            } catch (Exception ex) {
                _logger.LogError($"Exception caught in " + $"CleanupWorker_PreserveExistingLatest(): {ex}");
            } finally {
                // Limpieza completada
                _cleanupWorker = null;
                await TaskCancelAndCleanupGui();
                Interlocked.Decrement(ref _crawler.CrawlingSuspended);
                _logger.LogTrace("Leave: CleanupWorker_PreserveExistingLatest()");
            }
        }

        /// <summary>
        /// プログレスバー表示ワーカー
        /// </summary>
        async Task ProgressWorker()
        {
            try
            {
                _logger.LogTrace("Enter: ProgressWorker()");

                // GUI初期化
                SafeInvoke(() =>
                {
                    progressBar1.Value = 0;
                    ScanCountTextBox.Text = "";
                    DeleteCountTextBox.Text = "";
                });

                // CleanupWorkerで_filesToScanが設定されるのを待つ
                while (_filesToScan == -1)
                    await Task.Delay(100, _cts.Token);

                while (!_cts.IsCancellationRequested)
                {
                    SafeInvoke(() =>
                    {
                        if (_scanCount > 0 && _filesToScan > 0)
                        {
                            ScanCountTextBox.Text = $"{_scanCount.ToString("#,0")} / {_filesToScan.ToString("#,0")}";
                            progressBar1.Value = Math.Min(100, (_scanCount * 100) / _filesToScan);
                        }
                        DeleteCountTextBox.Text = _deleteCount.ToString("#,0");
                    });
                    await Task.Delay(1000, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation($"OperationCanceledException in ProgressWorker()");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception caught in ProgressWorker(): {ex}");
            }
            finally
            {
                _logger.LogTrace("Leave: ProgressWorker()");
            }
        }

        /// <summary>
        /// 閉じるボタン
        /// </summary>
        private void buttonClose_Click(object sender, EventArgs e)
        {
            try
            {
                _logger.LogTrace("Enter: buttonClose_Click()");
                Close();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception caught in buttonClose_Click(): {ex}");
            }
            finally
            {
                _logger.LogTrace("Leave: buttonClose_Click()");
            }
        }

        /// <summary>
        /// 削除/停止ボタン
        /// </summary>
        private async void buttonStartStop_Click(object sender, EventArgs e)
        {
            try
            {
                _logger.LogTrace("Enter: buttonStartStop_Click()");

                // 停止
                if (_running)
                {
                    await TaskCancelAndCleanupGui();
                    return;
                }

                // 削除開始
                _running = true;
                _filesToScan = -1;
                _deleteCount = 0;
                _cts = new CancellationTokenSource();
                comboBox.Enabled = false;
                buttonStartStop.Text = Strings.Get("Cleanup_ButtonStop");
                buttonClose.Enabled = false;

                // ワーカー起動
                // (Task.Runはasyncデリゲートをアンラップするため、
                //  ワーカー全体の完了を_progressWorker/_cleanupWorkerで待機できる)
                _progressWorker = Task.Run(() => ProgressWorker());
                if (comboBox.SelectedIndex == 0)
                    _cleanupWorker = Task.Run(() => CleanupWorker_PreserveAllLatest());
                else
                    _cleanupWorker = Task.Run(() => CleanupWorker_PreserveExistingLatest());
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception caught in buttonStartStop_Click(): {ex}");
            }
            finally
            {
                _logger.LogTrace("Leave: buttonStartStop_Click()");
            }
        }

        /// <summary>
        /// _progressWorkerと_cleanupWorkerをキャンセルしてGUIを元に戻す
        /// </summary>
        async Task TaskCancelAndCleanupGui()
        {
            try
            {
                _cts.Cancel();
                if (_progressWorker != null)
                {
                    await _progressWorker.ConfigureAwait(false);
                    _progressWorker = null;
                }
                if (_cleanupWorker != null)
                {
                    await _cleanupWorker.ConfigureAwait(false);
                    _cleanupWorker = null;
                }
            }
            catch { }

            var action = new Action(() =>
            {
                comboBox.Enabled = true;
                buttonStartStop.Text = Strings.Get("Cleanup_ButtonStart");
                buttonClose.Enabled = true;
                ScanCountTextBox.Text = "";
                DeleteCountTextBox.Text = "";
                progressBar1.Value = 0;
            });
            if (InvokeRequired)
                SafeInvoke(action);
            else
                action();
            _running = false;
        }
    }
}
