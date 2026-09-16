using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace FileHistoryTests
{
    [TestClass]
    public class RetentionIntegrationTests
    {
        [TestMethod]
        public void RetentionWorker_DeletesCheckoutAndDependentDeltas_WhenAllEligible()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_retention_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var settings = new FileHistory.Settings
                {
                    BackupBaseDir = tmp,
                    MaxDeltasBeforeCheckout = 10,
                    DeltaSizeThresholdPercent = 50.0,
                    // Keep only 1 generation -> older ones should be deletable
                    MaxGenerations = 1,
                };

                var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
                using var db = new FileHistory.BackupDb(settings, loggerFactory);
                var scheduler = new FileHistory.BackupScheduler(settings, loggerFactory, db);

                var testFile = Path.Combine(Path.GetTempPath(), "fhc_retention_file_" + Guid.NewGuid().ToString("N") + ".txt");

                // create initial file DB entry and an OLD checkout + deltas (these should be eligible for deletion)
                File.WriteAllText(testFile, "oldversion");
                Directory.CreateDirectory(settings.DataDir);
                var fileEntry = db.InsertFile(testFile);

                var oldCheckoutTime = DateTime.Now.AddDays(-10);
                // create a fake checkout .fhc file
                var oldCheckoutMetadata = new FileHistory.FhcMetadata
                {
                    Type = "checkout",
                    BaseBackupId = null,
                    DeltaIndex = 0,
                    Sha256 = "",
                    OriginalFileName = Path.GetFileName(testFile),
                    OriginalSize = 11,
                    CreatedAt = oldCheckoutTime,
                    FormatVersion = 1,
                };
                var oldCheckoutName = Guid.NewGuid().ToString("N") + ".fhc";
                var oldCheckoutPath = Path.Combine(settings.DataDir, oldCheckoutName);
                using (var ofs = new FileStream(oldCheckoutPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    FileHistory.FhcPackage.CreatePackageAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("old")), oldCheckoutMetadata, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                var oldRel = Path.GetRelativePath(settings.DataDir, oldCheckoutPath);
                db.InsertAttributeExtended(fileEntry.Id, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutMetadata.OriginalSize,
                    "checkout", oldRel, oldCheckoutMetadata.Sha256, null, 0, oldCheckoutMetadata.CreatedAt);

                var checkout = db.GetLatestCheckoutAttribute(fileEntry.Id);
                Assert.IsNotNull(checkout, "Checkout attribute must exist");

                // create two delta .fhc files referencing the old checkout
                for (int idx = 1; idx <= 2; idx++)
                {
                    var payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"delta{idx}"));
                    var metadata = new FileHistory.FhcMetadata
                    {
                        Type = "delta",
                        BaseBackupId = checkout.Id.ToString(),
                        DeltaIndex = idx,
                        Sha256 = "",
                        OriginalFileName = Path.GetFileName(testFile),
                        OriginalSize = payload.Length,
                        CreatedAt = oldCheckoutTime.AddMinutes(idx),
                        FormatVersion = 1,
                    };
                    var fhcName = Guid.NewGuid().ToString("N") + ".fhc";
                    var fhcPath = Path.Combine(settings.DataDir, fhcName);
                    using (var ofs = new FileStream(fhcPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        FileHistory.FhcPackage.CreatePackageAsync(payload, metadata, ofs, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    var rel = Path.GetRelativePath(settings.DataDir, fhcPath);
                    db.InsertAttributeExtended(fileEntry.Id, metadata.CreatedAt.DateTime, metadata.CreatedAt.DateTime, metadata.CreatedAt.DateTime, metadata.CreatedAt.DateTime, metadata.OriginalSize,
                        metadata.Type, rel, metadata.Sha256, checkout.Id, metadata.DeltaIndex, metadata.CreatedAt);
                }

                var beforeAttrs = db.GetAttributes(fileEntry.Id).ToList();
                Assert.IsTrue(beforeAttrs.Count >= 3, "Expected at least 3 attributes (old checkout + 2 deltas)");

                // create a NEW checkout via scheduler to act as most recent generation (should be kept)
                File.WriteAllText(testFile, "newversion");
                var fileAttrNew = new FileHistory.AttributeFileEntry(testFile);
                scheduler.Add(testFile, null, fileAttrNew, FileHistory.SchedulePriority.High);

                // wait for the scheduler to create the new checkout attribute
                var stop = DateTime.Now.AddSeconds(10);
                while (DateTime.Now < stop)
                {
                    var attrsNow = db.GetAttributes(fileEntry.Id);
                    if (attrsNow.Count >= 4) break;
                    Thread.Sleep(200);
                }
                beforeAttrs = db.GetAttributes(fileEntry.Id).ToList();

                // Run retention prune: with MaxGenerations=1, should delete all but latest
                var deleted = FileHistory.RetentionWorker.PruneFileGenerations(settings, db, loggerFactory.CreateLogger("retention-test"), fileEntry, CancellationToken.None);

                // After pruning, only one attribute must remain (the latest)
                var after = db.GetAttributes(fileEntry.Id).ToList();
                Assert.AreEqual(1, after.Count, "Retention should leave only the latest generation");

                // Ensure stored files for deleted attributes are removed
                var storedFiles = beforeAttrs.Where(a => !string.IsNullOrEmpty(a.StoredFileName)).Select(a => Path.Combine(settings.DataDir, a.StoredFileName)).ToList();
                var remaining = after.Select(a => !string.IsNullOrEmpty(a.StoredFileName) ? Path.Combine(settings.DataDir, a.StoredFileName) : null).Where(p => p != null).ToList();
                foreach (var f in storedFiles)
                {
                    if (!remaining.Contains(f))
                        Assert.IsFalse(File.Exists(f), "Deleted attribute stored file should be removed: " + f);
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [TestMethod]
        public void RetentionWorker_DoesNotDeleteCheckoutWhenDependentDeltaNotEligible()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_retention_" + Guid.NewGuid().ToString("N"));
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
                using var db = new FileHistory.BackupDb(settings, loggerFactory);

                // create file entry and an old checkout
                var testFile = Path.Combine(Path.GetTempPath(), "fhc_retention_file_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(testFile, "oldversion");
                Directory.CreateDirectory(settings.DataDir);
                var fileEntry = db.InsertFile(testFile);

                var oldCheckoutTime = DateTime.Now.AddDays(-10);
                var oldCheckoutMetadata = new FileHistory.FhcMetadata
                {
                    Type = "checkout",
                    BaseBackupId = null,
                    DeltaIndex = 0,
                    Sha256 = "",
                    OriginalFileName = Path.GetFileName(testFile),
                    OriginalSize = 11,
                    CreatedAt = oldCheckoutTime,
                    FormatVersion = 1,
                };
                var oldCheckoutName = Guid.NewGuid().ToString("N") + ".fhc";
                var oldCheckoutPath = Path.Combine(settings.DataDir, oldCheckoutName);
                using (var ofs = new FileStream(oldCheckoutPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    FileHistory.FhcPackage.CreatePackageAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("old")), oldCheckoutMetadata, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                var oldRel = Path.GetRelativePath(settings.DataDir, oldCheckoutPath);
                db.InsertAttributeExtended(fileEntry.Id, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutMetadata.OriginalSize,
                    "checkout", oldRel, oldCheckoutMetadata.Sha256, null, 0, oldCheckoutMetadata.CreatedAt);

                var checkout = db.GetLatestCheckoutAttribute(fileEntry.Id);
                Assert.IsNotNull(checkout);

                // create one delta eligible for deletion
                var payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("delta1"));
                var deltaMeta1 = new FileHistory.FhcMetadata
                {
                    Type = "delta",
                    BaseBackupId = checkout.Id.ToString(),
                    DeltaIndex = 1,
                    Sha256 = "",
                    OriginalFileName = Path.GetFileName(testFile),
                    OriginalSize = payload.Length,
                    CreatedAt = oldCheckoutTime.AddMinutes(1),
                    FormatVersion = 1,
                };
                var fhc1 = Path.Combine(settings.DataDir, Guid.NewGuid().ToString("N") + ".fhc");
                using (var ofs = new FileStream(fhc1, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    FileHistory.FhcPackage.CreatePackageAsync(payload, deltaMeta1, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                db.InsertAttributeExtended(fileEntry.Id, deltaMeta1.CreatedAt.DateTime, deltaMeta1.CreatedAt.DateTime, deltaMeta1.CreatedAt.DateTime, deltaMeta1.CreatedAt.DateTime, deltaMeta1.OriginalSize,
                    deltaMeta1.Type, Path.GetRelativePath(settings.DataDir, fhc1), deltaMeta1.Sha256, checkout.Id, deltaMeta1.DeltaIndex, deltaMeta1.CreatedAt);

                // create one delta NOT eligible for deletion (future date)
                var payload2 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("delta2"));
                var deltaMeta2 = new FileHistory.FhcMetadata
                {
                    Type = "delta",
                    BaseBackupId = checkout.Id.ToString(),
                    DeltaIndex = 2,
                    Sha256 = "",
                    OriginalFileName = Path.GetFileName(testFile),
                    OriginalSize = payload2.Length,
                    CreatedAt = DateTime.Now.AddDays(1), // in the future => not eligible by age
                    FormatVersion = 1,
                };
                var fhc2 = Path.Combine(settings.DataDir, Guid.NewGuid().ToString("N") + ".fhc");
                using (var ofs = new FileStream(fhc2, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    FileHistory.FhcPackage.CreatePackageAsync(payload2, deltaMeta2, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                db.InsertAttributeExtended(fileEntry.Id, deltaMeta2.CreatedAt.DateTime, deltaMeta2.CreatedAt.DateTime, deltaMeta2.CreatedAt.DateTime, deltaMeta2.CreatedAt.DateTime, deltaMeta2.OriginalSize,
                    deltaMeta2.Type, Path.GetRelativePath(settings.DataDir, fhc2), deltaMeta2.Sha256, checkout.Id, deltaMeta2.DeltaIndex, deltaMeta2.CreatedAt);

                var beforeAttrs = db.GetAttributes(fileEntry.Id).ToList();
                Assert.IsTrue(beforeAttrs.Count >= 3);

                // Run retention prune (MaxGenerations=1 -> older should be eligible based on count), but because one delta is not eligible, checkout should be preserved
                var deleted = FileHistory.RetentionWorker.PruneFileGenerations(settings, db, loggerFactory.CreateLogger("retention-test-inv"), fileEntry, CancellationToken.None);

                var after = db.GetAttributes(fileEntry.Id).ToList();
                // Checkout should still exist because one dependent delta was not eligible
                Assert.IsTrue(after.Any(a => a.Type == "checkout"), "Checkout must be preserved when a dependent delta is not eligible");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
