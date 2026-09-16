using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace FileHistoryTests
{
    [TestClass]
    public class BackupSchedulerIntegrationTests
    {
        [TestMethod]
        public void BackupScheduler_CreatesFhcAndDbEntry_EndToEnd()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "fhc_sched_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                var settings = new FileHistory.Settings { BackupBaseDir = tmp, MaxDeltasBeforeCheckout = 10, DeltaSizeThresholdPercent = 50.0 };
                var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug().SetMinimumLevel(LogLevel.Debug));

                using var db = new FileHistory.BackupDb(settings, loggerFactory);

                var scheduler = new FileHistory.BackupScheduler(settings, loggerFactory, db);

                // create a test file
                var dataDir = Path.Combine(tmp, Environment.UserName, Environment.MachineName, "BackupFiles");
                Directory.CreateDirectory(Path.Combine(dataDir, "C"));
                var testFile = Path.Combine(Path.GetTempPath(), "fhc_testfile_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(testFile, "hello world");

                var fileAttr = new FileHistory.AttributeFileEntry(testFile);

                // add high priority schedule to trigger immediate backup
                scheduler.Add(testFile, null, fileAttr, FileHistory.SchedulePriority.High);

                // wait up to 5 seconds for DB entry to appear
                var stop = DateTime.Now.AddSeconds(10);
                bool ok = false;
                while (DateTime.Now < stop)
                {
                    var f = db.GetFile(testFile);
                    if (f != null)
                    {
                        var attrs = db.GetAttributes(f.Id);
                        if (attrs != null && attrs.Count > 0)
                        {
                            ok = true; break;
                        }
                    }
                    Thread.Sleep(200);
                }

                Assert.IsTrue(ok, "BackupScheduler did not create DB entry in time");

                var fdb = db.GetFile(testFile);
                var attr = db.GetLatestAttribute(fdb.Id);
                Assert.IsNotNull(attr);
                Assert.IsFalse(string.IsNullOrEmpty(attr.StoredFileName), "StoredFileName should be set for new .fhc backups");

                var storedPath = Path.Combine(settings.DataDir, attr.StoredFileName);
                Assert.IsTrue(File.Exists(storedPath), ".fhc file not created");
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }
    }
}
