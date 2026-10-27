using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse_Conversion;
using FModel.ViewModels;

namespace FModel.Automation;

internal sealed class AssetOperationScope : IDisposable
{
    private static readonly AsyncLocal<AssetOperationScope> Slot = new();
    public static AssetOperationScope Current => Slot.Value;
    private readonly AssetOperationScope _previous;

    public TabItem Tab { get; }
    public ExportSession Session { get; } = new();
    public List<string> Files { get; } = [];
    public List<string> Errors { get; } = [];
    public string Document { get; private set; }
    public Action<string, string, byte[]> AudioCaptured { get; set; }

    public AssetOperationScope(GameFile file, bool captureDocument = false)
    {
        _previous = Slot.Value;
        Tab = Application.Current.Dispatcher.Invoke(() => new TabItem(file));
        if (captureDocument) Tab.DocumentCaptured = text => Document = text;
        Tab.FileSaved = path => Files.Add(path.Replace('\\', '/'));
        Slot.Value = this;
    }

    public void Dispose() => Slot.Value = _previous;
}
