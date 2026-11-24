using Avalonia.Markup.Xaml;

namespace NetStuff.Ui.Infrastructure;

/// <summary>Übersetzter Text in AXAML: <c>Text="{l:T 'Ziele'}"</c> – deutscher Originaltext ist der Schlüssel (Kern: <see cref="L"/>).</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;
    public string Text { get; set; }
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text ?? "");
}
