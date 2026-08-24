using StardewValley;
using StardewValley.Menus;

namespace StardewMod.Ui;

/// <summary>
/// 为整个模组菜单树共享一个可调的 UI 缩放倍率。
/// </summary>
internal static class ModMenuLayoutState
{
    private const int ViewportMargin = 32;
    private static readonly float[] ScaleSteps = { 0.85f, 1.0f, 1.15f, 1.3f, 1.45f };
    private static int scaleIndex = 1;

    public static int ScaleIndex => scaleIndex;

    public static float Scale => ScaleSteps[scaleIndex];

    public static string ScaleLabel => $"{ScaleSteps[scaleIndex]:0.##}x";

    public static void Initialize(int preferredScaleIndex)
    {
        scaleIndex = Math.Clamp(preferredScaleIndex, 0, ScaleSteps.Length - 1);
    }

    public static bool TryShrink()
    {
        if (scaleIndex <= 0)
        {
            return false;
        }

        scaleIndex--;
        return true;
    }

    public static bool TryGrow()
    {
        if (scaleIndex >= ScaleSteps.Length - 1)
        {
            return false;
        }

        scaleIndex++;
        return true;
    }

    public static int ScalePreferred(int value)
    {
        return (int)Math.Round(value * ScaleSteps[scaleIndex]);
    }

    public static int ScaleMinimum(int value, int absoluteMinimum)
    {
        return Math.Max(absoluteMinimum, (int)Math.Round(value * Math.Min(ScaleSteps[scaleIndex], 1f)));
    }

    public static (int Width, int Height) MeasureSize(
        int preferredWidth,
        int preferredHeight,
        int minWidth,
        int minHeight,
        int absoluteMinWidth,
        int absoluteMinHeight)
    {
        int scaledPreferredWidth = ScalePreferred(preferredWidth);
        int scaledPreferredHeight = ScalePreferred(preferredHeight);
        int scaledMinWidth = ScaleMinimum(minWidth, absoluteMinWidth);
        int scaledMinHeight = ScaleMinimum(minHeight, absoluteMinHeight);
        int maxWidth = Math.Max(1, Game1.uiViewport.Width - ViewportMargin * 2);
        int maxHeight = Math.Max(1, Game1.uiViewport.Height - ViewportMargin * 2);
        int effectiveMinWidth = Math.Min(scaledMinWidth, maxWidth);
        int effectiveMinHeight = Math.Min(scaledMinHeight, maxHeight);
        return (
            Math.Clamp(scaledPreferredWidth, effectiveMinWidth, maxWidth),
            Math.Clamp(scaledPreferredHeight, effectiveMinHeight, maxHeight));
    }

    public static void Resize(IClickableMenu menu, int preferredWidth, int preferredHeight, int minWidth, int minHeight)
    {
        Resize(menu, preferredWidth, preferredHeight, minWidth, minHeight, minWidth, minHeight);
    }

    public static void Resize(IClickableMenu menu, int preferredWidth, int preferredHeight, int minWidth, int minHeight, int absoluteMinWidth, int absoluteMinHeight)
    {
        (int width, int height) = MeasureSize(preferredWidth, preferredHeight, minWidth, minHeight, absoluteMinWidth, absoluteMinHeight);
        menu.width = width;
        menu.height = height;
        menu.xPositionOnScreen = (Game1.uiViewport.Width - menu.width) / 2;
        menu.yPositionOnScreen = (Game1.uiViewport.Height - menu.height) / 2;
    }

    public static void ResizeTopAnchored(
        IClickableMenu menu,
        int preferredWidth,
        int preferredHeight,
        int minWidth,
        int minHeight,
        int absoluteMinWidth,
        int absoluteMinHeight,
        int topMargin)
    {
        (int width, int height) = MeasureSize(preferredWidth, preferredHeight, minWidth, minHeight, absoluteMinWidth, absoluteMinHeight);
        menu.width = width;
        menu.height = height;
        menu.xPositionOnScreen = (Game1.uiViewport.Width - menu.width) / 2;
        menu.yPositionOnScreen = topMargin;
    }

    public static void ResizeBottomAnchored(
        IClickableMenu menu,
        int preferredWidth,
        int preferredHeight,
        int minWidth,
        int minHeight,
        int absoluteMinWidth,
        int absoluteMinHeight,
        int bottomMargin)
    {
        (int width, int height) = MeasureSize(preferredWidth, preferredHeight, minWidth, minHeight, absoluteMinWidth, absoluteMinHeight);
        menu.width = width;
        menu.height = height;
        menu.xPositionOnScreen = (Game1.uiViewport.Width - menu.width) / 2;
        menu.yPositionOnScreen = Game1.uiViewport.Height - menu.height - bottomMargin;
    }
}
