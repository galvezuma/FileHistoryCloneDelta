using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using FileHistory;

namespace FileHistoryTests
{
    [TestClass]
    public class RetentionRollbackTests
    {
        [TestMethod]
        public void RetentionWorker_RollbackOnDbDeleteFailure_RestoresMovedFiles()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_retention_rb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var settings = new FileHistory.Settings
                {
                    BackupBaseDir = tmp,
                    MaxDeltasBeforeCheckout = 10,
                    DeltaSizeThresholdPercent = 50.0,
                    MaxGenerations = 1,
                };
                var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));

                // Use a real DB but wrap it to simulate DeleteAttribute failure
                using var dbReal = new FileHistory.BackupDb(settings, loggerFactory);
                var db = new FaultyDeleteBackupDb(dbReal);

                // prepare file + checkout + one delta (eligible)
                var testFile = Path.Combine(Path.GetTempPath(), "fhc_retention_rb_file_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(testFile, "oldversion");
                Directory.CreateDirectory(settings.DataDir);
                var fileEntry = dbReal.InsertFile(testFile);

                var oldCheckoutTime = DateTime.Now.AddDays(-10);
                var oldCheckoutName = Guid.NewGuid().ToString("N") + ".fhc";
                var oldCheckoutPath = Path.Combine(settings.DataDir, oldCheckoutName);
                using (var ofs = new FileStream(oldCheckoutPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    FileHistory.FhcPackage.CreatePackageAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("old")), new FileHistory.FhcMetadata { Type = "checkout", CreatedAt = oldCheckoutTime }, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                dbReal.InsertAttributeExtended(fileEntry.Id, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, 11, "checkout", Path.GetRelativePath(settings.DataDir, oldCheckoutPath), "", null, 0, oldCheckoutTime);

                var checkout = dbReal.GetLatestCheckoutAttribute(fileEntry.Id);
                Assert.IsNotNull(checkout);

                var payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("delta1"));
                var metadata = new FileHistory.FhcMetadata { Type = "delta", BaseBackupId = checkout.Id.ToString(), DeltaIndex = 1, CreatedAt = oldCheckoutTime.AddMinutes(1) };
                var fhc1 = Path.Combine(settings.DataDir, Guid.NewGuid().ToString("N") + ".fhc");
                using (var ofs = new FileStream(fhc1, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    FileHistory.FhcPackage.CreatePackageAsync(payload, metadata, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                dbReal.InsertAttributeExtended(fileEntry.Id, metadata.CreatedAt.DateTime, metadata.CreatedAt.DateTime, metadata.CreatedAt.DateTime, metadata.CreatedAt.DateTime, metadata.OriginalSize, metadata.Type, Path.GetRelativePath(settings.DataDir, fhc1), metadata.Sha256, checkout.Id, metadata.DeltaIndex, metadata.CreatedAt);

                var before = dbReal.GetAttributes(fileEntry.Id).ToList();
                Assert.IsTrue(before.Count >= 2);

                // Now mark Faulty DB to fail on next DeleteAttribute
                db.FailNextDelete = true;

                // Run retention using wrapper DB; since DeleteAttribute will fail, moved files must be restored
                var deleted = FileHistory.RetentionWorker.PruneFileGenerations(settings, db, loggerFactory.CreateLogger("retention-rb"), fileEntry, CancellationToken.None);

                // Ensure files are restored (i.e., original files exist)
                var attrsAfter = dbReal.GetAttributes(fileEntry.Id).ToList();
                foreach (var a in attrsAfter)
                {
                    if (!string.IsNullOrEmpty(a.StoredFileName))
                    {
                        var p = Path.Combine(settings.DataDir, a.StoredFileName);
                        Assert.IsTrue(File.Exists(p), "File must exist after rollback: " + p);
                    }
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }

    // Wrapper DB to simulate failure on DeleteAttribute
    class FaultyDeleteBackupDb : IBackupDb
    {
        readonly IBackupDb _inner;
        public bool FailNextDelete { get; set; }
        public FaultyDeleteBackupDb(IBackupDb inner) { _inner = inner; }
        public void Dispose() => _inner.Dispose();
        public AttributeDbEntry GetLatestAttribute(int fileId) => _inner.GetLatestAttribute(fileId);
        public AttributeDbEntry GetLatestCheckoutAttribute(int fileId) => _inner.GetLatestCheckoutAttribute(fileId);
        public AttributeDbEntry GetAttributeById(int id) => _inner.GetAttributeById(id);
        public int GetDeltaCountForBase(int baseAttributeId) => _inner.GetDeltaCountForBase(baseAttributeId);
        public AttributeDbEntry GetAttribute(int fileId, DateTime creationTime, DateTime lastWriteTime, long size) => _inner.GetAttribute(fileId, creationTime, lastWriteTime, size);
        public FileDbEntry GetFile(string fullPath, DirectoryDbEntry entry = null) => _inner.GetFile(fullPath, entry);
        public void InsertAttribute(int fileId, DateTime backupTime, DateTime creationTime, DateTime lastWriteTime, DateTime lastAccessTime, long size) => _inner.InsertAttribute(fileId, backupTime, creationTime, lastWriteTime, lastAccessTime, size);
        public void InsertAttributeExtended(int fileId, DateTime backupTime, DateTime creationTime, DateTime lastWriteTime, DateTime lastAccessTime, long size, string type, string storedFileName, string checksum, int? baseAttributeId, int deltaIndex, DateTimeOffset createdAt) => _inner.InsertAttributeExtended(fileId, backupTime, creationTime, lastWriteTime, lastAccessTime, size, type, storedFileName, checksum, baseAttributeId, deltaIndex, createdAt);
        public FileDbEntry InsertFile(string fullPath) => _inner.InsertFile(fullPath);
        public Task<int> FileCount(CancellationToken token) => _inner.FileCount(token);
        public Task<int> AttributeCount(CancellationToken token) => _inner.AttributeCount(token);
        public List<DirectoryDbEntry> GetChildDirectories(int parentId) => _inner.GetChildDirectories(parentId);
        public List<FileDbEntry> GetChildFiles(int directoryId) => _inner.GetChildFiles(directoryId);
        public List<AttributeDbEntry> GetAttributes(int fileId) => _inner.GetAttributes(fileId);
        public DirectoryDbEntry GetDirectryFromFilePath(string fullPath) => _inner.GetDirectryFromFilePath(fullPath);
        public DirectoryDbEntry GetDirectryFromDirPath(string dirPath) => _inner.GetDirectryFromDirPath(dirPath);
        public string GetFileDir(int fileId) => _inner.GetFileDir(fileId);
        public string GetDirPath(int directoryId) => _inner.GetDirPath(directoryId);
        public IEnumerable<FileDbEntry> FindAllFiles() => _inner.FindAllFiles();
        public bool DeleteFile(int fileId) => _inner.DeleteFile(fileId);
        public void DeleteDirectoryIfEmpty(int directoryId) => _inner.DeleteDirectoryIfEmpty(directoryId);

        public bool DeleteAttribute(int attributeId)
        {
            if (FailNextDelete)
            {
                FailNextDelete = false;
                return false;
            }
            return _inner.DeleteAttribute(attributeId);
        }
    }
}
