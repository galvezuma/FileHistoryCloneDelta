using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;

namespace FileHistory
{
    /// <summary>
    /// 保持ポリシー（最大世代数・保持日数）に従って古いバックアップを
    /// 自動削除するバックグラウンドワーカー
    /// </summary>
    public class RetentionWorker : IDisposable
    {
        // DI
        readonly Settings _settings;
        readonly IBackupDb _db;
        readonly ILogger _logger;

        readonly CancellationTokenSource _cts;
        readonly Thread _thread;


        public RetentionWorker(Settings settings, IBackupDb db, ILoggerFactory loggerFactory)
        {
            _settings = settings;
            _db = db;
            _logger = loggerFactory.CreateLogger<RetentionWorker>();
            _cts = new CancellationTokenSource();

            if (_settings.MaxGenerations <= 0 && _settings.RetentionDays <= 0)
            {
                _logger.LogInformation("Retention policy disabled (MaxGenerations/RetentionDays not set)");
                return;
            }

            _thread = new Thread(() => Run(_cts.Token))
            {
                Priority = ThreadPriority.Lowest,
                IsBackground = true,
            };
            _thread.Start();
        }

        void Run(CancellationToken token)
        {
            // 起動直後の負荷を避けるための初回待機
            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(0, _settings.RetentionStartupDelay)))) return;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ApplyRetentionPolicy(token);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Exception caught in RetentionWorker: {ex}", ex.ToString());
                }
                if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(60, _settings.RetentionScanInterval)))) return;
            }
        }

        /// <summary>
        /// 保持ポリシーを一回適用する
        /// 各ファイルの最新世代は常に保持し、それ以外について
        /// MaxGenerations超過分およびRetentionDaysより古い世代を削除する
        /// </summary>
        public void ApplyRetentionPolicy(CancellationToken token)
        {
            if (_settings.MaxGenerations <= 0 && _settings.RetentionDays <= 0) return;

            _logger.LogInformation("Retention scan start (MaxGenerations = {max}, RetentionDays = {days})",
                _settings.MaxGenerations, _settings.RetentionDays);
            var deleted = 0;

            foreach (var file in _db.FindAllFiles())
            {
                if (token.IsCancellationRequested) break;
                deleted += PruneFileGenerations(_settings, _db, _logger, file, token);
            }
            _logger.LogInformation("Retention scan finished, {count} backups deleted", deleted);
        }

        /// <summary>
        /// 1ファイル分の保持ポリシー適用。最新世代は常に保持し、
        /// MaxGenerations超過分およびRetentionDaysより古い世代を削除する。
        /// バックアップ保存直後(BackupScheduler)と定期スキャンの両方から呼ばれる。
        /// </summary>
        /// <returns>削除した世代数</returns>
        public static int PruneFileGenerations(Settings settings, IBackupDb db, ILogger logger, FileDbEntry file, CancellationToken token)
        {
            var deleted = 0;
            var attrs = db.GetAttributes(file.Id).OrderByDescending(m => m.BackupTime).ToList();
            if (attrs.Count <= 1) return deleted;
            var ageLimit = settings.RetentionDays > 0 ? DateTime.Now.AddDays(-settings.RetentionDays) : DateTime.MinValue;

            // First pass: mark candidates for deletion based on age and count (latest kept)
            var canDelete = new bool[attrs.Count];
            for (int i = 0; i < attrs.Count; i++) canDelete[i] = false;

            for (int i = 1; i < attrs.Count; i++)
            {
                if (token.IsCancellationRequested) break;
                var expiredByAge = settings.RetentionDays > 0 && attrs[i].BackupTime < ageLimit;
                var expiredByCount = settings.MaxGenerations > 0 && i >= settings.MaxGenerations;
                if (expiredByAge || expiredByCount) canDelete[i] = true;
            }

            // Second pass: ensure we don't delete a checkout while keeping dependent deltas
            // For each checkout marked deletable, verify all dependent deltas are also marked deletable; otherwise skip deleting the checkout
            for (int i = 0; i < attrs.Count; i++)
            {
                if (!canDelete[i]) continue;
                var attr = attrs[i];
                if (attr.Type == "checkout")
                {
                    // find dependent deltas
                    var deps = attrs.Where((a, idx) => a.BaseAttributeId == attr.Id && a.Type == "delta").ToList();
                    if (deps.Count > 0)
                    {
                        // if any dependent delta is NOT deletable, we must not delete this checkout
                        var anyNonDeletable = deps.Any(d => {
                            var idx = attrs.FindIndex(x => x.Id == d.Id);
                            return idx >= 0 && !canDelete[idx];
                        });
                        if (anyNonDeletable)
                        {
                            // skip deleting this checkout
                            canDelete[i] = false;
                        }
                    }
                }
            }

            // Final pass: perform deletion atomically using move-to-trash then DB removal with rollback on failure
            var toDelete = new List<(AttributeDbEntry Attr, string OriginalPath)>();
            for (int i = 0; i < attrs.Count; i++)
            {
                if (!canDelete[i]) continue;
                var a = attrs[i];
                string originalPath = null;
                if (!string.IsNullOrEmpty(a.StoredFileName))
                {
                    originalPath = Path.Combine(settings.DataDir, a.StoredFileName);
                }
                else
                {
                    try
                    {
                        var backupFileDir = db.GetFileDir(file.Id);
                        originalPath = BackupDb.BackupFileName(settings.DataDir, Path.Combine(backupFileDir, file.Name), a.BackupTime);
                    }
                    catch
                    {
                        originalPath = null;
                    }
                }
                toDelete.Add((a, originalPath));
            }

            if (toDelete.Count == 0) return deleted;

            var movedFiles = new List<(string From, string To)>();
            var trashRoot = Path.Combine(settings.DataDir, ".trash");
            Directory.CreateDirectory(trashRoot);
            var trashDir = Path.Combine(trashRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(trashDir);

            try
            {
                // Move files to trash (with cross-volume fallback: copy+delete)
                foreach (var item in toDelete)
                {
                    if (token.IsCancellationRequested) break;
                    var path = item.OriginalPath;
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    var dest = Path.Combine(trashDir, Path.GetFileName(path));
                    logger.LogInformation("Retention move to trash {from} -> {to}", path, dest);
                    try
                    {
                        File.Move(path, dest);
                    }
                    catch (IOException)
                    {
                        // Possibly cross-volume; fallback to copy+delete
                        File.Copy(path, dest, overwrite: true);
                        File.Delete(path);
                    }
                    movedFiles.Add((path, dest));
                }

                // Delete attributes from DB, keep backup of attributes to restore on failure
                var deletedAttrs = new List<AttributeDbEntry>();
                foreach (var item in toDelete)
                {
                    if (token.IsCancellationRequested) break;
                    var ok = db.DeleteAttribute(item.Attr.Id);
                    if (!ok)
                    {
                        // rollback: restore moved files
                        foreach (var m in movedFiles)
                        {
                            try { if (File.Exists(m.To)) File.Move(m.To, m.From); } catch { }
                        }
                        return deleted; // nothing committed
                    }
                    else
                    {
                        deletedAttrs.Add(item.Attr);
                        deleted++;
                    }
                }

                // All deletions succeeded: remove trash directory
                try
                {
                    foreach (var m in movedFiles)
                    {
                        if (File.Exists(m.To)) File.Delete(m.To);
                    }
                    // attempt to remove empty trash dir
                    Directory.Delete(trashDir, false);
                }
                catch { }

                return deleted;
            }
            catch (Exception ex)
            {
                // On any exception, attempt to rollback moved files
                foreach (var m in movedFiles)
                {
                    try { if (File.Exists(m.To)) File.Move(m.To, m.From); } catch { }
                }
                logger.LogDebug("Exception caught in retention deletion: {ex}", ex.ToString());
                return deleted;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _thread?.Join();
        }
    }
}
