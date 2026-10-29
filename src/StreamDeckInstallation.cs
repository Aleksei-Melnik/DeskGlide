using System.Diagnostics;

namespace SdrCapture;

static class StreamDeckInstallation
{
    public const string Package="streamdeck/com.yami.deskglide.streamDeckPlugin";
    public const string MarketplaceUrl="https://marketplace.elgato.com/product/deskglide-756e68ee-b961-40bb-b19c-cb0a39635679";

    public static void Install()
    {
        // The published plugin is installed through Marketplace. Shell activation
        // also works when the browser is already running and returns no process.
        using var browser=Process.Start(InstallCommand());
    }

    internal static ProcessStartInfo InstallCommand()=>new(MarketplaceUrl){UseShellExecute=true};
}
