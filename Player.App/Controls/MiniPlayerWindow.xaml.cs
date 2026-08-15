using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Player.App.ViewModels;
using Player.Core.Infra;

namespace Player.App.Controls;

/// <summary>
/// L3.2 迷你悬浮窗：340×104 置顶卡片（封面/跑马灯标题/进度线/可选频谱柱）。
/// 显隐语义：开迷你窗 = 主窗隐藏；关（按钮/Esc/回主窗） = 主窗恢复。托盘退出仍是唯一退出。
/// 频谱红线：绝不直接对解码流/混音流 ChannelGetData 消费数据——引擎在 mixer 挂 DSP tap
/// 复制样本进环形缓冲，这里只对缓冲做 FFT（探针已验证不抢 ASIO 数据）。
/// </summary>
public partial class MiniPlayerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly PlayerViewModel _player;
    private readonly DispatcherTimer _marqueeTimer;
    private readonly DispatcherTimer _spectrumTimer;
    private readonly System.Windows.Shapes.Rectangle[] _bars = new System.Windows.Shapes.Rectangle[16];
    private double _marqueeOffset;

    /// <summary>回主窗请求（双击/按钮/Esc）。</summary>
    public event Action? RestoreRequested;

    /// <summary>主窗退出路径：置位后 OnClosing 放行真实关闭（不再拦截+回主窗）。</summary>
    public bool AllowRealClose { get; set; }

    public MiniPlayerWindow(PlayerViewModel player)
    {
        InitializeComponent();
        _player = player;
        DataContext = this;

        // 频谱柱容器（16 柱，代码驱动高度）
        for (var i = 0; i < 16; i++)
        {
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = 4,
                VerticalAlignment = VerticalAlignment.Bottom,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(Colors.Transparent)
            };
            bar.SetValue(Grid.ColumnProperty, i);
            SpectrumHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            SpectrumHost.Children.Add(bar);
            _bars[i] = bar;
        }
        ApplyThemeBars();

        BorderRoot.MouseEnter += (_, _) => FadeHover(1);
        BorderRoot.MouseLeave += (_, _) => FadeHover(0);

        // 跑马灯：标题超宽时缓慢滚动（30fps 拉取频谱的同一批定时器体系，跑马灯用低优先）
        _marqueeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _marqueeTimer.Tick += (_, _) => TickMarquee();
        _marqueeTimer.Start();

        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _spectrumTimer.Tick += (_, _) => TickSpectrum();

        SourceInitialized += (_, _) =>
        {
            RestorePosition();
            if (ConfigService.Current.Ui.MiniSpectrum)
            {
                _player.EnableSpectrum(true);
                _spectrumTimer.Start();
            }
        };
        Closed += (_, _) => { _player.EnableSpectrum(false); _spectrumTimer.Stop(); };
    }

    private void ApplyThemeBars()
    {
        var brush = (Brush)FindResource("AccentBrush");
        foreach (var bar in _bars) bar.Fill = brush;
    }

    private void FadeHover(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(150));
        HoverControls.BeginAnimation(OpacityProperty, anim);
    }

    private void TickMarquee()
    {
        var title = _player.Title ?? string.Empty;
        TitleText.Text = title;
        var target = Math.Max(40, BorderRoot.ActualWidth - 110);
        var width = title.Length * 13.0;
        if (width <= target)
        {
            _marqueeOffset = 0;
            TitleText.RenderTransform = null;
            return;
        }
        _marqueeOffset -= 2;
        var maxOffset = width - target + 20;
        if (_marqueeOffset < -maxOffset) _marqueeOffset = 0;
        TitleText.RenderTransform = new TranslateTransform(_marqueeOffset, 0);
    }

    private void TickSpectrum()
    {
        var levels = _player.GetSpectrumLevels(16);
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i].Height = 4 + levels[i] * 10;
        }
    }

    private void RestorePosition()
    {
        var pos = ConfigService.Current.Ui.MiniPos;
        if (string.IsNullOrWhiteSpace(pos)) return;
        var parts = pos.Split(',');
        if (parts.Length == 2 && double.TryParse(parts[0], out var x) && double.TryParse(parts[1], out var y))
        {
            var sw = SystemParameters.PrimaryScreenWidth;
            var sh = SystemParameters.PrimaryScreenHeight;
            x = Math.Clamp(x, 0, sw - Width);
            y = Math.Clamp(y, 0, sh - Height);
            Left = x;
            Top = y;
        }
    }

    private void SavePosition()
    {
        ConfigService.Current.Ui.MiniPos = $"{Left:0},{Top:0}";
        ConfigService.Save();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            // 双击 = 回主窗（Border 无 MouseDoubleClick 事件，在按下事件里判 ClickCount）
            OnRestoreClick(sender, e);
            return;
        }
        if (e.ClickCount == 1) DragMove();
        SavePosition();
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        SavePosition();
        Hide();
        RestoreRequested?.Invoke();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SavePosition();
            Hide();
            RestoreRequested?.Invoke();
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 频谱清理（无论隐藏还是真关闭都执行；Closed 事件在拦截路径不触发）
        _player.EnableSpectrum(false);
        _spectrumTimer.Stop();

        if (AllowRealClose)
        {
            base.OnClosing(e);
            return;
        }

        // 不真正关闭（隐藏即可，与主窗同生命周期）；真正退出走托盘
        SavePosition();
        e.Cancel = true;
        Hide();
        RestoreRequested?.Invoke();
        base.OnClosing(e);
    }
}
