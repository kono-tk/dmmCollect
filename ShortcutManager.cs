using System;
using System.IO;

namespace dmmCollect
{
    public class ShortcutManager
    {
        private readonly dynamic _shell;

        public ShortcutManager()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException("ショートカット操作はWindowsでのみサポートされています。");
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("WScript.Shell がシステムに存在しません。");
            }

            _shell = Activator.CreateInstance(shellType)!;
        }

        public void SetFileTimestamp(string filePath, DateTime dt)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                // 作成日時、更新日時、アクセス日時をすべて設定
                File.SetCreationTime(filePath, dt);
                File.SetLastWriteTime(filePath, dt);
                File.SetLastAccessTime(filePath, dt);
                
                Console.WriteLine($"    タイムスタンプを {dt:yyyy-MM-dd HH:mm:ss} に設定: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    タイムスタンプの設定に失敗しました ({Path.GetFileName(filePath)}): {ex.Message}");
            }
        }

        public void CreateShortcut(string linkPath, string targetPath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                dynamic shortcut = _shell.CreateShortcut(linkPath);
                shortcut.TargetPath = targetPath;
                shortcut.Save();

                Console.WriteLine($"    [LINK] リンクを処理: '{Path.GetFileName(linkPath)}' -> '{Path.GetFileName(targetPath)}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    リンク作成/上書きに失敗しました ({Path.GetFileName(linkPath)}): {ex.Message}");
            }
        }

        /// <summary>
        /// 既存の .lnk ファイルのリンク先パスを取得します。
        /// 取得に失敗した場合は null を返します。
        /// </summary>
        public string? GetTargetPath(string linkPath)
        {
            try
            {
                dynamic shortcut = _shell.CreateShortcut(linkPath);
                string targetPath = shortcut.TargetPath;
                return string.IsNullOrEmpty(targetPath) ? null : targetPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
