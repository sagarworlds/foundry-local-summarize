using System.Collections.Specialized;
using System.Windows.Controls;

namespace FoundrySummarizer.Wpf.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();

        // Keep the newest message in view; scrolling is purely presentational, so it lives in the view.
        ((INotifyCollectionChanged)MessageList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && MessageList.Items.Count > 0)
            {
                MessageList.ScrollIntoView(MessageList.Items[^1]);
            }
        };
    }
}
