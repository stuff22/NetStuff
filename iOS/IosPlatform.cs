using CoreGraphics;
using Foundation;
using PhotosUI;
using UIKit;
using UniformTypeIdentifiers;
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
        NetStuff.Platform.PhotoPicker = PickPhoto;
    }

    /// <summary>
    /// Foto aus der Mediathek als Originaldatei (PHPicker, ohne Umwandlung – HEIC bleibt HEIC, Metadaten bleiben erhalten).
    /// Braucht keinen Zugriff auf die ganze Mediathek; der Nutzer wählt genau ein Bild.
    /// </summary>
    private static Task<(byte[] Data, string Name)?> PickPhoto()
    {
        var tcs = new TaskCompletionSource<(byte[] Data, string Name)?>();
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var root = TopController();
            if (root == null) { tcs.TrySetResult(null); return; }
            var config = new PHPickerConfiguration
            {
                SelectionLimit = 1,
                Filter = PHPickerFilter.ImagesFilter,
                PreferredAssetRepresentationMode = PHPickerConfigurationAssetRepresentationMode.Current,
            };
            var picker = new PHPickerViewController(config) { Delegate = new PhotoPickerDelegate(tcs) };
            root.PresentViewController(picker, true, null);
        });
        return tcs.Task;
    }

    private sealed class PhotoPickerDelegate(TaskCompletionSource<(byte[] Data, string Name)?> tcs) : PHPickerViewControllerDelegate
    {
        public override void DidFinishPicking(PHPickerViewController picker, PHPickerResult[] results)
        {
            picker.DismissViewController(true, null);
            if (results.Length == 0) { tcs.TrySetResult(null); return; }
            var provider = results[0].ItemProvider;
            // Ursprünglicher Bildtyp (HEIC, JPEG, DNG …), nicht eine umgewandelte Fassung
            var type = provider.RegisteredTypeIdentifiers.FirstOrDefault(t => UTType.CreateFromIdentifier(t)?.ConformsTo(UTTypes.Image) == true)
                       ?? UTTypes.Image.Identifier;
            provider.LoadFileRepresentation(type, (url, error) =>
            {
                // Die Datei existiert nur bis zum Ende dieses Aufrufs – sofort einlesen
                try
                {
                    if (url == null) { tcs.TrySetException(new IOException(error?.LocalizedDescription ?? "Foto nicht lesbar")); return; }
                    var data = File.ReadAllBytes(url.Path!);
                    var name = url.LastPathComponent ?? (provider.SuggestedName ?? "foto");
                    tcs.TrySetResult((data, name));
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
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
