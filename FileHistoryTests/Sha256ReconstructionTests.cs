using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace FileHistoryTests
{
    [TestClass]
    public class Sha256ReconstructionTests
    {
        [TestMethod]
        public void ReconstructedFile_HasSameSha256AsOriginal()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_sha_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var settings = new FileHistory.Settings
                {
                    BackupBaseDir = tmp,
                    MaxDeltasBeforeCheckout = 10,
                    DeltaSizeThresholdPercent = 50.0,
                };

                var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
                using var db = new FileHistory.BackupDb(settings, loggerFactory);
                var scheduler = new FileHistory.BackupScheduler(settings, loggerFactory, db);
                var deltaService = new FileHistory.DeltaService(loggerFactory.CreateLogger<FileHistory.DeltaService>(), allowFallback: true);

                // create base file and a checkout
                var testFile = Path.Combine(Path.GetTempPath(), "fhc_sha_file_" + Guid.NewGuid().ToString("N") + ".bin");
                var baseContent = new byte[1024];
                new Random(42).NextBytes(baseContent);
                File.WriteAllBytes(testFile, baseContent);

                var fileEntry = db.InsertFile(testFile);

                var checkoutPayload = new MemoryStream(baseContent);
                var checkoutMeta = new FileHistory.FhcMetadata { Type = "checkout", Sha256 = deltaService.ComputeSha256Async(checkoutPayload).GetAwaiter().GetResult(), OriginalFileName = Path.GetFileName(testFile), OriginalSize = checkoutPayload.Length, DeltaIndex = 0 };
                var checkoutFhc = Path.Combine(settings.DataDir, Guid.NewGuid().ToString("N") + ".fhc");
                Directory.CreateDirectory(settings.DataDir);
                using (var ofs = new FileStream(checkoutFhc, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    FileHistory.FhcPackage.CreatePackageAsync(checkoutPayload, checkoutMeta, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                db.InsertAttributeExtended(fileEntry.Id, DateTime.Now.AddMinutes(-10), DateTime.Now.AddMinutes(-10), DateTime.Now.AddMinutes(-10), DateTime.Now.AddMinutes(-10), checkoutPayload.Length, "checkout", Path.GetRelativePath(settings.DataDir, checkoutFhc), checkoutMeta.Sha256, null, 0, DateTimeOffset.UtcNow);

                // create a delta (simulated) representing a small change
                var modified = (byte[])baseContent.Clone();
                modified[0] ^= 0xFF;
                var modifiedStream = new MemoryStream(modified);

                // Create a delta stream using DeltaService
                var deltaStream = deltaService.CreateDeltaAsync(new MemoryStream(baseContent), modifiedStream, CancellationToken.None).GetAwaiter().GetResult();
                deltaStream.Seek(0, SeekOrigin.Begin);

                var deltaMeta = new FileHistory.FhcMetadata { Type = "delta", BaseBackupId = db.GetLatestCheckoutAttribute(fileEntry.Id).Id.ToString(), DeltaIndex = 1, Sha256 = deltaService.ComputeSha256Async(deltaStream).GetAwaiter().GetResult(), OriginalFileName = Path.GetFileName(testFile), OriginalSize = modified.Length };
                var deltaFhc = Path.Combine(settings.DataDir, Guid.NewGuid().ToString("N") + ".fhc");
                using (var ofs = new FileStream(deltaFhc, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    // deltaStream may be non-seek, ensure at start
                    if (deltaStream.CanSeek) deltaStream.Seek(0, SeekOrigin.Begin);
                    FileHistory.FhcPackage.CreatePackageAsync(deltaStream, deltaMeta, ofs, CancellationToken.None).GetAwaiter().GetResult();
                }
                db.InsertAttributeExtended(fileEntry.Id, DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(-5), deltaMeta.OriginalSize, deltaMeta.Type, Path.GetRelativePath(settings.DataDir, deltaFhc), deltaMeta.Sha256, db.GetLatestCheckoutAttribute(fileEntry.Id).Id, deltaMeta.DeltaIndex, DateTimeOffset.UtcNow);

                // Reconstruct by applying deltas to checkout
                var checkoutAttr = db.GetLatestCheckoutAttribute(fileEntry.Id);
                var checkoutPath = Path.Combine(settings.DataDir, checkoutAttr.StoredFileName);
                using var checkoutFs = new FileStream(checkoutPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var extracted = FileHistory.FhcPackage.ExtractPackageAsync(checkoutFs, CancellationToken.None).GetAwaiter().GetResult();
                var baseMs = extracted.Payload;

                var deltas = db.GetAttributes(fileEntry.Id).Where(a => a.Type == "delta" && a.BaseAttributeId == checkoutAttr.Id).OrderBy(a => a.DeltaIndex).ToList();
                MemoryStream current = baseMs;
                foreach (var d in deltas)
                {
                    var dPath = Path.Combine(settings.DataDir, d.StoredFileName);
                    using var df = new FileStream(dPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var ex = FileHistory.FhcPackage.ExtractPackageAsync(df, CancellationToken.None).GetAwaiter().GetResult();
                    using var deltaMs = ex.Payload;
                    var applied = deltaService.ApplyDeltaAsync(current, deltaMs, CancellationToken.None).GetAwaiter().GetResult();
                    current.Dispose();
                    current = applied as MemoryStream ?? new MemoryStream();
                }

                // Compute sha256 of reconstructed and of modified original
                current.Seek(0, SeekOrigin.Begin);
                var reconSha = deltaService.ComputeSha256Async(current).GetAwaiter().GetResult();
                var modifiedSha = deltaService.ComputeSha256Async(new MemoryStream(modified)).GetAwaiter().GetResult();

                Assert.AreEqual(modifiedSha, reconSha, "Reconstructed file must have same SHA256 as modified original");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
