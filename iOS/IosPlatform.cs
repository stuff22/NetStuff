using CoreGraphics;
using Foundation;
using PhotosUI;
using UIKit;
using UserNotifications;

namespace NetStuff.iOS;

/// <summary>
/// Plattformdienste unter iOS. Nicht verfügbar (iOS lässt es nicht zu): WLAN-Liste, ARP/MAC-Adressen, LLDP, Paketmitschnitt.
/// Ping/Traceroute laufen über ICMP-Datagram-Sockets (Core: DatagramIcmpSender, automatisch gewählt).
/// </summary>
internal static class IosPlatform
{
    public static void Register()
    {
        NetStuff.Platform.Notifier = new IosNotifier();
        NetStuff.Platform.UrlOpener = Open;
        NetStuff.Platform.PhotoPicker = PickPhotoAsync;
    }

    /// <summary>
    /// Foto aus der Mediathek (PHPicker – braucht keine Fotos-Berechtigung). „Current“ liefert die Originaldatei
    /// (HEIC/JPEG/RAW) unverändert, also mit allen Metadaten inkl. Aufnahmeort.
    /// </summary>
    private static Task<(byte[] Data, string Name)?> PickPhotoAsync()
    {
        var tcs = new TaskCompletionSource<(byte[] Data, string Name)?>();
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var root = TopController();
            if (root == null) { tcs.TrySetResult(null); return; }
            var config = new PHPickerConfiguration
            {
                SelectionLimit = 1,
                Filter = PHPickerFilter.GetAnyFilter(new[] { PHPickerFilter.ImagesFilter, PHPickerFilter.VideosFilter }),
                PreferredAssetRepresentationMode = PHPickerConfigurationAssetRepresentationMode.Current,
            };
            var picker = new PHPickerViewController(config) { Delegate = new PhotoPickerDelegate(tcs) };
            root.PresentViewController(picker, true, null);
        });
        return tcs.Task;
    }

    private sealed class PhotoPickerDelegate : PHPickerViewControllerDelegate
    {
        private readonly TaskCompletionSource<(byte[] Data, string Name)?> _tcs;
        public PhotoPickerDelegate(TaskCompletionSource<(byte[] Data, string Name)?> tcs) => _tcs = tcs;

        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(true, null);
            var provider = results?.FirstOrDefault()?.ItemProvider;
            var type = provider?.RegisteredTypeIdentifiers?.FirstOrDefault();
            if (provider == null || type == null) { _tcs.TrySetResult(null); return; }
            provider.LoadFileRepresentation(type, (url, error) =>
            {
                // Die Datei ist nur innerhalb dieses Rückrufs gültig – sofort lesen
                try
                {
                    if (url?.Path == null) { _tcs.TrySetResult(null); return; }
                    var data = File.ReadAllBytes(url.Path);
                    var name = (provider.SuggestedName ?? "Foto") + Path.GetExtension(url.Path);
                    _tcs.TrySetResult((data, name));
                }
                catch (Exception ex) { _tcs.TrySetException(ex); }
            });
        }
    }

    /// <summary>Webadressen in Safari öffnen, lokale Dateien (Berichte, PDF, CSV) über den Teilen-Dialog anbieten.</summary>
    private static void Open(string target)
    {
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            if (File.Exists(target))
            {
                var root = TopController();
                if (root == null) return;
                var share = new UIActivityViewController(new NSObject[] { NSUrl.FromFilename(target) }, null);
                if (share.PopoverPresentationController is { } pop)   // iPad: als Popover in der Bildschirmmitte
                {
                    pop.SourceView = root.View;
                    pop.SourceRect = new CGRect(root.View!.Bounds.GetMidX(), root.View.Bounds.GetMidY(), 0, 0);
                    pop.PermittedArrowDirections = 0;
                }
                root.PresentViewController(share, true, null);
            }
            else if (NSUrl.FromString(target) is { } url)
            {
                UIApplication.SharedApplication.OpenUrl(url, new UIApplicationOpenUrlOptions(), null);
            }
        });
    }

    private static UIViewController TopController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>()
            .SelectMany(s => s.Windows).FirstOrDefault(w => w.IsKeyWindow);
        var vc = window?.RootViewController;
        while (vc?.PresentedViewController != null) vc = vc.PresentedViewController;
        return vc;
    }
}

/// <summary>Mitteilungen (z. B. Ausfälle in der Überwachung). Erlaubnis wird beim ersten Mal angefragt.</summary>
internal sealed class IosNotifier : UNUserNotificationCenterDelegate, INotifier
{
    private bool _asked;

    public void Show(string title, string message)
    {
        var center = UNUserNotificationCenter.Current;
        if (!_asked)
        {
            _asked = true;
            center.Delegate = this;
            center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (_, _) => Post(title, message));
            return;
        }
        Post(title, message);
    }

    private static void Post(string title, string message)
    {
        var content = new UNMutableNotificationContent { Title = title ?? "", Body = message ?? "" };
        var request = UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString(), content, null);
        UNUserNotificationCenter.Current.AddNotificationRequest(request, null);
    }

    // Auch anzeigen, während die App im Vordergrund ist
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
        => completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound);
}
