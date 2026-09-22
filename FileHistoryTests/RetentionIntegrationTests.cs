using FileHistory;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace FileHistoryTests {
    [TestClass]
    public class RetentionIntegrationTests {
        [TestMethod]
        public void RetentionWorker_DeletesCheckoutAndDependentDeltas_WhenAllEligible() {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_retention_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try {
                var settings = new FileHistory.Settings {
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
                var oldCheckoutMetadata = new FileHistory.FhcMetadata {
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
                using (var ofs = new FileStream(oldCheckoutPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    FileHistory.FhcPackage.CreatePackageAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("old")), oldCheckoutMetadata, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                var oldRel = Path.GetRelativePath(settings.DataDir, oldCheckoutPath);
                db.InsertAttributeExtended(fileEntry.Id, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutMetadata.OriginalSize,
                    "checkout", oldRel, oldCheckoutMetadata.Sha256, null, 0, oldCheckoutMetadata.CreatedAt);

                var checkout = db.GetLatestCheckoutAttribute(fileEntry.Id);
                Assert.IsNotNull(checkout, "Checkout attribute must exist");

                // create two delta .fhc files referencing the old checkout
                for (int idx = 1; idx <= 2; idx++) {
                    var payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"delta{idx}"));
                    var metadata = new FileHistory.FhcMetadata {
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
                    using (var ofs = new FileStream(fhcPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
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
                while (DateTime.Now < stop) {
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
                foreach (var f in storedFiles) {
                    if (!remaining.Contains(f))
                        Assert.IsFalse(File.Exists(f), "Deleted attribute stored file should be removed: " + f);
                }
            } finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [TestMethod]
        public void RetentionWorker_DoesNotDeleteCheckoutWhenDependentDeltaNotEligible() {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_retention_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try {
                var settings = new FileHistory.Settings {
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
                var oldCheckoutMetadata = new FileHistory.FhcMetadata {
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
                using (var ofs = new FileStream(oldCheckoutPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    FileHistory.FhcPackage.CreatePackageAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("old")), oldCheckoutMetadata, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                var oldRel = Path.GetRelativePath(settings.DataDir, oldCheckoutPath);
                db.InsertAttributeExtended(fileEntry.Id, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutTime, oldCheckoutMetadata.OriginalSize,
                    "checkout", oldRel, oldCheckoutMetadata.Sha256, null, 0, oldCheckoutMetadata.CreatedAt);

                var checkout = db.GetLatestCheckoutAttribute(fileEntry.Id);
                Assert.IsNotNull(checkout);

                // create one delta eligible for deletion
                var payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("delta1"));
                var deltaMeta1 = new FileHistory.FhcMetadata {
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
                using (var ofs = new FileStream(fhc1, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    FileHistory.FhcPackage.CreatePackageAsync(payload, deltaMeta1, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                db.InsertAttributeExtended(fileEntry.Id, deltaMeta1.CreatedAt.DateTime, deltaMeta1.CreatedAt.DateTime, deltaMeta1.CreatedAt.DateTime, deltaMeta1.CreatedAt.DateTime, deltaMeta1.OriginalSize,
                    deltaMeta1.Type, Path.GetRelativePath(settings.DataDir, fhc1), deltaMeta1.Sha256, checkout.Id, deltaMeta1.DeltaIndex, deltaMeta1.CreatedAt);

                // create one delta NOT eligible for deletion (future date)
                var payload2 = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("delta2"));
                var deltaMeta2 = new FileHistory.FhcMetadata {
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
                using (var ofs = new FileStream(fhc2, FileMode.Create, FileAccess.Write, FileShare.None)) {
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
            } finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [TestMethod]
        public void RetentionWorker_KeepsCheckoutAndAllPredecessorDeltas_WhenNewestDeltaIsRetained() {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_retention_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            try {
                var settings = new FileHistory.Settings {
                    BackupBaseDir = tmp,
                    MaxGenerations = 1,
                    RetentionDays = 0,
                    MaxDeltasBeforeCheckout = 10,
                    DeltaSizeThresholdPercent = 50.0,
                };

                using var loggerFactory = LoggerFactory.Create(builder =>
                    builder.AddDebug().SetMinimumLevel(LogLevel.Debug));

                using var db = new FileHistory.BackupDb(settings, loggerFactory);

                var testFile = Path.Combine(
                    Path.GetTempPath(),
                    "fhc_retention_chain_" + Guid.NewGuid().ToString("N") + ".txt");

                File.WriteAllText(testFile, "test");

                Directory.CreateDirectory(settings.DataDir);

                var fileEntry = db.InsertFile(testFile);
                var baseTime = DateTime.Now.AddDays(-10);

                AttributeDbEntry AddPackage(
                    string type,
                    DateTime time,
                    int deltaIndex,
                    int? baseAttributeId,
                    string payloadText) {
                    var metadata = new FileHistory.FhcMetadata {
                        Type = type,
                        BaseBackupId = baseAttributeId?.ToString(),
                        DeltaIndex = deltaIndex,
                        Sha256 = "",
                        OriginalFileName = Path.GetFileName(testFile),
                        OriginalSize = System.Text.Encoding.UTF8.GetByteCount(payloadText),
                        CreatedAt = time,
                        FormatVersion = 1
                    };

                    var fileName = Guid.NewGuid().ToString("N") + ".fhc";
                    var path = Path.Combine(settings.DataDir, fileName);

                    using (var output = new FileStream(
                        path,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None))
                    using (var payload = new MemoryStream(
                        System.Text.Encoding.UTF8.GetBytes(payloadText))) {
                        FileHistory.FhcPackage.CreatePackageAsync(
                            payload,
                            metadata,
                            output,
                            CancellationToken.None).GetAwaiter().GetResult();
                    }

                    db.InsertAttributeExtended(
                        fileEntry.Id,
                        time,
                        time,
                        time,
                        time,
                        metadata.OriginalSize,
                        metadata.Type,
                        Path.GetRelativePath(settings.DataDir, path),
                        metadata.Sha256,
                        baseAttributeId,
                        metadata.DeltaIndex,
                        metadata.CreatedAt);

                    return db.GetAttributes(fileEntry.Id)
                        .Single(a =>
                            a.Type == type &&
                            a.DeltaIndex == deltaIndex &&
                            a.BackupTime == time);
                }

                // C0 -> D1 -> D2 -> D3
                // Todos comparten BaseAttributeId = checkout.Id, pero D3 requiere
                // lógicamente C0, D1 y D2 para poder restaurarse.
                var checkout = AddPackage(
                    "checkout",
                    baseTime,
                    deltaIndex: 0,
                    baseAttributeId: null,
                    payloadText: "checkout");

                var delta1 = AddPackage(
                    "delta",
                    baseTime.AddMinutes(1),
                    deltaIndex: 1,
                    baseAttributeId: checkout.Id,
                    payloadText: "delta-1");

                var delta2 = AddPackage(
                    "delta",
                    baseTime.AddMinutes(2),
                    deltaIndex: 2,
                    baseAttributeId: checkout.Id,
                    payloadText: "delta-2");

                var delta3 = AddPackage(
                    "delta",
                    DateTime.Now.AddMinutes(1),
                    deltaIndex: 3,
                    baseAttributeId: checkout.Id,
                    payloadText: "delta-3");

                var before = db.GetAttributes(fileEntry.Id).ToList();
                Assert.AreEqual(4, before.Count, "La cadena de prueba debe tener C0 + D1 + D2 + D3.");

                // MaxGenerations = 1 marca inicialmente C0, D1 y D2 como candidatos.
                // D3 es la generación más reciente y debe conservarse.
                // La lógica correcta debe desmarcar C0, D1 y D2 como dependencias de D3.
                var deleted = FileHistory.RetentionWorker.PruneFileGenerations(
                    settings,
                    db,
                    loggerFactory.CreateLogger("retention-chain-test"),
                    fileEntry,
                    CancellationToken.None);

                var after = db.GetAttributes(fileEntry.Id)
                    .OrderBy(a => a.DeltaIndex)
                    .ToList();

                Assert.AreEqual(
                    0,
                    deleted,
                    "No se puede borrar ningún miembro de la cadena porque D3 se conserva.");

                Assert.AreEqual(
                    4,
                    after.Count,
                    "D3 retenido requiere C0, D1 y D2; la cadena completa debe permanecer.");

                CollectionAssert.AreEqual(
                    new[] { checkout.Id, delta1.Id, delta2.Id, delta3.Id },
                    after.Select(a => a.Id).ToArray(),
                    "Deben conservarse checkout y todos los predecesores del delta retenido.");

                CollectionAssert.AreEqual(
                    new[] { 0, 1, 2, 3 },
                    after.Select(a => a.DeltaIndex).ToArray(),
                    "La cadena debe conservar todos los DeltaIndex desde 0 hasta 3.");

                foreach (var attribute in after) {
                    Assert.IsFalse(
                        string.IsNullOrWhiteSpace(attribute.StoredFileName),
                        $"El atributo {attribute.Id} debe conservar StoredFileName.");

                    var path = Path.Combine(settings.DataDir, attribute.StoredFileName);

                    Assert.IsTrue(
                        File.Exists(path),
                        $"El fichero FHC del atributo {attribute.Id} debe seguir existiendo: {path}");
                }
            } finally {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
