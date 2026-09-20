using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Autoroadmarking_Pro.CadHost.UI
{
    public sealed class WebViewHost : IDisposable
    {
        private const string VirtualHostName =
            "autoroadmarking.local";

        private readonly WebView2 _webView;
        private readonly WebMessageRouter _router;

        private bool _initialized;
        private bool _disposed;

        public WebViewHost(
            WebView2 webView,
            WebMessageRouter router)
        {
            _webView =
                webView ??
                throw new ArgumentNullException(
                    nameof(webView));

            _router =
                router ??
                throw new ArgumentNullException(
                    nameof(router));
        }

        public async Task InitializeAsync(
            string webRoot)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(WebViewHost));
            }

            if (_initialized)
                return;

            if (string.IsNullOrWhiteSpace(webRoot))
            {
                throw new ArgumentException(
                    "Đường dẫn UI web không hợp lệ.",
                    nameof(webRoot));
            }

            webRoot =
                Path.GetFullPath(
                    webRoot);

            string indexPath =
                Path.Combine(
                    webRoot,
                    "index.html");

            if (!Directory.Exists(webRoot))
            {
                throw new DirectoryNotFoundException(
                    "Không tìm thấy thư mục UI web đã chuẩn bị:\n\n" +
                    webRoot);
            }

            if (!File.Exists(indexPath))
            {
                throw new FileNotFoundException(
                    "Không tìm thấy index.html:\n\n" +
                    indexPath,
                    indexPath);
            }

            // WebView2 cần vùng dữ liệu có quyền ghi.
            // Không dùng thư mục AutoCAD trong Program Files.
            string localAppData =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData);

            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException(
                    "Không xác định được LocalApplicationData.");
            }

            string userDataFolder =
                Path.Combine(
                    localAppData,
                    "AutoRoadMarking_Pro",
                    "WebView2",
                    "C3D");

            Directory.CreateDirectory(
                userDataFolder);

            string runtimeVersion;

            try
            {
                runtimeVersion =
                    CoreWebView2Environment
                        .GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Không tìm thấy Microsoft Edge WebView2 Runtime.",
                    ex);
            }

            if (string.IsNullOrWhiteSpace(runtimeVersion))
            {
                throw new InvalidOperationException(
                    "Microsoft Edge WebView2 Runtime chưa sẵn sàng.");
            }

            CoreWebView2Environment environment =
                await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: null);

            await _webView
                .EnsureCoreWebView2Async(
                    environment);

            if (_webView.CoreWebView2 == null)
            {
                throw new InvalidOperationException(
                    "CoreWebView2 khởi tạo không thành công.");
            }

            _webView.CoreWebView2
                .SetVirtualHostNameToFolderMapping(
                    VirtualHostName,
                    webRoot,
                    CoreWebView2HostResourceAccessKind.Allow);

            _webView.CoreWebView2
                .WebMessageReceived +=
                OnWebMessageReceived;

            _webView.CoreWebView2.Navigate(
                "https://" +
                VirtualHostName +
                "/index.html");

            _initialized = true;
        }

        private async void OnWebMessageReceived(
            object? sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_disposed)
                return;

            try
            {
                string requestJson =
                    e.WebMessageAsJson;

                WebResponse response =
                    await _router
                        .RouteAsync(
                            requestJson);

                string responseJson =
                    JsonSerializer.Serialize(
                        response);

                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2
                        .PostWebMessageAsJson(
                            responseJson);
                }
            }
            catch (Exception ex)
            {
                // Không để exception từ message handler thoát ra host AutoCAD.
                try
                {
                    WebResponse response =
                        WebResponse.Fail(
                            "System",
                            ex.Message);

                    string responseJson =
                        JsonSerializer.Serialize(
                            response);

                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2
                            .PostWebMessageAsJson(
                                responseJson);
                    }
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2
                        .WebMessageReceived -=
                        OnWebMessageReceived;
                }
            }
            catch
            {
            }

            _initialized = false;
        }
    }
}
