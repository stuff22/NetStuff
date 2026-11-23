using NetStuff.Services;

namespace NetStuff.Platforms.Windows;

/// <summary>IWlanScanner über die native Windows-WLAN-API (wlanapi.dll).</summary>
public sealed class WindowsWlanScanner : IWlanScanner
{
    private readonly object _lock = new();
    private WlanApi _api;

    private WlanApi Api
    {
        get
        {
            lock (_lock) return _api ??= new WlanApi();
        }
    }

    public string PermissionHint => "ms-settings:privacy-location";

    public IReadOnlyList<WlanInterfaceInfo> Interfaces() =>
        Api.Interfaces().Select(i => new WlanInterfaceInfo(i.Id.ToString(), i.Description)).ToList();

    public async Task<List<WlanBss>> ScanAsync(string interfaceId, int settleMs, CancellationToken ct)
    {
        var id = Guid.Parse(interfaceId);
        Api.Scan(id);
        if (settleMs > 0) await Task.Delay(settleMs, ct);
        return await Task.Run(() => Api.GetBssList(id), ct);
    }

    public WlanConnection CurrentConnection(string interfaceId)
    {
        var c = Api.CurrentConnection(Guid.Parse(interfaceId));
        return c == null ? null : new WlanConnection(c.Profile, c.Ssid, c.Bssid, c.SignalQuality, c.RxMbps, c.TxMbps);
    }
}
