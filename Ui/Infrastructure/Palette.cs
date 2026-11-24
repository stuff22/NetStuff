using Avalonia.Media;

namespace NetStuff.Ui.Infrastructure;

public static class Palette
{
    public static readonly Color Ok = Color.FromRgb(0x3F, 0xB9, 0x50);
    public static readonly Color Warn = Color.FromRgb(0xD2, 0x99, 0x22);
    public static readonly Color Bad = Color.FromRgb(0xF8, 0x51, 0x49);

    public static readonly Color[] Series =
    {
        Color.FromRgb(0x4C, 0xA3, 0xFF), Color.FromRgb(0x3F, 0xB9, 0x50), Color.FromRgb(0xF0, 0x88, 0x3E),
        Color.FromRgb(0xC2, 0x7A, 0xF5), Color.FromRgb(0xF8, 0x51, 0x49), Color.FromRgb(0x2D, 0xD4, 0xBF),
        Color.FromRgb(0xE3, 0xB3, 0x41), Color.FromRgb(0xFF, 0x7B, 0xAC), Color.FromRgb(0x8B, 0xA4, 0xFF),
        Color.FromRgb(0x9B, 0xD0, 0x5F), Color.FromRgb(0xFF, 0xA6, 0x57), Color.FromRgb(0x56, 0xC2, 0xE6),
    };

    public static IBrush For(Level l) => new SolidColorBrush(l switch
    {
        Level.Ok => Ok,
        Level.Warn => Warn,
        Level.Bad => Bad,
        _ => Color.FromRgb(0x80, 0x80, 0x80),
    });
}
