using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace AboEzzatReceiver.Windows;

public partial class BrowserWindow : Window
{
    private bool _browserReady;

    public BrowserWindow()
    {
        InitializeComponent();
        Loaded += BrowserWindow_Loaded;
    }

    private async void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AboEzzatTV", "WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await Browser.EnsureCoreWebView2Async(environment);

            var core = Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;

            core.NavigationStarting += (_, args) =>
            {
                AddressBox.Text = args.Uri;
                StatusText.Text = "جارٍ تحميل الصفحة...";
            };
            core.NavigationCompleted += (_, args) =>
            {
                BackButton.IsEnabled = core.CanGoBack;
                ForwardButton.IsEnabled = core.CanGoForward;
                StatusText.Text = args.IsSuccess
                    ? "تم تحميل الصفحة"
                    : $"تعذّر تحميل الصفحة: {args.WebErrorStatus}";
            };
            core.NewWindowRequested += (_, args) =>
            {
                // Keep target=_blank links inside the receiver instead of launching Edge.
                args.Handled = true;
                Navigate(args.Uri);
            };

            _browserReady = true;
            Navigate("https://www.google.com");
            AddressBox.Focus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذّر تشغيل المتصفح الداخلي";
            MessageBox.Show(
                "تعذّر تهيئة المتصفح الداخلي. تأكد من تثبيت Microsoft Edge WebView2 Runtime ثم أعد المحاولة.\n\n"
                + ex.Message
                + "\n\nhttps://developer.microsoft.com/microsoft-edge/webview2/",
                "AboEzzat Receiver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Navigate(string? input)
    {
        if (!_browserReady || Browser.CoreWebView2 is null || string.IsNullOrWhiteSpace(input))
            return;

        string value = input.Trim();
        string target;

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            target = uri.AbsoluteUri;
        }
        else if (!value.Contains(' ') && value.Contains('.'))
        {
            target = "https://" + value;
        }
        else
        {
            target = "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
        }

        try
        {
            Browser.CoreWebView2.Navigate(target);
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذّر فتح العنوان: " + ex.Message;
        }
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Navigate(AddressBox.Text);

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Navigate(AddressBox.Text);
        e.Handled = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady && Browser.CoreWebView2.CanGoBack)
            Browser.CoreWebView2.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady && Browser.CoreWebView2.CanGoForward)
            Browser.CoreWebView2.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady)
            Browser.CoreWebView2.Reload();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => Navigate("https://www.google.com");

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
