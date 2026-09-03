using System.Windows;

namespace FoundrySummarizer.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Loaded += (s, e) =>
        {
            var workArea = SystemParameters.WorkArea;

            // Automatically constrain height if larger than screen work area
            if (Height > workArea.Height - 30)
            {
                Height = Math.Max(520, workArea.Height - 50);
            }

            // Automatically constrain width if larger than screen work area
            if (Width > workArea.Width - 30)
            {
                Width = Math.Max(800, workArea.Width - 40);
            }

            // Ensure window top title bar (minimize/maximize/close) is safely on screen
            Top = Math.Max(workArea.Top + 10, (workArea.Height - Height) / 2 + workArea.Top);
            Left = Math.Max(workArea.Left + 10, (workArea.Width - Width) / 2 + workArea.Left);
        };
    }
}
