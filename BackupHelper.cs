using System;
using System.IO;
using System.Linq;

namespace dmmCollect
{
    public static class BackupHelper
    {
        public static void CreateBackup(string? filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(filePath) ?? "";
                string filename = Path.GetFileName(filePath);
                string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                
                string backupPath = Path.Combine(directory, $"{filename}.{timestamp}");
                File.Copy(filePath, backupPath, true);
                Console.WriteLine($"[BACKUP] Created backup: {backupPath}");

                CleanupOldBackups(directory, filename);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BACKUP] Failed to create backup for {filePath}: {ex.Message}");
            }
        }

        private static void CleanupOldBackups(string directory, string filename)
        {
            try
            {
                var dirInfo = new DirectoryInfo(directory);
                var prefix = filename + ".";
                var thresholdDate = DateTime.Now.AddMonths(-6);

                var backupFiles = dirInfo.GetFiles(filename + ".*")
                    .Where(f => f.Name.StartsWith(prefix) && f.Name.Length > prefix.Length);

                foreach (var file in backupFiles)
                {
                    string suffix = file.Name.Substring(prefix.Length);
                    
                    if (DateTime.TryParseExact(suffix, new[] { "yyyyMMdd-HHmmss", "yyyyMMdd_HHmmss" }, 
                        System.Globalization.CultureInfo.InvariantCulture, 
                        System.Globalization.DateTimeStyles.None, out DateTime backupDate))
                    {
                        if (backupDate < thresholdDate)
                        {
                            file.Delete();
                            Console.WriteLine($"[BACKUP] Deleted old backup: {file.FullName}");
                        }
                    }
                    else
                    {
                        if (file.LastWriteTime < thresholdDate)
                        {
                            file.Delete();
                            Console.WriteLine($"[BACKUP] Deleted old legacy backup by LastWriteTime: {file.FullName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BACKUP] Cleanup failed: {ex.Message}");
            }
        }
    }
}
