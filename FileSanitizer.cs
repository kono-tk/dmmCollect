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

        /// <summary>
        /// Lnkファイル名から元のタイトル部分を抽出する
        /// </summary>
        public static string ExtractShortenedTitleFromLnkName(string lnkFilename)
        {
            string nameWithoutExt = lnkFilename.Replace(".lnk", "");
            string nameWithoutLink = nameWithoutExt.EndsWith("_link") ? nameWithoutExt[..^5] : nameWithoutExt;

            var match = Regex.Match(nameWithoutLink, @"^(.+?)(\d+)$");
            if (match.Success)
            {
                string basePart = match.Groups[1].Value;
                if (basePart.EndsWith("_"))
                {
                    basePart = basePart[..^1];
                }
                return basePart;
            }
            return nameWithoutLink;
        }

        /// <summary>
        /// ファイル名部分を指定の最大長に収めるようにフォーマットする
        /// </summary>
        public static string FormatFilename(string filePathStr, int maxLength)
        {
            string? directory = Path.GetDirectoryName(filePathStr) ?? "";
            string name = Path.GetFileName(filePathStr);

            if (name.Length <= maxLength) return filePathStr;

            string stem = Path.GetFileNameWithoutExtension(filePathStr);
            string ext = Path.GetExtension(filePathStr);

            var match = Regex.Match(stem, @"(\d+)(_link)$");
            string preservedSuffix;
            string baseName;

            if (match.Success)
            {
                string numPart = match.Groups[1].Value;
                string trailingPart = match.Groups[2].Value;
                preservedSuffix = $"{numPart}{trailingPart}{ext}";
                baseName = stem[..match.Index];
            }
            else
            {
                preservedSuffix = ext;
                baseName = stem;
            }

            int availableLen = maxLength - preservedSuffix.Length;
            if (availableLen < 0)
            {
                if (ext.Length < maxLength)
                {
                    string endSuffix = preservedSuffix[^maxLength..];
                    return Path.Combine(directory, endSuffix);
                }
                else
                {
                    return Path.Combine(directory, ext[..maxLength]);
                }
            }

            string trimmedBase = baseName[..Math.Min(availableLen, baseName.Length)];
            string newFilename = $"{trimmedBase}{preservedSuffix}";

            return Path.Combine(directory, newFilename);
        }
    }
}
