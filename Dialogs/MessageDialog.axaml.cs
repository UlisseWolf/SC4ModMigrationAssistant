using System.Threading.Tasks;
using Avalonia.Controls;

namespace SC4ModMigrationAssistant.Dialogs;

/// <summary>
/// Minimal, self-contained modal dialog used in place of WPF's <c>MessageBox</c>, which
/// Avalonia does not provide out of the box. Supports a single "OK" button (informational /
/// error messages) or "Yes" / "No" buttons (confirmations).
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    /// <summary>Shows an informational/error/warning message with a single OK button.</summary>
    public static Task ShowInfo(Window owner, string title, string message) =>
        ShowCore(owner, title, message, new[] { "OK" });

    /// <summary>Shows a Yes/No confirmation; returns true if the user chose "Yes".</summary>
    public static async Task<bool> ShowConfirm(Window owner, string title, string message)
    {
        string result = await ShowCore(owner, title, message, new[] { "Yes", "No" });
        return result == "Yes";
    }

    private static async Task<string> ShowCore(Window owner, string title, string message, string[] buttonLabels)
    {
        var dialog = new MessageDialog { Title = title };
        dialog.MessageText.Text = message;

        var tcs = new TaskCompletionSource<string>();
        bool resultSet = false;

        foreach (string label in buttonLabels)
        {
            var button = new Button { Content = label, MinWidth = 80 };
            button.Click += (_, _) =>
            {
                resultSet = true;
                tcs.TrySetResult(label);
                dialog.Close();
            };
            dialog.ButtonPanel.Children.Add(button);
        }

        dialog.Closed += (_, _) =>
        {
            if (!resultSet)
            {
                tcs.TrySetResult(string.Empty);
            }
        };

        await dialog.ShowDialog(owner);
        return await tcs.Task;
    }
}
