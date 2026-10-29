using System.Windows;
using System.Windows.Input;

namespace AutomationPlatform.AccountManager;

public partial class CourseLinkDialog : Window
{
    public CourseLinkDialog(string courseUrl)
    {
        InitializeComponent();
        CourseUrlTextBox.Text = courseUrl;
        Loaded += (_, _) =>
        {
            CourseUrlTextBox.Focus();
            CourseUrlTextBox.SelectAll();
        };
    }

    public string CourseUrl => CourseUrlTextBox.Text.Trim();

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CourseUrl))
        {
            MessageBox.Show(
                this,
                "Hãy nhập link khóa học.",
                "Thiếu link",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            CourseUrlTextBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        DialogResult = false;

    private void CourseUrlTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveButton_Click(sender, e);
            e.Handled = true;
        }
    }
}
