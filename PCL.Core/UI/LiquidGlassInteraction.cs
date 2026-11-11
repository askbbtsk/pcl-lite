namespace PCL.Core.UI;

public static class LiquidGlassInteraction
{
    public static bool Moving(bool current, int windowMessage) => windowMessage switch
    {
        0x0231 => true,  // WM_ENTERSIZEMOVE (title dragging and border resizing)
        0x0232 => false, // WM_EXITSIZEMOVE (including cancelled moves)
        _ => current
    };

    public static double NavigationActionInset(double original, bool glassEnabled) =>
        glassEnabled ? System.Math.Max(18, original) : original;
}
