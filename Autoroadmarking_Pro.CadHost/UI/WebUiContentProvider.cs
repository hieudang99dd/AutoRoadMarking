using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Autoroadmarking_Pro.CadHost.UI
{
    /// <summary>
    /// Chuẩn bị bộ HTML/CSS/JS cho WebView2.
    ///
    /// UI web được nhúng vào CadHost.dll dưới dạng EmbeddedResource.
    /// Khi chạy, lớp này giải nén UI vào LocalAppData.
    ///
    /// Vì vậy plugin KHÔNG còn phụ thuộc vào:
    ///     bin\...\UI\web
    /// và cũng KHÔNG phụ thuộc vào:
    ///     C:\Program Files\Autodesk\AutoCAD ...\UI\web
    /// </summary>
    public static class WebUiContentProvider
    {
        private const string EmbeddedPrefix = "ARM_WEB/";

        public static string PrepareWebRoot()
        {
            Assembly assembly = typeof(WebUiContentProvider).Assembly;

            string localAppData =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException(
                    "Không xác định được thư mục LocalApplicationData.");
            }

            string assemblyName =
                assembly.GetName().Name ?? "Autoroadmarking_Pro.CadHost";

            string safeAssemblyName =
                MakeSafeFolderName(assemblyName);

            string targetRoot =
                Path.Combine(
                    localAppData,
                    "AutoRoadMarking_Pro",
                    "WebUI",
                    safeAssemblyName);

            Directory.CreateDirectory(targetRoot);

            string[] resources =
                assembly
                    .GetManifestResourceNames()
                    .Where(name =>
                        name.StartsWith(
                            EmbeddedPrefix,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            if (resources.Length == 0)
            {
                throw new InvalidOperationException(
                    "Không tìm thấy tài nguyên UI web được nhúng trong CadHost.dll.\n\n" +
                    "Hãy kiểm tra Autoroadmarking_Pro.CadHost.csproj có block " +
                    "EmbeddedResource Include=\"UI\\web\\**\\*.*\" hay chưa.");
            }

            foreach (string resourceName in resources)
            {
                ExtractOneResource(
                    assembly,
                    resourceName,
                    targetRoot);
            }

            string indexPath =
                Path.Combine(
                    targetRoot,
                    "index.html");

            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException(
                    "Đã giải nén UI nhưng không tìm thấy index.html.",
                    indexPath);
            }

            return targetRoot;
        }

        private static void ExtractOneResource(
            Assembly assembly,
            string resourceName,
            string targetRoot)
        {
            string relative =
                resourceName.Substring(
                    EmbeddedPrefix.Length);

            relative =
                relative
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(relative))
                return;

            string fullTargetRoot =
                Path.GetFullPath(targetRoot)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            string destination =
                Path.GetFullPath(
                    Path.Combine(
                        targetRoot,
                        relative));

            // Không cho resource name thoát ra ngoài thư mục WebUI.
            if (!destination.StartsWith(
                    fullTargetRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Tên embedded resource không hợp lệ: " +
                    resourceName);
            }

            string? directory =
                Path.GetDirectoryName(
                    destination);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(
                    directory);
            }

            using Stream? input =
                assembly.GetManifestResourceStream(
                    resourceName);

            if (input == null)
            {
                throw new InvalidOperationException(
                    "Không mở được embedded resource: " +
                    resourceName);
            }

            using FileStream output =
                new FileStream(
                    destination,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read);

            input.CopyTo(output);
        }

        private static string MakeSafeFolderName(
            string value)
        {
            foreach (char invalid in
                Path.GetInvalidFileNameChars())
            {
                value =
                    value.Replace(
                        invalid,
                        '_');
            }

            return value;
        }
    }
}
