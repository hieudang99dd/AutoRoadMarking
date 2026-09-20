using System;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace Autoroadmarking_Pro.CadHost.UI
{
    public partial class MainWindow : Window
    {
        private WebView2? _webView;
        private WebViewHost? _host;
        private bool _isInitializing;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _host != null) return;
            _isInitializing = true;
            try
            {
                _webView = new WebView2();
                WebViewContainer.Children.Add(_webView);
                _host = new WebViewHost(_webView, new WebMessageRouter());
                string webRoot = WebUiContentProvider.PrepareWebRoot();
                await _host.InitializeAsync(webRoot);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "AutoRoadMarking Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isInitializing = false;
            }
        }

        internal void PostResponse(WebResponse response)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_webView?.CoreWebView2 == null) return;
                    string json = JsonSerializer.Serialize(response);
                    _webView.CoreWebView2.PostWebMessageAsJson(json);
                }
                catch { }
            }));
        }

        internal void HideForCadInput()
        {
            Action action = () => { try { Hide(); } catch { } };
            if (Dispatcher.CheckAccess()) action(); else Dispatcher.Invoke(action);
        }

        internal void RestoreAfterCadInput()
        {
            Action action = () =>
            {
                try { Show(); Activate(); } catch { }
            };
            if (Dispatcher.CheckAccess()) action(); else Dispatcher.BeginInvoke(action);
        }

        protected override void OnClosed(EventArgs e)
        {
            SafeCleanup();
            base.OnClosed(e);
        }

        private void SafeCleanup()
        {
            try { _host?.Dispose(); } catch { }
            _host = null;
            try { _webView?.Dispose(); } catch { }
            _webView = null;
            try { WebViewContainer.Children.Clear(); } catch { }
        }
    }

    public static class MainWindowManager
    {
        private static MainWindow? _window;

        public static void Show()
        {
            if (_window != null)
            {
                try
                {
                    if (_window.IsVisible) { _window.Activate(); return; }
                }
                catch { _window = null; }
            }

            _window = new MainWindow();
            _window.Closed += (_, __) => _window = null;
            Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModelessWindow(_window);
        }

        public static void PostResponse(WebResponse response) => _window?.PostResponse(response);
        public static void HideForCadInput() => _window?.HideForCadInput();
        public static void RestoreAfterCadInput() => _window?.RestoreAfterCadInput();
    }
}
