using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ChernobylZLauncher.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();

            var launcherFolder = Path.Combine(AppContext.BaseDirectory, "launcher");

            if (!Directory.Exists(launcherFolder))
            {
                MessageBox.Show($"No se encontró la carpeta:\n{launcherFolder}");
                return;
            }

            WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "chernobylz.local",
                launcherFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            WebView.CoreWebView2.Navigate("https://chernobylz.local/launcher.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al iniciar WebView2:\n{ex.Message}");
        }
    }
}