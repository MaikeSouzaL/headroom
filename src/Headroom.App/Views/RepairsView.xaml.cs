using System.Windows.Controls;

namespace Headroom.App.Views;

public partial class RepairsView : UserControl
{
    public RepairsView()
    {
        InitializeComponent();
    }

    /// <summary>Mantém a saída rolada até o fim enquanto o comando escreve.</summary>
    private void OnOutputChanged(object sender, TextChangedEventArgs e) => Output.ScrollToEnd();
}
