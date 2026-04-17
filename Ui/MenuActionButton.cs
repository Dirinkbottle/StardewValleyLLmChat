using Microsoft.Xna.Framework;

namespace StardewMod.Ui;

/// <summary>
/// 通用卡片式按钮。
/// </summary>
internal sealed class MenuActionButton
{
    public MenuActionButton(string title, string description, string footer = "")
    {
        this.Title = title;
        this.Description = description;
        this.Footer = footer;
    }

    public string Title { get; }

    public string Description { get; }

    public string Footer { get; }

    public string BadgeText { get; set; } = string.Empty;

    public bool BadgeAccent { get; set; }

    public Rectangle Bounds { get; private set; }

    public void SetBounds(Rectangle bounds)
    {
        this.Bounds = bounds;
    }

    public bool Contains(int x, int y)
    {
        return this.Bounds.Contains(x, y);
    }
}
