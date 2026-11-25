// Startprogramm für den Mac-App-Store-Build (wird von build-appstore.sh übersetzt und ersetzt das .NET-Apphost).
//
// In der App Sandbox bricht AppKit ab, wenn NSApplication erst nach dem Start der .NET-Laufzeit (CoreCLR)
// angelegt wird: Die Sandbox verweigert dann den Zugriff auf com.apple.coreservices.launchservicesd und
// _RegisterApplication ruft abort() auf. Deshalb meldet dieses Programm die App zuerst bei macOS an und
// startet danach die .NET-Laufzeit wie das normale Apphost. Avalonia übernimmt die bestehende NSApplication.
#import <Cocoa/Cocoa.h>
#include <dlfcn.h>

#ifndef APP_DLL
#define APP_DLL "NetStuff.dll"
#endif

typedef int (*hostfxr_main_startupinfo_fn)(int argc, const char** argv, const char* host_path,
                                           const char* dotnet_root, const char* app_path);

int main(int argc, const char** argv) {
    @autoreleasepool { [NSApplication sharedApplication]; }

    NSString* exe = [[NSBundle mainBundle] executablePath];
    NSString* dir = [exe stringByDeletingLastPathComponent];
    NSString* fxr = [dir stringByAppendingPathComponent:@"libhostfxr.dylib"];
    NSString* app = [dir stringByAppendingPathComponent:@APP_DLL];

    void* h = dlopen(fxr.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
    if (!h) { fprintf(stderr, "libhostfxr.dylib nicht ladbar: %s\n", dlerror()); return 1; }
    hostfxr_main_startupinfo_fn run = (hostfxr_main_startupinfo_fn)dlsym(h, "hostfxr_main_startupinfo");
    if (!run) { fprintf(stderr, "hostfxr_main_startupinfo nicht gefunden\n"); return 1; }

    return run(argc, argv, exe.fileSystemRepresentation, dir.fileSystemRepresentation, app.fileSystemRepresentation);
}
