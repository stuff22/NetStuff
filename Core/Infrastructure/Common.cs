using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetStuff.Infrastructure;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum Level { Info, Ok, Warn, Bad }

/// <summary>Eine Zeile in einer Prüfergebnis-Liste (wird automatisch übersetzt).</summary>
public class CheckItem
{
    public CheckItem(string name, string value, Level level = Level.Info)
    {
        Name = L.T(name ?? "");
        Value = L.T(value ?? "");
        Level = level;
    }

    public string Name { get; }
    public string Value { get; }
    public Level Level { get; }

    public override string ToString() => $"[{Level}] {Name}: {Value}";
}
