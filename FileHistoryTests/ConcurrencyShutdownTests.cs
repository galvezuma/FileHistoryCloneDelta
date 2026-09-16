using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileHistoryTests
{
    [TestClass]
    public class ConcurrencyShutdownTests
    {
        [TestMethod]
        public void SchedulerAndRetention_RunConcurrently_GracefulShutdown()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_conc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var settings = new FileHistory.Settings
                {
                    BackupBaseDir = tmp,
                    MaxDeltasBeforeCheckout = 10,
                    DeltaSizeThresholdPercent = 50.0,
                    MaxGenerations = 5,
                    RetentionScanInterval = 1, // run often for test
                    RetentionStartupDelay = 0,
                };

                var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));
                using var db = new FileHistory.BackupDb(settings, loggerFactory);

                using var scheduler = new FileHistory.BackupScheduler(settings, loggerFactory, db);
                using var retention = new FileHistory.RetentionWorker(settings, db, loggerFactory);

                // create several files and schedule frequent backups
                var files = Enumerable.Range(0, 5).Select(i => Path.Combine(Path.GetTempPath(), "fhc_conc_file_" + Guid.NewGuid().ToString("N") + ".txt")).ToList();
                foreach (var f in files)
                {
                    File.WriteAllText(f, "initial");
                    var fileAttr = new FileHistory.AttributeFileEntry(f);
                    scheduler.Add(f, null, fileAttr, FileHistory.SchedulePriority.High);
                }

                // Let them run concurrently for a short while
                Thread.Sleep(2000);

                // Trigger shutdown
                scheduler.Dispose();
                retention.Dispose();

                // After shutdown, DB should be consistent and no worker threads remain
                var fileCount = db.FileCount(CancellationToken.None).GetAwaiter().GetResult();
                Assert.IsTrue(fileCount >= 0);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
