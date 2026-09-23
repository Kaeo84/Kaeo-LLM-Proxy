using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Kaeo.LlmProxy;

/// <summary>
/// A compact multi-select filter that behaves like a drop-down field: the button shows a summary of
/// the current selection and clicking it opens a <see cref="CheckedListBox"/> listing every option
/// with a checkbox, so all options are visible at once.
/// </summary>
/// <remarks>
/// Built on a stock <see cref="Button"/> and <see cref="ToolStripDropDown"/> rather than custom
/// painting so it inherits the normal themed button rendering, focus cues, and accessibility. The
/// hosted list is a real child control, so checkboxes, keyboard navigation, and theming all behave
/// normally.
/// </remarks>
internal sealed class MultiSelectDropDown : Button
{
    /// <summary>
    /// Raised after the checked set changes through user interaction (not through
    /// <see cref="SetSelection"/>), so callers can refresh without reacting to their own writes.
    /// </summary>
    internal event EventHandler? SelectionChanged;

    private readonly ToolStripDropDown _dropDown = new()
    {
        AutoSize = true,
        DropShadowEnabled = true,
        Padding = Padding.Empty,
    };

    private readonly CheckedListBox _list = new()
    {
        CheckOnClick = true,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        SelectionMode = SelectionMode.One,
    };

    /// <summary>Suppresses <see cref="SelectionChanged"/> while the set changes programmatically.</summary>
    private bool _suppressEvents;

    internal MultiSelectDropDown()
    {
        UseVisualStyleBackColor = true;

        _dropDown.Items.Add(new ToolStripControlHost(_list) { Margin = Padding.Empty, Padding = Padding.Empty });

        // ItemCheck precedes the state change, so the refresh is posted to run after it is applied.
        // Guarded on both suppression and handle existence: adding an item with an initial check
        // state raises this event, and posting before the handle exists throws.
        _list.ItemCheck += (_, _) =>
        {
            if (_suppressEvents)
                return;

            if (IsHandleCreated)
                BeginInvoke(AfterUserCheck);
            else
                AfterUserCheck();
        };
    }

    /// <summary>The currently checked options, in the order they were added.</summary>
    internal IReadOnlyList<string> SelectedItems =>
        [.. _list.CheckedItems.Cast<object>().Select(o => o.ToString() ?? string.Empty)];

    /// <summary>Adds an option; <paramref name="isChecked"/> sets its initial state.</summary>
    internal void AddItem(string text, bool isChecked)
    {
        _suppressEvents = true;
        try
        {
            _list.Items.Add(text, isChecked);
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateSummary();
    }

    /// <summary>
    /// Replaces the checked set without raising <see cref="SelectionChanged"/>, for loading state.
    /// </summary>
    internal void SetSelection(IEnumerable<string> checkedItems)
    {
        HashSet<string> wanted = [.. checkedItems];

        _suppressEvents = true;
        try
        {
            for (int i = 0; i < _list.Items.Count; i++)
            {
                string text = _list.Items[i]?.ToString() ?? string.Empty;
                _list.SetItemChecked(i, wanted.Contains(text));
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateSummary();
    }

    /// <summary>Refreshes the button caption after the list's state has settled.</summary>
    private void AfterUserCheck()
    {
        UpdateSummary();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Summarises the checked set on the closed button. All-checked is the common case and reads
    /// best as "All"; an empty set reads "None"; otherwise the names are listed.
    /// </summary>
    private void UpdateSummary()
    {
        IReadOnlyList<string> checkedItems = SelectedItems;

        Text = checkedItems.Count == 0
            ? "None"
            : checkedItems.Count == _list.Items.Count
                ? "All"
                : string.Join(", ", checkedItems);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ToggleDropDown();
    }

    private void ToggleDropDown()
    {
        if (_dropDown.Visible)
        {
            _dropDown.Close();
            return;
        }

        // Show every option without scrolling: the list takes the greater of the button width and
        // its natural width, and its full preferred height.
        _list.Width = Math.Max(Width, 160);
        _list.Height = _list.PreferredHeight;

        _dropDown.Show(this, 0, Height);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateSummary();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dropDown.Dispose();
            _list.Dispose();
        }

        base.Dispose(disposing);
    }
}