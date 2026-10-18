using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace VoxType_App;

// A real transparent backdrop prevents WinUI's default themed rectangle from
// showing behind the rounded XAML surface, including after a theme change.
internal sealed class TransparentOverlayBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? compositor;
    private Windows.UI.Composition.CompositionColorBrush? brush;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        _ = GetDefaultSystemBackdropConfiguration(target, root);
        compositor = new Windows.UI.Composition.Compositor();
        brush = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        target.SystemBackdrop = brush;
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        brush?.Dispose(); brush = null;
        compositor?.Dispose(); compositor = null;
        base.OnTargetDisconnected(target);
    }
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // This brush has no theme/activation-dependent controller. The default
        // implementation expects its own controller target and throws E_INVALIDARG
        // when used with our custom transparent brush after theme/activation changes.
    }
}
