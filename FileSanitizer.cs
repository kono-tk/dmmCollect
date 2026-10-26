using System;
using System.IO;
using System.Text.RegularExpressions;

namespace dmmCollect
{
    public static class FileSanitizer
    {
        private static readonly Regex InvalidCharsPattern = new(@"[\\/:*?""<>|]", RegexOptions.Compiled);
        private static readonly Regex ReservedNamesPattern = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Windowsファイル名として無効な文字を置換しサニタイズする
        /// </summary>
        public static string SanitizeFilename(string name, string replacementChar = "_")
        {
            if (string.IsNullOrEmpty(name)) return "default_filename";
            
            string sanitized = InvalidCharsPattern.Replace(name, replacementChar);
            sanitized = sanitized.Trim().TrimEnd('.');
            
            if (ReservedNamesPattern.IsMatch(sanitized))
            {
                sanitized = $"_{sanitized}";
            }
            
            return string.IsNullOrEmpty(sanitized) ? "default_filename" : sanitized;
        }

        /// <summary>
        /// コロンを削除する形式でサニタイズする
        /// </summary>
        public static string SanitizeFilenameRemoveColon(string name)
        {
            if (string.IsNullOrEmpty(name)) return "default_filename";

            // コロン（半角・全角）を削除
            string sanitized = Regex.Replace(name, @"[：:]", "");
            
            // その他の無効文字は_で置換
            sanitized = Regex.Replace(sanitized, @"[\\/*?""<>|]", "_");
            sanitized = sanitized.Trim().TrimEnd('.');
            
            if (ReservedNamesPattern.IsMatch(sanitized))
            {
                sanitized = $"_{sanitized}";
            }
            
            return string.IsNullOrEmpty(sanitized) ? "default_filename" : sanitized;
        }

    }
}
