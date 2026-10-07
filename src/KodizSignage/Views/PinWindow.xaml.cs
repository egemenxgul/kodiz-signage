using System.Windows;
using System.Windows.Input;
using KodizSignage.Core.Services;

namespace KodizSignage.Views;

/// <summary>Asks for the settings PIN (or, in "set" mode, for a new PIN twice).</summary>
public partial class PinWindow : Window
{
    private readonly Func<string, bool>? _verify;
    private readonly Func<string, string> _text;

    private PinWindow(string prompt, Func<string, bool>? verify, Func<string, string> text)
    {
        InitializeComponent();
        Services.ThemeService.Attach(this);
        _verify = verify;
        _text = text;
        PromptText.Text = prompt;
        if (verify is null)
        {
            ConfirmBox.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            Activate();
            PinBox.Focus();
        };
    }

    public string? EnteredPin { get; private set; }

    /// <summary>The box the number pad types into: the confirmation once the first PIN is complete.</summary>
    private System.Windows.Controls.PasswordBox Target =>
        ConfirmBox.Visibility == Visibility.Visible && (ConfirmBox.IsKeyboardFocused || PinBox.Password.Length >= 4 && !PinBox.IsKeyboardFocused)
            ? ConfirmBox
            : PinBox;

    private void Digit_Click(object sender, RoutedEventArgs e)
    {
        var box = Target;
        if (box.Password.Length < box.MaxLength && sender is System.Windows.Controls.Button { Content: string digit })
        {
            box.Password += digit;
        }

        box.Focus();
    }

    private void Backspace_Click(object sender, RoutedEventArgs e)
    {
        var box = Target;
        if (box.Password.Length > 0)
        {
            box.Password = box.Password[..^1];
        }

        box.Focus();
    }

    /// <summary>Asks for the existing PIN. Returns true when <paramref name="verify"/> accepts it.</summary>
    public static bool Ask(string prompt, Func<string, bool> verify, Func<string, string> text) =>
        new PinWindow(prompt, verify, text).ShowDialog() == true;

    /// <summary>Asks for a new PIN (entered twice). Returns null when cancelled.</summary>
    public static string? AskNew(string prompt, Func<string, string> text)
    {
        var window = new PinWindow(prompt, null, text);
        return window.ShowDialog() == true ? window.EnteredPin : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var pin = PinBox.Password;
        if (_verify is not null)
        {
            if (_verify(pin))
            {
                DialogResult = true;
                return;
            }

            ShowError(_text("Pin_Wrong"));
            PinBox.Clear();
            PinBox.Focus();
            return;
        }

        if (!PinHasher.IsValidPin(pin))
        {
            ShowError(_text("Pin_Invalid"));
            return;
        }

        if (pin != ConfirmBox.Password)
        {
            ShowError(_text("Pin_Mismatch"));
            ConfirmBox.Clear();
            ConfirmBox.Focus();
            return;
        }

        EnteredPin = pin;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        // Digits only.
        var isDigit = e.Key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9;
        if (!isDigit && e.Key is not (Key.Back or Key.Delete or Key.Tab or Key.Enter or Key.Escape or Key.Left or Key.Right))
        {
            e.Handled = true;
        }
    }
}
