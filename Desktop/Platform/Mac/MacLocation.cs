namespace NetStuff.Desktop.Platforms.Mac;

/// <summary>
/// Fragt einmalig die Standortfreigabe an (macOS gibt WLAN-Namen nur damit heraus).
/// Der Dialog erscheint nur, wenn die App als .app-Bundle mit NSLocationWhenInUseUsageDescription läuft
/// (siehe Desktop/macos/build-app.sh) – bei „dotnet run“ bleibt die Anfrage wirkungslos.
/// Muss auf dem Haupt-Thread aufgerufen werden.
/// </summary>
internal static class MacLocation
{
    private static IntPtr _manager;

    /// <summary>CLAuthorizationStatus: 0 = nicht gefragt, 1 = eingeschränkt, 2 = abgelehnt, 3/4 = erlaubt; -1 = unbekannt.</summary>
    public static int Status => _manager == IntPtr.Zero || !ObjC.Responds(_manager, "authorizationStatus") ? -1 : ObjC.GetInt32(_manager, "authorizationStatus");

    public static void RequestOnce()
    {
        try
        {
            if (_manager != IntPtr.Zero || !ObjC.LoadFramework("CoreLocation")) return;
            var cls = ObjC.Class("CLLocationManager");
            if (cls == IntPtr.Zero) return;
            _manager = ObjC.Get(ObjC.Get(cls, "alloc"), "init");   // bleibt bewusst erhalten
            if (ObjC.Responds(_manager, "requestWhenInUseAuthorization")) ObjC.Call(_manager, "requestWhenInUseAuthorization");
            ObjC.Call(_manager, "startUpdatingLocation");
            Avalonia.Threading.DispatcherTimer.RunOnce(() => ObjC.Call(_manager, "stopUpdatingLocation"), TimeSpan.FromSeconds(15));
        }
        catch { }
    }
}
