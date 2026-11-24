using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NetStuff.Desktop.Platforms.Mac;

/// <summary>
/// Minimaler Zugriff auf die Objective-C-Laufzeit (für CoreWLAN und CoreLocation ohne zusätzliche Bindungen).
/// objc_msgSend wird je Signatur einzeln deklariert – auf arm64 darf es nicht variadisch aufgerufen werden.
/// </summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgPtr(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgPtrPtrErr(IntPtr self, IntPtr sel, IntPtr a, out IntPtr err);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgPtrIndex(IntPtr self, IntPtr sel, nuint index);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern nint MsgNInt(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern int MsgInt32(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern double MsgDouble(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern byte MsgBoolNInt(IntPtr self, IntPtr sel, nint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern byte MsgBoolPtr(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern void MsgVoid(IntPtr self, IntPtr sel);

    private static readonly ConcurrentDictionary<string, IntPtr> Selectors = new();

    public static IntPtr Sel(string name) => Selectors.GetOrAdd(name, sel_registerName);
    public static IntPtr Class(string name) => objc_getClass(name);

    /// <summary>Lädt ein System-Framework, damit seine Klassen registriert sind.</summary>
    public static bool LoadFramework(string name) =>
        NativeLibrary.TryLoad($"/System/Library/Frameworks/{name}.framework/{name}", out _);

    public static IntPtr Get(IntPtr obj, string sel) => obj == IntPtr.Zero ? IntPtr.Zero : MsgPtr(obj, Sel(sel));
    public static nint GetNInt(IntPtr obj, string sel) => obj == IntPtr.Zero ? 0 : MsgNInt(obj, Sel(sel));
    public static int GetInt32(IntPtr obj, string sel) => obj == IntPtr.Zero ? 0 : MsgInt32(obj, Sel(sel));
    public static double GetDouble(IntPtr obj, string sel) => obj == IntPtr.Zero ? 0 : MsgDouble(obj, Sel(sel));
    public static void Call(IntPtr obj, string sel) { if (obj != IntPtr.Zero) MsgVoid(obj, Sel(sel)); }
    public static bool CallBool(IntPtr obj, string sel, nint arg) => obj != IntPtr.Zero && MsgBoolNInt(obj, Sel(sel), arg) != 0;
    public static IntPtr CallWithError(IntPtr obj, string sel, IntPtr arg, out IntPtr error)
    {
        error = IntPtr.Zero;
        return obj == IntPtr.Zero ? IntPtr.Zero : MsgPtrPtrErr(obj, Sel(sel), arg, out error);
    }

    public static bool Responds(IntPtr obj, string sel) =>
        obj != IntPtr.Zero && MsgBoolPtr(obj, Sel("respondsToSelector:"), Sel(sel)) != 0;

    /// <summary>NSString → string (null bei nil).</summary>
    public static string Str(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var p = MsgPtr(nsString, Sel("UTF8String"));
        return p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
    }

    /// <summary>NSData → byte[] (leer bei nil).</summary>
    public static byte[] Bytes(IntPtr nsData)
    {
        if (nsData == IntPtr.Zero) return Array.Empty<byte>();
        int len = (int)MsgNInt(nsData, Sel("length"));
        var p = MsgPtr(nsData, Sel("bytes"));
        if (len <= 0 || p == IntPtr.Zero) return Array.Empty<byte>();
        var b = new byte[len];
        Marshal.Copy(p, b, 0, len);
        return b;
    }

    /// <summary>Elemente eines NSArray oder NSSet.</summary>
    public static List<IntPtr> Items(IntPtr collection)
    {
        var list = new List<IntPtr>();
        if (collection == IntPtr.Zero) return list;
        var arr = Responds(collection, "allObjects") ? Get(collection, "allObjects") : collection;
        long count = MsgNInt(arr, Sel("count"));
        for (nuint i = 0; i < (nuint)count; i++) list.Add(MsgPtrIndex(arr, Sel("objectAtIndex:"), i));
        return list;
    }

    /// <summary>Autorelease-Pool für Aufrufe außerhalb des Haupt-Threads.</summary>
    public readonly struct Pool : IDisposable
    {
        private readonly IntPtr _p;
        public Pool(bool _ = true) { _p = objc_autoreleasePoolPush(); }
        public void Dispose() { if (_p != IntPtr.Zero) objc_autoreleasePoolPop(_p); }
    }
}
