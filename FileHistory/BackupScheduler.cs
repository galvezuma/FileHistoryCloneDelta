using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FileHistory
{
    public interface IBackupScheduler
    {
        void Add(string path, AttributeDbEntry dbAttr, AttributeFileEntry fileAttr, SchedulePriority priority);
        void Dispose();
    }

    public class BackupScheduler : IBackupScheduler, IDisposable
    {
        // DI
        Settings _settings { get; set; }
        ILogger _logger { get; set; }
        readonly ILoggerFactory _loggerFactory;
        IBackupDb _db { get; set; }

        // Task Schedule
        SortedList<DateTime, List<ScheduleItem>> _highPrioritySchedules { get; set; }
        object _highPrioritySchedulesLock { get; set; }
        ManualResetEvent _highPrioritySchedulesAddEvent { get; set; }

        List<KeyValuePair<DateTime, ScheduleItem>> _lowPrioritySchedules { get; set; }
        SemaphoreSlim _lowPrioritySchedulesSemaphore { get; set; }
        object _lowPrioritySchedulesLock { get; set; }
        readonly int MAX_LOW_SCHEDULE;

        // Copy Worker
        Dictionary<string, Task> _copyWorkers;
        ManualResetEvent _copyWorkersExitEvent { get; set; }
        readonly int MAX_COPY_WORKER;

        // Worker管理
        CancellationTokenSource _cts { get; set; }
        Thread _thread { get; set; }

        /// <summary>
        /// コンストラクタ
        /// </summary>
        /// <param name="conf"></param>
        /// <param name="logger"></param>
        /// <param name="db"></param>
        public BackupScheduler(Settings settings, ILoggerFactory loggerFactory, IBackupDb db)
        {
            _settings = settings;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<BackupScheduler>();
            _db = db;

            MAX_LOW_SCHEDULE = Math.Max(1, settings.MaxLowPrioritySchedules);
            MAX_COPY_WORKER = Math.Max(1, settings.MaxCopyWorkers);

            _highPrioritySchedules = new SortedList<DateTime, List<ScheduleItem>>();
            _highPrioritySchedulesLock = new object();
            _highPrioritySchedulesAddEvent = new ManualResetEvent(false);
            _lowPrioritySchedules = new List<KeyValuePair<DateTime, ScheduleItem>>(MAX_LOW_SCHEDULE);
            _lowPrioritySchedulesSemaphore = new SemaphoreSlim(MAX_LOW_SCHEDULE, MAX_LOW_SCHEDULE);
            _lowPrioritySchedulesLock = new object();

            _copyWorkersExitEvent = new ManualResetEvent(false);

            _cts = new CancellationTokenSource();

            _thread = new Thread(new ThreadStart(() => CopyWorkerController(_cts.Token))) { Priority = ThreadPriority.Lowest, };
            _thread.Start();
        }

        /// <summary>
        /// バックアップスケジュール登録
        /// </summary>
        /// <param name="path"></param>
        /// <param name="attr"></param>
        /// <param name="priority"></param>
        public void Add(string path, AttributeDbEntry dbAttr, AttributeFileEntry fileAttr, SchedulePriority priority)
        {
            if (priority == SchedulePriority.High)
            {
                // Highプライオリティの場合、スケジュール投入してすぐにリターン
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        if (_settings.IsExcluded(path)) return;

                        if (fileAttr == null)
                            fileAttr = new AttributeFileEntry(path);
                        if (!fileAttr.IsValid)
                            return;

                        var backupAt = fileAttr.LastUpdate + TimeSpan.FromSeconds(_settings.BackupInterval(path));
                        var scheduleItem = new ScheduleItem
                        {
                            FileDbEntry = new FileDbEntry { Name = path },
                            AttributeDbEntry = new AttributeDbEntry(fileAttr),
                        };

                        _logger.LogDebug($"Schedule(High) backup at {backupAt}: {path}");
                        lock (_highPrioritySchedulesLock)
                        {
                            if (_highPrioritySchedules.ContainsKey(backupAt))
                                _highPrioritySchedules[backupAt].Add(scheduleItem);
                            else
                                _highPrioritySchedules.Add(backupAt, new List<ScheduleItem> { scheduleItem });
                            _highPrioritySchedulesAddEvent.Set();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Exception caught in Add(High) for \"{path}\": {ex}");
                    }
                });
            }
            else
            {
                // Lowプライオリティの場合
                if (_settings.IsExcluded(path)) return;

                DateTime backupAt;
                if (dbAttr == null)
                    backupAt = fileAttr.LastUpdate + TimeSpan.FromSeconds(_settings.BackupInterval(path));
                else
                    backupAt = dbAttr.LastUpdate + TimeSpan.FromSeconds(_settings.BackupInterval(path));

                // MAX_LOW_SCHEDULEになるまでブロック
                try
                {
                    _lowPrioritySchedulesSemaphore.Wait(_cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                _logger.LogDebug($"Shcedule(Low) backup at {backupAt}: {path}");
                lock (_lowPrioritySchedulesLock)
                {
                    _lowPrioritySchedules.Add(new KeyValuePair<DateTime, ScheduleItem>(
                         backupAt,
                         new ScheduleItem
                         {
                             FileDbEntry = new FileDbEntry { Name = path },
                             AttributeDbEntry = new AttributeDbEntry(fileAttr),
                         }));
                }
            }
        }

        void CopyWorkerController(CancellationToken token)
        {
            _copyWorkers = new Dictionary<string, Task>();
            while (true)
            {
                try
                {

                    if (token.IsCancellationRequested) break;

                    // Highプライオリティタスクがあれば全て起動
                    var snapshot = new List<KeyValuePair<DateTime, List<ScheduleItem>>>();
                    lock (_highPrioritySchedulesLock)
                        foreach (var item in _highPrioritySchedules)
                        {
                            if (DateTime.Now < item.Key) break;
                            snapshot.Add(item);
                        }
                    if (snapshot.Count > 0)
                    {

                        foreach (var items in snapshot)
                        {
                            if (token.IsCancellationRequested) break;

                            foreach (var item in items.Value.ToArray())
                            {
                                if (token.IsCancellationRequested) break;

                                // 既にコピー中であれば何もしない
                                if (_copyWorkers.ContainsKey(item.FileDbEntry.Name))
                                {
                                    _logger.LogDebug($"{item.FileDbEntry.Name} is under copying, skip.");
                                    lock (_highPrioritySchedulesLock)
                                    {
                                        items.Value.Remove(item);
                                        if (items.Value.Count == 0) _highPrioritySchedules.Remove(items.Key);
                                    }
                                    continue;
                                }

                                // 現時点でのディスク上の属性
                                var fileAttr = new AttributeFileEntry(item.FileDbEntry.Name);

                                // 現時点での最新のDBバックアップ情報
                                var dbFile = _db.GetFile(item.FileDbEntry.Name);
                                var dbAttr = dbFile == null ? null : _db.GetLatestAttribute(dbFile.Id);

                                // 既にバックアップされていれば何もしない
                                if (dbAttr != null && fileAttr.LastUpdate == dbAttr.LastUpdate)
                                {
                                    _logger.LogDebug($"{item.FileDbEntry.Name} already copied, skip.");
                                    lock (_highPrioritySchedulesLock)
                                    {
                                        items.Value.Remove(item);
                                        if (items.Value.Count == 0) _highPrioritySchedules.Remove(items.Key);
                                    }
                                    continue;
                                }

                                if (item.AttributeDbEntry.LastUpdate == fileAttr.LastUpdate)
                                {
                                    // ファイルが更新されていなければコピー
                                    _copyWorkers.Add(item.FileDbEntry.Name, Task.Factory.StartNew(() => CopyTask(item.FileDbEntry.Name, fileAttr, dbFile, token)));
                                    lock (_highPrioritySchedulesLock)
                                    {
                                        items.Value.Remove(item);
                                        if (items.Value.Count == 0) _highPrioritySchedules.Remove(items.Key);
                                    }
                                }
                                else
                                {
                                    // ファイルが更新されていた場合、スケジューラに再登録
                                    var backupAt = fileAttr.LastUpdate + TimeSpan.FromSeconds(_settings.BackupInterval(item.FileDbEntry.Name));
                                    var scheduleItem = new ScheduleItem
                                    {
                                        FileDbEntry = new FileDbEntry { Name = item.FileDbEntry.Name },
                                        AttributeDbEntry = new AttributeDbEntry(fileAttr),
                                    };

                                    _logger.LogDebug($"{item.FileDbEntry.Name} modified, reschedule at {backupAt}");
                                    lock (_highPrioritySchedulesLock)
                                    {
                                        items.Value.Remove(item);
                                        if (items.Value.Count == 0) _highPrioritySchedules.Remove(items.Key);

                                        if (_highPrioritySchedules.ContainsKey(backupAt))
                                            _highPrioritySchedules[backupAt].Add(scheduleItem);
                                        else
                                            _highPrioritySchedules.Add(backupAt, new List<ScheduleItem> { scheduleItem });
                                        _highPrioritySchedulesAddEvent.Set();
                                    }
                                }
                            }
                        }
                    }

                    // Workerクリーンアップ
                    foreach (var item in _copyWorkers.ToArray())
                    {
                        if (item.Value.Status == TaskStatus.Canceled ||
                        item.Value.Status == TaskStatus.Faulted ||
                        item.Value.Status == TaskStatus.RanToCompletion)
                            _copyWorkers.Remove(item.Key);
                    }

                    // Lowプライオリティタスクは、タスク数がMAX_LOW_SCHEDULE以下の場合のみ起動
                    lock (_lowPrioritySchedulesLock)
                    {
                        if (_lowPrioritySchedules.Count > 0 && _copyWorkers.Count < MAX_COPY_WORKER)
                        {
                            var tasks = _lowPrioritySchedules.Where(m => m.Key <= DateTime.Now);
                            if (tasks.Any())
                            {
                                // 起動するタスクあり
                                var task = tasks.OrderBy(m => m.Key).First();

                                // 既にコピー中であれば何もしない
                                if (_copyWorkers.ContainsKey(task.Value.FileDbEntry.Name))
                                {
                                    _logger.LogDebug($"{task.Value.FileDbEntry.Name} is under copying, skip.");
                                    _lowPrioritySchedules.Remove(task);
                                    _lowPrioritySchedulesSemaphore.Release();
                                }
                                else
                                {
                                    // 現時点でのディスク上の属性
                                    var fileAttr = new AttributeFileEntry(task.Value.FileDbEntry.Name);

                                    // 現時点での最新のDBバックアップ情報
                                    var dbFile = _db.GetFile(task.Value.FileDbEntry.Name);
                                    var dbAttr = dbFile == null ? null : _db.GetLatestAttribute(dbFile.Id);

                                    if (dbAttr != null && fileAttr.LastUpdate == dbAttr.LastUpdate)
                                    {
                                        // 既にバックアップされていれば何もしない
                                        _logger.LogDebug($"{task.Value.FileDbEntry.Name} already copied, skip.");
                                        _lowPrioritySchedules.Remove(task);
                                        _lowPrioritySchedulesSemaphore.Release();
                                    }
                                    else if (task.Value.AttributeDbEntry.LastUpdate == fileAttr.LastUpdate)
                                    {
                                        // ファイルが更新されていなければコピー
                                        _copyWorkers.Add(task.Value.FileDbEntry.Name, Task.Factory.StartNew(() => CopyTask(task.Value.FileDbEntry.Name, fileAttr, dbFile, token)));
                                        _lowPrioritySchedules.Remove(task);
                                        _lowPrioritySchedulesSemaphore.Release();
                                    }
                                    else
                                    {
                                        // ファイルが更新されていた場合、スケジューラに再登録
                                        var backupAt = fileAttr.LastUpdate + TimeSpan.FromSeconds(_settings.BackupInterval(task.Value.FileDbEntry.Name));
                                        var scheduleItem = new ScheduleItem
                                        {
                                            FileDbEntry = new FileDbEntry { Name = task.Value.FileDbEntry.Name },
                                            AttributeDbEntry = new AttributeDbEntry(fileAttr),
                                        };
                                        _logger.LogDebug($"{task.Value.FileDbEntry.Name} modified, reschedule at {backupAt}");
                                        _lowPrioritySchedules.Remove(task);
                                        _lowPrioritySchedules.Add(new KeyValuePair<DateTime, ScheduleItem>(backupAt, scheduleItem));
                                    }
                                }
                            }
                        }
                    }

                    // 待機
                    // 次にスケジュールされたアイテムの時刻・追加イベント・ワーカー終了イベントの
                    // いずれか早いものまで待つ（実行可能なアイテムがあれば待たずに抜ける）
                    while (true)
                    {
                        _highPrioritySchedulesAddEvent.Reset();
                        _copyWorkersExitEvent.Reset();

                        var now = DateTime.Now;
                        var timeout = 1000.0;

                        bool highReady;
                        lock (_highPrioritySchedulesLock)
                        {
                            highReady = _highPrioritySchedules.Count > 0 && _highPrioritySchedules.Keys[0] <= now;
                            if (!highReady && _highPrioritySchedules.Count > 0)
                                timeout = Math.Min(timeout, (_highPrioritySchedules.Keys[0] - now).TotalMilliseconds + 1);
                        }
                        if (highReady) break;

                        bool lowReady = false;
                        lock (_lowPrioritySchedulesLock)
                        {
                            if (_lowPrioritySchedules.Count > 0)
                            {
                                var nextLow = _lowPrioritySchedules.Min(m => m.Key);
                                if (nextLow <= now) lowReady = true;
                                else timeout = Math.Min(timeout, (nextLow - now).TotalMilliseconds + 1);
                            }
                        }
                        if (lowReady && _copyWorkers.Count(m => m.Value.Status == TaskStatus.Running) < MAX_COPY_WORKER)
                            break;

                        if (WaitHandle.WaitTimeout != WaitHandle.WaitAny(new WaitHandle[] { _highPrioritySchedulesAddEvent, _copyWorkersExitEvent }, (int)Math.Max(1, timeout)))
                            break;
                        if (token.IsCancellationRequested) break;
                    }
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                    {
                        _logger.LogInformation($"BackupScheduler Canceled");
                        return;
                    }
                    _logger.LogError($"Exception caught in CopyWorkerController: {ex}");
                    if (token.WaitHandle.WaitOne(60 * 1000)) return;
                }
            }
        }

        private static async Task<Stream> CreateCheckoutPayloadAsync(Stream source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);

            if (!source.CanRead)
                throw new ArgumentException(
                    "El stream de origen debe poder leerse.",
                    nameof(source));

            if (source.CanSeek)
                source.Position = 0;

            var payload = new MemoryStream();

            await source.CopyToAsync(
                payload,
                bufferSize: 1024 * 1024,
                cancellationToken).ConfigureAwait(false);

            payload.Position = 0;
            return payload;
        }

        void CopyTask(string file, AttributeFileEntry fileAttr, FileDbEntry dbFile, CancellationToken token)
        {
            var now = DateTime.Now;
            _logger.LogInformation($"Backup {file}");

            var backupFile = BackupDb.BackupFileName(_settings.DataDir, file, now);
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile));
            try
            {
                // Generar paquete .fhc usando DeltaService + FhcPackage
                var fhcPath = Path.ChangeExtension(backupFile, ".fhc");
                Directory.CreateDirectory(Path.GetDirectoryName(fhcPath));

                // Decide si crear checkout o delta
                if (dbFile == null) dbFile = _db.InsertFile(file);

                using (var deltaService = new DeltaService(_loggerFactory.CreateLogger<DeltaService>(), _settings.AllowOctodiffFallback))
                using (var infs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Calcular checksum del fichero nuevo (SHA-256)
                    var newFileSha = deltaService.ComputeSha256Async(infs, token).GetAwaiter().GetResult();
                    if (infs.CanSeek) infs.Seek(0, SeekOrigin.Begin);

                    // Buscar último checkout conocido para este fichero
                    var lastCheckout = _db.GetLatestCheckoutAttribute(dbFile.Id);

                    bool forceCheckout = false;
                    if (lastCheckout == null)
                        forceCheckout = true;
                    else
                    {
                        // Verificar que el archivo base existe en disco
                        var basePath = Path.Combine(_settings.DataDir, lastCheckout.StoredFileName ?? "");
                        if (string.IsNullOrEmpty(lastCheckout.StoredFileName) || !File.Exists(basePath))
                            forceCheckout = true;
                    }

                    // Si forzamos checkout o la política lo requiere -> crear checkout
                    if (forceCheckout)
                    {
                        Stream payload = null;
                        try
                        {
                            payload = CreateCheckoutPayloadAsync(infs, token).GetAwaiter().GetResult();
                            if (payload.CanSeek) payload.Seek(0, SeekOrigin.Begin);

                            var metadata = new FhcMetadata
                            {
                                Type = "checkout",
                                BaseBackupId = null,
                                Sha256 = newFileSha,
                                OriginalFileName = Path.GetFileName(file),
                                OriginalSize = fileAttr.Size,
                                DeltaIndex = 0,
                                CreatedAt = DateTimeOffset.UtcNow,
                                FormatVersion = 1,
                            };

                            using var outfs = new FileStream(fhcPath, FileMode.Create, FileAccess.Write, FileShare.None);
                            FhcPackage.CreatePackageAsync(payload, metadata, outfs, token).GetAwaiter().GetResult();

                            var storedRelative = Path.GetRelativePath(_settings.DataDir, fhcPath);
                            _db.InsertAttributeExtended(dbFile.Id, now, fileAttr.CreationTime, fileAttr.LastWriteTime, fileAttr.LastAccessTime, fileAttr.Size,
                                metadata.Type, storedRelative, metadata.Sha256, null, metadata.DeltaIndex, metadata.CreatedAt);
                        }
                        finally { payload?.Dispose(); }
                    }
                    else
                    {
                        // Intentar crear delta respecto al lastCheckout
                        var basePath = Path.Combine(_settings.DataDir, lastCheckout.StoredFileName);
                        Stream basePayload = null;
                        Stream deltaStream = null;
                        try
                        {
                            // Reconstruir el estado más reciente a partir del checkout + todos sus deltas aplicados
                            using (var baseFhc = new FileStream(basePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                var extracted = FhcPackage.ExtractPackageAsync(baseFhc, token).GetAwaiter().GetResult();
                                basePayload = extracted.Payload; // MemoryStream con contenido del checkout
                            }

                            // Aplicar en orden todos los deltas que referencian a este checkout
                            var allAttrs = _db.GetAttributes(dbFile.Id);
                            var deltasForBase = allAttrs.Where(a => a.BaseAttributeId == lastCheckout.Id && a.Type == "delta")
                                                       .OrderBy(a => a.DeltaIndex)
                                                       .ToList();

                            foreach (var dAttr in deltasForBase)
                            {
                                // Abrir paquete .fhc del delta y extraer payload
                                var dPath = Path.Combine(_settings.DataDir, dAttr.StoredFileName);
                                using var dFhc = new FileStream(dPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                var extractedDelta = FhcPackage.ExtractPackageAsync(dFhc, token).GetAwaiter().GetResult();
                                using var deltaPayload = extractedDelta.Payload; // MemoryStream

                                // Aplicar deltaPayload sobre la base actual
                                var newBase = deltaService.ApplyDeltaAsync(basePayload, deltaPayload, token).GetAwaiter().GetResult();
                                basePayload.Dispose();
                                basePayload = newBase; // MemoryStream resultante
                                if (basePayload.CanSeek) basePayload.Seek(0, SeekOrigin.Begin);
                            }

                            // Crear delta entre el estado reconstruido (basePayload) y el fichero actual (infs)
                            if (basePayload.CanSeek) basePayload.Seek(0, SeekOrigin.Begin);
                            if (infs.CanSeek) infs.Seek(0, SeekOrigin.Begin);
                            deltaStream = deltaService.CreateDeltaAsync(basePayload, infs, token).GetAwaiter().GetResult();
                            if (deltaStream.CanSeek) deltaStream.Seek(0, SeekOrigin.Begin);

                            var deltaSize = deltaStream.CanSeek ? deltaStream.Length : -1;
                            var thresholdBytes = (long)Math.Ceiling(_settings.DeltaSizeThresholdPercent / 100.0 * fileAttr.Size);

                            if (deltaSize < 0 || deltaSize > thresholdBytes || _db.GetDeltaCountForBase(lastCheckout.Id) >= _settings.MaxDeltasBeforeCheckout)
                            {
                                // Delta no eficiente -> crear checkout en su lugar
                                deltaStream.Dispose(); deltaStream = null;
                                basePayload.Dispose(); basePayload = null;
                                if (infs.CanSeek) infs.Seek(0, SeekOrigin.Begin);
                                var payload = CreateCheckoutPayloadAsync(infs, token).GetAwaiter().GetResult();
                                if (payload.CanSeek) payload.Seek(0, SeekOrigin.Begin);
                                var metadata = new FhcMetadata
                                {
                                    Type = "checkout",
                                    BaseBackupId = null,
                                    Sha256 = newFileSha,
                                    OriginalFileName = Path.GetFileName(file),
                                    OriginalSize = fileAttr.Size,
                                    DeltaIndex = 0,
                                    CreatedAt = DateTimeOffset.UtcNow,
                                    FormatVersion = 1,
                                };
                                using var outfs = new FileStream(fhcPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                FhcPackage.CreatePackageAsync(payload, metadata, outfs, token).GetAwaiter().GetResult();
                                var storedRelative = Path.GetRelativePath(_settings.DataDir, fhcPath);
                                _db.InsertAttributeExtended(dbFile.Id, now, fileAttr.CreationTime, fileAttr.LastWriteTime, fileAttr.LastAccessTime, fileAttr.Size,
                                    metadata.Type, storedRelative, metadata.Sha256, null, metadata.DeltaIndex, metadata.CreatedAt);
                            }
                            else
                            {
                                // Guardar delta como paquete .fhc
                                var deltaIndex = _db.GetDeltaCountForBase(lastCheckout.Id) + 1;
                                var metadata = new FhcMetadata
                                {
                                    Type = "delta",
                                    BaseBackupId = lastCheckout.Id.ToString(),
                                    Sha256 = newFileSha,
                                    OriginalFileName = Path.GetFileName(file),
                                    OriginalSize = fileAttr.Size,
                                    DeltaIndex = deltaIndex,
                                    CreatedAt = DateTimeOffset.UtcNow,
                                    FormatVersion = 1,
                                };

                                using var outfs = new FileStream(fhcPath, FileMode.Create, FileAccess.Write, FileShare.None);
                                FhcPackage.CreatePackageAsync(deltaStream, metadata, outfs, token).GetAwaiter().GetResult();

                                var storedRelative = Path.GetRelativePath(_settings.DataDir, fhcPath);
                                _db.InsertAttributeExtended(dbFile.Id, now, fileAttr.CreationTime, fileAttr.LastWriteTime, fileAttr.LastAccessTime, fileAttr.Size,
                                    metadata.Type, storedRelative, metadata.Sha256, lastCheckout.Id, metadata.DeltaIndex, metadata.CreatedAt);
                            }
                        }
                        finally
                        {
                            basePayload?.Dispose();
                            deltaStream?.Dispose();
                        }
                    }
                }

                // 保持ポリシーをこのファイルへ即時適用(失敗してもバックアップ自体は成功扱い)
                if (_settings.MaxGenerations > 0 || _settings.RetentionDays > 0)
                {
                    try
                    {
                        RetentionWorker.PruneFileGenerations(_settings, _db, _logger, dbFile, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Exception caught in retention pruning for \"{file}\": {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                // 例外が発生したらロールバック
                if (token.IsCancellationRequested)
                    _logger.LogInformation($"Copy task canceled \"{file}\" to \"{backupFile}\"");
                else if (ex is FileNotFoundException)
                    _logger.LogDebug($"File \"{file}\" not found, skip backup.");
                else
                    _logger.LogError($"Exception caught in copy \"{file}\" to \"{backupFile}\": {ex}");

                if (File.Exists(backupFile))
                    try
                    {
                        File.Delete(backupFile);
                    }
                    catch (Exception) { }
                if (dbFile != null)
                {
                    var dbAttribute = _db.GetAttribute(dbFile.Id, fileAttr.CreationTime, fileAttr.LastWriteTime, fileAttr.Size);
                    if (dbAttribute != null) _db.DeleteAttribute(dbAttribute.Id);
                }

                if (token.IsCancellationRequested) return;
            }
            finally
            {
                _copyWorkersExitEvent.Set();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                Task.WaitAll(_copyWorkers?.Values.ToArray() ?? Array.Empty<Task>());
            }
            catch (AggregateException) { }
            _thread.Join();
        }
    }

    public class ScheduleItem
    {
        public AttributeDbEntry AttributeDbEntry { get; set; }
        public FileDbEntry FileDbEntry { get; set; }
        public DirectoryDbEntry DirectoryDbEntry { get; set; }
    }

    public enum SchedulePriority
    {
        High,
        Low,
    }
}
