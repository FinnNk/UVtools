﻿using Avalonia.Markup.Xaml;
using UVtools.Core.Operations;
using UVtools.WPF.Extensions;

namespace UVtools.WPF.Controls.Tools
{
    public class ToolLayerReHeightControl : ToolControl
    {
        public OperationLayerReHeight Operation => BaseOperation as OperationLayerReHeight;

        public string CurrentLayers => $"Current layers: {App.SlicerFile.LayerCount} at {App.SlicerFile.LayerHeight}mm";

        public ToolLayerReHeightControl()
        {
            InitializeComponent();
            BaseOperation = new OperationLayerReHeight(SlicerFile);
            if (Operation.SelectedItem is null)
            {
                App.MainWindow.MessageBoxInfo("No valid configuration to be able to re-height.\n" +
                                              "As workaround clone first or last layer and try re run this tool.", "Not possible to re-height");
                CanRun = false;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
