using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Player.App.ViewModels;
using Player.Core.Audio;

namespace Player.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // 进度条：鼠标按下或开始拖动就"接管"滑条（定时器停止回写），松手才真正 seek。
        // 点击跳转和拖动两条路径都走同一对 Begin/EndSeek，由 VM 保证一次操作只 seek 一次。
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSeekDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSeekDragCompleted));

        DataContextChanged += OnDataContextChanged;
    }

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    // 自动连播时让当前曲目保持在可视区内
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;

        if (e.NewValue is INotifyPropertyChanged newVm)
            newVm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.SelectedIndex)) return;

        var index = Vm?.SelectedIndex ?? -1;
        if (index < 0 || index >= QueueList.Items.Count) return;

        QueueList.ScrollIntoView(QueueList.Items[index]);
    }

    // ---------------- 拖放 ----------------

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ContainsPlayableFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            Vm?.LoadPaths(paths);

        e.Handled = true;
    }

    private static bool ContainsPlayableFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return false;
        return paths.Any(p => Directory.Exists(p) || AudioFormats.IsSupported(p));
    }

    // ---------------- 队列 ----------------

    private void QueueList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(QueueList, source) is not ListBoxItem container) return;

        var index = QueueList.ItemContainerGenerator.IndexFromContainer(container);
        if (index >= 0) Vm?.PlayQueueItem(index);
    }

    // ---------------- 进度条 ----------------

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => Vm?.BeginSeek();

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e) => Vm?.EndSeek(SeekSlider.Value);

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Vm?.BeginSeek();

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Vm?.EndSeek(SeekSlider.Value);

    // ---------------- 深色标题栏 ----------------

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var enabled = 1;
            if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
        catch
        {
            // 老系统没有该属性，忽略即可
        }
    }
}
