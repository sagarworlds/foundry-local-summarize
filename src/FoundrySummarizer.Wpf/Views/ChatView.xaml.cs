using System.Collections.Specialized;
using System.Windows.Controls;

namespace FoundrySummarizer.Wpf.Views;

public partial class ChatView : UserControl
{
    // True while the conversation is scrolled to the bottom; an answer being written then stays in view as it grows.
    // Scrolling up to reread something turns it off, so the text does not jump away while the user reads.
    private bool _followNewText = true;

    public ChatView()
    {
        InitializeComponent();

        // Keep the newest message in view; scrolling is purely presentational, so it lives in the view.
        ((INotifyCollectionChanged)MessageList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && MessageList.Items.Count > 0)
            {
                _followNewText = true;
                MessageList.ScrollIntoView(MessageList.Items[^1]);
            }
        };

        MessageList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer viewer) return;

        if (e.ExtentHeightChange == 0)
        {
            // The user scrolled: follow new text only from the bottom.
            _followNewText = viewer.VerticalOffset >= viewer.ScrollableHeight - 1;
        }
        else if (_followNewText)
        {
            // The content grew (an answer is being written): stay at the bottom.
            viewer.ScrollToEnd();
        }
    }
}
