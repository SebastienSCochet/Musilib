using System.Diagnostics;
using Musilib.Application.Abstractions;

namespace Musilib.Infrastructure.Configuration;

public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("URL invalide.", nameof(url));

        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else
            Process.Start("xdg-open", url);
    }
}
