using System.Windows;
using System.Windows.Controls;

namespace Player.App.Controls;

/// <summary>经典菜单样式选择器：MenuItem 与 Separator 分开应用 keyed 样式
/// （ItemContainerStyle 直接设 MenuItem 样式会在 Separator 上抛类型不匹配）。</summary>
public sealed class ClassicMenuStyleSelector : StyleSelector
{
    public Style? MenuItemStyle { get; set; }

    public Style? SeparatorStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
        => container is Separator ? SeparatorStyle : MenuItemStyle;
}
