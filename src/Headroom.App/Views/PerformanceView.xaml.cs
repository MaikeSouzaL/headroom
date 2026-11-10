using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Headroom.App.Views;

public partial class PerformanceView : UserControl
{
    public PerformanceView()
    {
        InitializeComponent();

        // Ao abrir, o foco cai numa caixa de seleção do Monitor e o ScrollViewer rola até ela; volta ao topo.
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => MonitorScroll.ScrollToTop());
    }
}
