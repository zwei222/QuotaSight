using Avalonia.Controls;
using Avalonia.Input;

namespace QuotaSight.UI.Controls;

/// <summary>
/// ComboBox with the keyboard affordances Avalonia 11 provided while the dropdown is open:
/// Up/Down move the selection and Enter/Space commit and close it. Avalonia 12 removed those
/// branches from ComboBox.OnKeyDown, which leaves keyboard users unable to change or confirm
/// a selection once an item is highlighted, so this subclass restores them. Visuals, templates
/// and pointer behavior are inherited unchanged.
/// </summary>
public sealed class KeyboardComboBox : ComboBox
{
    // Without this the theme's ComboBox template and the app's ComboBox styles do not apply to the subclass.
    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Handled || !IsDropDownOpen || IsEditable || e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Alt+Up/Down toggles the dropdown in the base class.
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                e.Handled = MoveSelection(1);
                break;
            case Key.Up:
                e.Handled = MoveSelection(-1);
                break;
            case Key.Enter or Key.Space:
                SetCurrentValue(IsDropDownOpenProperty, false);
                e.Handled = true;
                break;
        }

        // Everything else (Escape, Home/End, type-ahead, ...) keeps the base behavior.
        if (!e.Handled) base.OnKeyDown(e);
    }

    private bool MoveSelection(int delta)
    {
        var target = SelectedIndex + delta;
        if (target < 0 || target >= ItemCount) return false;
        SelectedIndex = target;
        return true;
    }
}
