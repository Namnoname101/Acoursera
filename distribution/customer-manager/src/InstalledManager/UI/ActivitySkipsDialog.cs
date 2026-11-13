using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Acose.CustomerManager.UI;

internal sealed class ActivitySkipsDialog : Window
{
    private readonly CheckBox _pdf;
    private readonly CheckBox _review;
    public bool SkipPdf => _pdf.IsChecked == true;
    public bool SkipReview => _review.IsChecked == true;

    public ActivitySkipsDialog(MainWindow owner, ActivitySkipSettings settings)
    {
        Owner = owner;
        Title = "Bỏ qua dạng bài";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Brushes.White;
        FontSize = 14;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock {
            Text = "Chọn dạng bài muốn bỏ qua. Lựa chọn được lưu trên máy và áp dụng cho khóa mở sau khi lưu. Với khóa đang chạy, hãy dừng rồi chạy lại để áp dụng.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20) });
        _pdf = new CheckBox { Content = "Bỏ qua nộp PDF / Peer-graded", IsChecked = settings.SkipPdf,
            Margin = new Thickness(0, 0, 0, 16) };
        _review = new CheckBox { Content = "Bỏ qua đánh giá chéo (Review peers)", IsChecked = settings.SkipReview,
            Margin = new Thickness(0, 0, 0, 20) };
        panel.Children.Add(_pdf);
        panel.Children.Add(_review);
        panel.Children.Add(new TextBlock { Text = "Bài bỏ qua vẫn chưa hoàn thành trên Coursera.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancel = new Button { Content = "Hủy", IsCancel = true, Margin = new Thickness(0, 0, 9, 0),
            Style = (Style)owner.FindResource(typeof(Button)) };
        var save = new Button { Content = "Lưu lựa chọn", IsDefault = true,
            Style = (Style)owner.FindResource("PrimaryButton") };
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);
        Content = new Border { Background = Brushes.White, Child = panel };
    }
}
