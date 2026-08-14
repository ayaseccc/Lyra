using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Player.App.ViewModels;

namespace Player.App.Controls;

/// <summary>
/// 播放位置滑条接管（工程铁律：凡绑定播放位置的 Slider 一律使用本组件，禁止散装复制）。
/// 封装 P1.1-② 定稿的完整接管逻辑：Preview 按下/松开 + Thumb 拖动开始/结束 四条路径
/// 必然执行一次 BeginSeek/EndSeek（点击与拖动同一对接管），配合 PlayerViewModel 的
/// 乐观更新与 700ms 静默窗口，杜绝"拖动被定时器拉回"（P0.1 / P1.1 / B3 三次复发后立规）。
/// 用法：&lt;Slider ctrl:SeekSliderBehavior.Enable="True" ... /&gt;，滑条 DataContext 须为 PlayerViewModel。
/// </summary>
public static class SeekSliderBehavior
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(SeekSliderBehavior), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(Slider element, bool value) => element.SetValue(EnableProperty, value);

    public static bool GetEnable(Slider element) => (bool)element.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider slider || e.NewValue is not true) return;

        // handledEventsToo:true —— Slider 类处理器在 IsMoveToPointEnabled 时会把
        // PreviewMouseLeftButtonDown 标为已处理，普通实例处理器收不到（P1.1-② 根因）。
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPressed), true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnReleased), true);
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnDragStarted));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted));
        // 注：WPF 拖动取消（Esc/捕获丢失）同样会触发 DragCompleted（args.Canceled=true），
        // 因此 OnDragCompleted 已覆盖取消路径，无需额外事件。
    }

    /// <summary>按下：接管进度条（定时器停止回写）。</summary>
    private static void OnPressed(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;
        System.Diagnostics.Debug.Assert(slider.DataContext is PlayerViewModel,
            "SeekSliderBehavior：滑条 DataContext 应为 PlayerViewModel（否则接管静默失效）");
        slider.Tag = true;
        (slider.DataContext as PlayerViewModel)?.BeginSeek();
    }

    /// <summary>松开：无论点击还是拖动，必然执行一次 seek。按下不在滑条上（抬手刚好经过）不跳转。</summary>
    private static void OnReleased(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;
        if (slider.Tag is not true) return;
        slider.Tag = false;
        (slider.DataContext as PlayerViewModel)?.EndSeek(slider.Value);
    }

    /// <summary>拖动开始（Thumb 路径）：接管进度条。</summary>
    private static void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Slider slider) return;
        slider.Tag = true;
        (slider.DataContext as PlayerViewModel)?.BeginSeek();
    }

    /// <summary>拖动结束：必然 seek（同一位置可能被鼠标松开与拖动结束各调一次，EndSeek 幂等）。</summary>
    private static void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not Slider slider) return;
        slider.Tag = false;
        (slider.DataContext as PlayerViewModel)?.EndSeek(slider.Value);
    }
}
