using Avalonia.Markup.Xaml;

namespace JeekMusic.UI;

public sealed class TrExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(text);
}
