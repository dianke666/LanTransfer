using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LanTransfer.App.ViewModels;

namespace LanTransfer.App.Views;

/// <summary>会话视图：支持拖放文件、精确局部选区原文复制（13.4）、粘贴到光标位置（13.5）。</summary>
public partial class ConversationView : UserControl
{
    private ConversationViewModel? _vm;

    public ConversationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.InsertIntoInputRequested -= OnInsertIntoInput;
        }

        _vm = e.NewValue as ConversationViewModel;
        if (_vm is not null)
        {
            _vm.InsertIntoInputRequested += OnInsertIntoInput;
        }
    }

    /// <summary>需求 13.5：将纯文本按码点原样插入光标位置或替换选中文字。</summary>
    private void OnInsertIntoInput(string text)
    {
        var box = ComposerBox;
        var current = box.Text;
        var start = box.SelectionStart;
        var len = box.SelectionLength;
        if (start < 0 || start > current.Length)
        {
            start = current.Length;
            len = 0;
        }

        var next = current[..start] + text + current[(start + len)..];
        box.Text = next;
        box.SelectionStart = start + text.Length;
        box.SelectionLength = 0;
        box.Focus();
    }

    private void OnComposerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_vm?.SendTextCommand.CanExecute(null) == true)
            {
                _vm.SendTextCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    /// <summary>拦截消息只读 TextBox 的 Ctrl+C 与右键复制，通过 TextDisplayMapper.OriginalRange 还原原文子串（13.4）。</summary>
    private void OnMessageTextBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        if (tb.CommandBindings.OfType<CommandBinding>().Any(b => b.Command == ApplicationCommands.Copy))
        {
            return;
        }

        tb.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, args) =>
        {
            if (tb.Tag is not TextBubbleVm bubble || _vm is null)
            {
                return;
            }

            if (tb.SelectionLength <= 0)
            {
                return;
            }

            var display = bubble.ActiveDisplay;
            var selStart = Math.Clamp(tb.SelectionStart, 0, display.Display.Length);
            var selEnd = Math.Clamp(selStart + tb.SelectionLength, selStart, display.Display.Length);
            var (origStart, origEnd) = display.OriginalRange(selStart, selEnd);
            var origSlice = bubble.RawText[origStart..origEnd];
            _ = _vm.CopyRawTextAsync(origSlice);
            args.Handled = true;
        }));
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (_vm is not null)
            {
                _vm.IsDragOver = true;
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.IsDragOver = false;
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.IsDragOver = false;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                _vm.AddFilePaths(files);
            }
        }

        e.Handled = true;
    }
}
