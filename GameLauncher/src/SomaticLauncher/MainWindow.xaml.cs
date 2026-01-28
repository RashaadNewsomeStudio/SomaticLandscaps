using System.Windows;
using System.Windows.Input;
using SomaticLauncher.ViewModels;

namespace SomaticLauncher
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel => (MainViewModel)DataContext;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}