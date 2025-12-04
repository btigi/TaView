using ii.CompleteDestruction;
using ii.CompleteDestruction.Model.Hpi;
using MahApps.Metro.Controls;
using NAudio.Wave;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Taview
{
    public partial class MainWindow : Window
    {
        private Dictionary<TreeViewItem, HpiFileEntry> _filePathMap = new();
        private string? _currentHpiFilePath;
        private HpiArchive? _currentArchive;
        private HpiProcessor? _currentHpiProcessor;
        private string? _lastOpenFolder;
        private string? _lastSaveFolder;

        // File type filter state
        private HashSet<string> _checkedExtensions = new(StringComparer.OrdinalIgnoreCase);

        // GAF navigation state
        private List<GafImageEntry>? _currentGafEntries;
        private int _currentGafEntryIndex = 0;
        private int _currentGafFrameIndex = 0;
        private byte[]? _currentGafFileData;

        // Current file view state
        private byte[]? _currentFileData;
        private HpiFileEntry? _currentFileEntry;
        private bool _isHexView = false;

        // Audio playback state
        private DispatcherTimer? _audioPositionTimer;
        private bool _isDraggingAudioSlider = false;
        private WaveOutEvent? _waveOut;
        private WaveStream? _waveStream;
        private TimeSpan _audioTotalDuration = TimeSpan.Zero;

        public MainWindow(string? filePath = null)
        {
            InitializeComponent();

            // Set up title bar theming
            ThemeManager.InitializeWindow(this);

            // Load file if provided via command line
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    LoadHpiFile(filePath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening HPI file:\n{ex.Message}",
                                   "Error",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Error);
                }
            }
        }

        private void OpenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Archive Files (*.hpi;*.ccx;*.gp3;*.ufo)|*.hpi;*.ccx;*.gp3;*.ufo|HPI Files (*.hpi)|*.hpi|CCX Files (*.ccx)|*.ccx|GP3 Files (*.gp3)|*.gp3|UFO Files (*.ufo)|*.ufo|All Files (*.*)|*.*",
                Title = "Open Archive File"
            };

            // Set initial directory from last used open folder
            if (!string.IsNullOrEmpty(_lastOpenFolder) && Directory.Exists(_lastOpenFolder))
            {
                dialog.InitialDirectory = _lastOpenFolder;
            }

            if (dialog.ShowDialog() == true)
            {
                // Save the folder for next time
                var folder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    _lastOpenFolder = folder;
                }

                try
                {
                    LoadHpiFile(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error opening HPI file:\n{ex.Message}",
                                   "Error",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Error);
                }
            }
        }

        private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var aboutWindow = new AboutWindow
            {
                Owner = this
            };
            aboutWindow.ShowDialog();
        }

        private void OptionsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var optionsWindow = new OptionsWindow
            {
                Owner = this
            };
            optionsWindow.ShowDialog();
        }

        private void ExtractAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentHpiFilePath))
            {
                MessageBox.Show("No HPI file is currently open.",
                               "Information",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
                return;
            }

            // Prompt user for folder
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select folder to extract files to"
            };

            // Set initial directory from last used save folder
            if (!string.IsNullOrEmpty(_lastSaveFolder) && Directory.Exists(_lastSaveFolder))
            {
                dialog.InitialDirectory = _lastSaveFolder;
            }

            if (dialog.ShowDialog() != true)
                return;

            var targetFolder = dialog.FolderName;
            _lastSaveFolder = targetFolder;

            try
            {
                // Read all files with content
                var processor = new HpiProcessor();
                var archive = processor.Read(_currentHpiFilePath, false);

                if (archive == null || archive.Files == null || archive.Files.Count == 0)
                {
                    MessageBox.Show("No files found in HPI archive.",
                                   "Information",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Information);
                    return;
                }

                int extractedCount = 0;
                int errorCount = 0;

                foreach (var file in archive.Files)
                {
                    try
                    {
                        var targetPath = Path.Combine(targetFolder, file.RelativePath);
                        var targetDir = Path.GetDirectoryName(targetPath);

                        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                        {
                            Directory.CreateDirectory(targetDir);
                        }

                        if (file.Data != null)
                        {
                            File.WriteAllBytes(targetPath, file.Data);
                            extractedCount++;
                        }
                        else
                        {
                            errorCount++;
                        }
                    }
                    catch
                    {
                        errorCount++;
                    }
                }

                var message = $"Extracted {extractedCount} files to:\n{targetFolder}";
                if (errorCount > 0)
                {
                    message += $"\n\n{errorCount} files could not be extracted.";
                }

                MessageBox.Show(message,
                               "Extract All Complete",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error extracting files:\n{ex.Message}",
                               "Error",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
            }
        }

        private void SortAlphabeticallyButton_Click(object sender, RoutedEventArgs e)
        {
            // Rebuild tree with the new sort setting (no need to reload from disk)
            if (!string.IsNullOrEmpty(_currentHpiFilePath) && _currentArchive != null)
            {
                BuildTreeView();
            }
        }

        private void LoadHpiFile(string filePath)
        {
            _currentHpiFilePath = filePath;
            ContentTextBox.Text = string.Empty;
            FileInfoTextBlock.Text = $"File: {Path.GetFileName(filePath)}";
            Title = $"{Path.GetFileName(filePath)} - TAView";

            _currentHpiProcessor = new HpiProcessor();
            var archive = _currentHpiProcessor.Read(filePath);
            _currentArchive = archive;

            if (archive == null || archive.Files == null || archive.Files.Count == 0)
            {
                MessageBox.Show("No files found in HPI archive.",
                               "Information",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
                FilterDropdownButton.IsEnabled = false;
                return;
            }

            // Build file type filter
            BuildFileTypeFilter();

            // Enable Extract All menu item
            ExtractAllMenuItem.IsEnabled = true;

            BuildTreeView();
        }

        private void BuildFileTypeFilter()
        {
            if (_currentArchive == null)
                return;

            FilterCheckboxPanel.Children.Clear();
            _checkedExtensions.Clear();

            // Get all unique extensions, sorted alphabetically
            var extensions = _currentArchive.Files
                .Select(f => Path.GetExtension(f.RelativePath).ToUpperInvariant())
                .Where(ext => !string.IsNullOrEmpty(ext))
                .Distinct()
                .OrderBy(ext => ext, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Add [All] checkbox at the top
            var allCheckBox = new CheckBox
            {
                Content = "[All]",
                IsChecked = true,
                Margin = new Thickness(2),
                Tag = "ALL",
                FontWeight = FontWeights.Bold
            };
            allCheckBox.SetResourceReference(CheckBox.ForegroundProperty, "MahApps.Brushes.ThemeForeground");
            allCheckBox.SetResourceReference(CheckBoxHelper.CheckGlyphForegroundCheckedProperty, "MahApps.Brushes.ThemeForeground");
            allCheckBox.SetResourceReference(CheckBoxHelper.CheckGlyphForegroundIndeterminateProperty, "MahApps.Brushes.ThemeForeground");
            allCheckBox.Checked += AllFilterCheckBox_Checked;
            allCheckBox.Unchecked += AllFilterCheckBox_Unchecked;
            FilterCheckboxPanel.Children.Add(allCheckBox);

            // Add separator
            FilterCheckboxPanel.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 2) });

            // Check all extensions by default
            foreach (var ext in extensions)
            {
                _checkedExtensions.Add(ext);

                var checkBox = new CheckBox
                {
                    Content = ext,
                    IsChecked = true,
                    Margin = new Thickness(2),
                    Tag = ext
                };
                checkBox.SetResourceReference(CheckBox.ForegroundProperty, "MahApps.Brushes.ThemeForeground");
                checkBox.SetResourceReference(CheckBoxHelper.CheckGlyphForegroundCheckedProperty, "MahApps.Brushes.ThemeForeground");
                checkBox.Checked += FilterCheckBox_Changed;
                checkBox.Unchecked += FilterCheckBox_Changed;

                FilterCheckboxPanel.Children.Add(checkBox);
            }

            FilterDropdownButton.IsEnabled = extensions.Count > 0;
        }

        private void AllFilterCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            SetAllFilterCheckboxes(true);
        }

        private void AllFilterCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAllFilterCheckboxes(false);
        }

        private void SetAllFilterCheckboxes(bool isChecked)
        {
            foreach (var child in FilterCheckboxPanel.Children)
            {
                if (child is CheckBox checkBox && checkBox.Tag is string tag && tag != "ALL")
                {
                    // Temporarily unhook events to avoid multiple tree rebuilds
                    checkBox.Checked -= FilterCheckBox_Changed;
                    checkBox.Unchecked -= FilterCheckBox_Changed;

                    checkBox.IsChecked = isChecked;

                    if (isChecked)
                    {
                        _checkedExtensions.Add(tag);
                    }
                    else
                    {
                        _checkedExtensions.Remove(tag);
                    }

                    // Rehook events
                    checkBox.Checked += FilterCheckBox_Changed;
                    checkBox.Unchecked += FilterCheckBox_Changed;
                }
            }

            BuildTreeView();
        }

        private void FilterCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.Tag is string ext)
            {
                if (checkBox.IsChecked == true)
                {
                    _checkedExtensions.Add(ext);
                }
                else
                {
                    _checkedExtensions.Remove(ext);
                }

                UpdateAllCheckboxState();
                BuildTreeView();
            }
        }

        private void UpdateAllCheckboxState()
        {
            // Find the [All] checkbox and update its state
            foreach (var child in FilterCheckboxPanel.Children)
            {
                if (child is CheckBox checkBox && checkBox.Tag is string tag && tag == "ALL")
                {
                    // Temporarily unhook events
                    checkBox.Checked -= AllFilterCheckBox_Checked;
                    checkBox.Unchecked -= AllFilterCheckBox_Unchecked;

                    // Count total extension checkboxes
                    int totalCount = 0;
                    int checkedCount = 0;
                    foreach (var c in FilterCheckboxPanel.Children)
                    {
                        if (c is CheckBox cb && cb.Tag is string t && t != "ALL")
                        {
                            totalCount++;
                            if (cb.IsChecked == true)
                                checkedCount++;
                        }
                    }

                    if (checkedCount == 0)
                        checkBox.IsChecked = false;
                    else if (checkedCount == totalCount)
                        checkBox.IsChecked = true;
                    else
                        checkBox.IsChecked = null; // Indeterminate

                    // Rehook events
                    checkBox.Checked += AllFilterCheckBox_Checked;
                    checkBox.Unchecked += AllFilterCheckBox_Unchecked;
                    break;
                }
            }
        }

        private void BuildTreeView()
        {
            if (_currentArchive == null || _currentHpiFilePath == null)
                return;

            FileTreeView.Items.Clear();
            _filePathMap.Clear();

            var sortAlphabetically = SortAlphabeticallyButton.IsChecked == true;

            // Build tree structure
            var rootNode = new TreeViewItem
            {
                Header = Path.GetFileName(_currentHpiFilePath),
                Tag = "ROOT"
            };

            var directoryMap = new Dictionary<string, TreeViewItem>();

            // Get files, optionally sorted and filtered
            var files = sortAlphabetically
                ? _currentArchive.Files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList()
                : _currentArchive.Files.ToList();

            // Filter by checked extensions
            files = files.Where(f =>
            {
                var ext = Path.GetExtension(f.RelativePath).ToUpperInvariant();
                return string.IsNullOrEmpty(ext) || _checkedExtensions.Contains(ext);
            }).ToList();

            foreach (var file in files)
            {
                var parts = file.RelativePath.Split('\\', '/');
                var currentPath = string.Empty;
                TreeViewItem? parentNode = rootNode;

                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}\\{part}";

                    if (i == parts.Length - 1)
                    {
                        // Note that this is a file
                        var fileNode = new TreeViewItem
                        {
                            Header = part,
                            Tag = file
                        };

                        // Add context menu for files
                        var contextMenu = new ContextMenu();
                        var extractMenuItem = new MenuItem
                        {
                            Header = "Extract",
                            Tag = fileNode
                        };
                        extractMenuItem.Click += ExtractMenuItem_Click;
                        contextMenu.Items.Add(extractMenuItem);
                        fileNode.ContextMenu = contextMenu;

                        parentNode.Items.Add(fileNode);
                        _filePathMap[fileNode] = file;
                    }
                    else
                    {
                        if (!directoryMap.ContainsKey(currentPath))
                        {
                            var dirNode = new TreeViewItem
                            {
                                Header = part,
                                Tag = currentPath
                            };
                            parentNode.Items.Add(dirNode);
                            directoryMap[currentPath] = dirNode;
                        }
                        parentNode = directoryMap[currentPath];
                    }
                }
            }

            FileTreeView.Items.Add(rootNode);
            rootNode.IsExpanded = true;

            ScrollTreeViewToTop();
        }

        private void ScrollTreeViewToTop()
        {
            var sv = GetScrollViewer(FileTreeView);
            if (sv != null)
                sv.ScrollToTop();
        }

        public static ScrollViewer GetScrollViewer(DependencyObject o)
        {
            if (o is ScrollViewer sv)
                return sv;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
            {
                var child = VisualTreeHelper.GetChild(o, i);
                var result = GetScrollViewer(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void FileTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeViewItem selectedItem)
            {
                if (_filePathMap.TryGetValue(selectedItem, out var fileEntry))
                {
                    DisplayFileContent(fileEntry);
                }
                else
                {
                    ContentTextBox.Text = string.Empty;
                    FileInfoTextBlock.Text = $"Directory: {selectedItem.Header}";
                    TextScrollViewer.ScrollToHome();
                }
            }
        }

        private void ExtractMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem menuItem || menuItem.Tag is not TreeViewItem treeViewItem)
                return;

            if (!_filePathMap.TryGetValue(treeViewItem, out var fileEntry))
            {
                MessageBox.Show("Please select a file to extract.",
                               "Information",
                               MessageBoxButton.OK,
                               MessageBoxImage.Information);
                return;
            }

            var fileData = GetFileData(fileEntry);
            if (fileData == null || fileData.Length == 0)
            {
                MessageBox.Show("Could not read file data from archive.",
                               "Error",
                               MessageBoxButton.OK,
                               MessageBoxImage.Error);
                return;
            }

            var fileName = Path.GetFileName(fileEntry.RelativePath);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName,
                Filter = "All Files (*.*)|*.*",
                Title = "Extract File"
            };

            // Set initial directory from last used save folder
            if (!string.IsNullOrEmpty(_lastSaveFolder) && Directory.Exists(_lastSaveFolder))
            {
                dialog.InitialDirectory = _lastSaveFolder;
            }

            if (dialog.ShowDialog() == true)
            {
                // Save the folder for next time
                var folder = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(folder))
                {
                    _lastSaveFolder = folder;
                }

                try
                {
                    File.WriteAllBytes(dialog.FileName, fileData);
                    MessageBox.Show($"File extracted successfully to:\n{dialog.FileName}",
                                   "Success",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error extracting file:\n{ex.Message}",
                                   "Error",
                                   MessageBoxButton.OK,
                                   MessageBoxImage.Error);
                }
            }
        }

        private void ViewToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_currentFileEntry == null || _currentFileData == null)
                return;

            _isHexView = HexViewButton.IsChecked == true;

            if (_isHexView)
            {
                ShowHexView();
            }
            else
            {
                ShowPreviewView();
            }
        }

        private void ShowHexView()
        {
            if (_currentFileData == null || _currentFileEntry == null)
                return;

            StopAudio();

            TextScrollViewer.Visibility = Visibility.Visible;
            ImageContentGrid.Visibility = Visibility.Collapsed;
            AudioContentGrid.Visibility = Visibility.Collapsed;

            ContentTextBox.Text = FormatHexDump(_currentFileData, _currentFileEntry.RelativePath);
            TextScrollViewer.ScrollToHome();
        }

        private void ShowPreviewView()
        {
            if (_currentFileEntry == null)
                return;

            // Re-display the file content in preview mode
            _isHexView = false;
            DisplayFileContentInternal(_currentFileEntry, _currentFileData);
        }

        private void DisplayFileContent(HpiFileEntry fileEntry)
        {
            try
            {
                var filePath = fileEntry.RelativePath;
                FileInfoTextBlock.Text = $"File: {filePath}";

                if (_currentHpiFilePath == null || _currentArchive == null)
                {
                    ContentTextBox.Text = "No HPI file loaded.";
                    ViewTogglePanel.Visibility = Visibility.Collapsed;
                    return;
                }

                byte[]? fileData = GetFileData(fileEntry);

                if (fileData == null || fileData.Length == 0)
                {
                    ContentTextBox.Text = "Could not read file data from HPI archive.";
                    ViewTogglePanel.Visibility = Visibility.Collapsed;
                    return;
                }

                // Store current file data for view toggling
                _currentFileData = fileData;
                _currentFileEntry = fileEntry;

                // Show view toggle (maintain current view selection)
                ViewTogglePanel.Visibility = Visibility.Visible;

                if (_isHexView)
                {
                    ShowHexView();
                }
                else
                {
                    DisplayFileContentInternal(fileEntry, fileData);
                }
            }
            catch (Exception ex)
            {
                ContentTextBox.Text = $"Error reading file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private void DisplayFileContentInternal(HpiFileEntry fileEntry, byte[]? fileData)
        {
            try
            {
                var filePath = fileEntry.RelativePath;

                if (fileData == null || fileData.Length == 0)
                {
                    ContentTextBox.Text = "Could not read file data from archive.";
                    return;
                }

                var extension = Path.GetExtension(filePath).ToLower();

                if (extension == ".wav")
                {
                    DisplayAudio(fileData, extension);
                    return;
                }

                if (extension == ".pcx" || extension == ".bmp" || extension == ".gaf" || extension == ".tnt")
                {
                    DisplayImage(fileData, extension);
                    return;
                }

                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;

                try
                {
                    string content;

                    switch (extension)
                    {

                        case ".tdf":
                        case ".fbi":
                        case ".gui":
                        case ".ota":
                        case ".txt":
                        case ".ini":
                        case ".cfg":
                        case ".bos":
                            content = System.Text.Encoding.UTF8.GetString(fileData);
                            break;

                        default:
                            content = FormatHexDump(fileData, filePath);
                            break;
                    }

                    ContentTextBox.Text = content;

                    TextScrollViewer.ScrollToHome();
                }
                catch (Exception ex)
                {
                    ContentTextBox.Text = $"Error processing file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                    TextScrollViewer.ScrollToHome();
                }
            }
            catch (Exception ex)
            {
                ContentTextBox.Text = $"Error reading file:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private byte[]? GetFileData(HpiFileEntry fileEntry)
        {
            if (_currentHpiProcessor == null)
            {
                return null;
            }

            try
            {
                var fileData = _currentHpiProcessor.Extract(fileEntry.RelativePath);
                return fileData;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting file {fileEntry.RelativePath}: {ex.Message}");
                return null;
            }
        }


        private void DisplayImage(byte[] imageData, string extension)
        {
            try
            {
                ContentImage.Source = null;

                if (ZoomSlider != null)
                {
                    ZoomSlider.Value = 1.0;
                }
                if (ImageScaleTransform != null)
                {
                    ImageScaleTransform.ScaleX = 1.0;
                    ImageScaleTransform.ScaleY = 1.0;
                }
                if (ZoomValueTextBlock != null)
                {
                    ZoomValueTextBlock.Text = "100%";
                }

                TextScrollViewer.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                ImageContentGrid.Visibility = Visibility.Visible;

                if (extension != ".gaf")
                {
                    _currentGafEntries = null;
                    _currentGafFileData = null;
                    PreviousFrameButton.Visibility = Visibility.Collapsed;
                    NextFrameButton.Visibility = Visibility.Collapsed;
                }

                BitmapSource? bitmapImage = null;
                string? imageInfo = null;

                if (extension == ".pcx")
                {
                    var pcxProcessor = new PcxConverter();
                    var pcxImage = pcxProcessor.Parse(imageData);

                    bitmapImage = ConvertPcxToBitmapImage(pcxImage);
                    imageInfo = $"PCX Image: {pcxImage?.Width ?? 0}x{pcxImage?.Height ?? 0}";
                }
                else if (extension == ".bmp")
                {
                    // Stream will be disposed when BitmapImage is garbage collected
                    var memoryStream = new MemoryStream(imageData);
                    var bmpImage = new BitmapImage();
                    bmpImage.BeginInit();
                    bmpImage.StreamSource = memoryStream;
                    bmpImage.CacheOption = BitmapCacheOption.OnLoad;
                    bmpImage.EndInit();
                    bmpImage.Freeze();
                    bitmapImage = bmpImage;
                    imageInfo = $"BMP Image";
                }
                else if (extension == ".gaf")
                {
                    _currentGafFileData = imageData;

                    var gafProcessor = new GafProcessor();
                    var gafEntries = gafProcessor.Read(imageData);
                    _currentGafEntries = gafEntries;
                    _currentGafEntryIndex = 0;
                    _currentGafFrameIndex = 0;

                    DisplayGafFrame();
                    return;
                }
                else if (extension == ".tnt")
                {
                    var tntProcessor = new TntProcessor();

                    // Assume PALETTE.PAL in is exe directory
                    var exeDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    var palettePath = Path.Combine(exeDirectory ?? "", "PALETTE.PAL");

                    if (!File.Exists(palettePath))
                    {
                        imageInfo = "TNT File: PALETTE.PAL not found in exe directory";
                    }
                    else
                    {
                        try
                        {
                            var paletteBytes = File.ReadAllBytes(palettePath);
                            var palette = new ii.CompleteDestruction.Model.Tnt.TaPalette(paletteBytes);

                            var tntFile = tntProcessor.Read(imageData, palette);

                            if (tntFile != null && tntFile.Map != null)
                            {
                                if (tntFile.Map is Image<Rgba32> rgbaMap)
                                {
                                    bitmapImage = ConvertImageSharpRgba32ToBitmapImage(rgbaMap);
                                }
                                else
                                {
                                    bitmapImage = ConvertImageSharpToBitmapImage(tntFile.Map);
                                }
                                imageInfo = $"TNT Map: {tntFile.Map.Width}x{tntFile.Map.Height}";
                            }
                            else
                            {
                                imageInfo = "TNT File: Map not available";
                            }
                        }
                        catch (Exception ex)
                        {
                            imageInfo = $"TNT File: Error loading - {ex.Message}";
                        }
                    }
                }

                if (bitmapImage != null)
                {
                    ContentImage.Source = bitmapImage;
                    if (!string.IsNullOrEmpty(imageInfo))
                    {
                        FileInfoTextBlock.Text = imageInfo;
                    }
                }
                else
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = $"Could not display image: {extension}\n\n{imageInfo ?? ""}";
                    TextScrollViewer.ScrollToHome();
                }
            }
            catch (Exception ex)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error displaying image:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();
            }
        }

        private void DisplayGafFrame()
        {
            ContentImage.Source = null;

            if (ZoomSlider != null)
            {
                ZoomSlider.Value = 1.0;
            }
            if (ImageScaleTransform != null)
            {
                ImageScaleTransform.ScaleX = 1.0;
                ImageScaleTransform.ScaleY = 1.0;
            }
            if (ZoomValueTextBlock != null)
            {
                ZoomValueTextBlock.Text = "100%";
            }

            if (_currentGafEntries == null || _currentGafEntries.Count == 0)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = "GAF: No entries found";
                TextScrollViewer.ScrollToHome();
                return;
            }

            if (_currentGafEntryIndex >= _currentGafEntries.Count)
                _currentGafEntryIndex = 0;

            var currentEntry = _currentGafEntries[_currentGafEntryIndex];

            if (currentEntry.Frames == null || currentEntry.Frames.Count == 0)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"GAF: {currentEntry.Name}\nNo frames found";
                TextScrollViewer.ScrollToHome();
                PreviousFrameButton.Visibility = Visibility.Collapsed;
                NextFrameButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (_currentGafFrameIndex >= currentEntry.Frames.Count)
                _currentGafFrameIndex = 0;

            var currentFrame = currentEntry.Frames[_currentGafFrameIndex];

            if (currentFrame.Image != null)
            {
                BitmapSource? bitmapImage = null;

                try
                {
                    if (currentFrame.Image is Image<Rgba32> rgbaImage)
                    {
                        bitmapImage = ConvertImageSharpRgba32ToBitmapImage(rgbaImage);
                    }
                    else
                    {
                        bitmapImage = ConvertImageSharpToBitmapImage(currentFrame.Image);
                    }
                }
                catch (Exception ex)
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = $"Error converting GAF frame:\n{ex.Message}";
                    TextScrollViewer.ScrollToHome();
                    return;
                }

                if (bitmapImage != null)
                {
                    TextScrollViewer.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    ImageContentGrid.Visibility = Visibility.Visible;

                    ContentImage.Source = bitmapImage;

                    var totalFrames = currentEntry.Frames.Count;
                    var totalEntries = _currentGafEntries.Count;
                    FileInfoTextBlock.Text = $"GAF: {currentEntry.Name}\nEntry {_currentGafEntryIndex + 1}/{totalEntries}, Frame {_currentGafFrameIndex + 1}/{totalFrames}\nSize: {currentFrame.Image.Width}x{currentFrame.Image.Height}, Offset: ({currentFrame.XOffset}, {currentFrame.YOffset}), Compressed: {currentFrame.UseCompression}";

                    var hasMultipleFrames = totalFrames > 1;
                    var hasMultipleEntries = totalEntries > 1;
                    var showNavigation = hasMultipleFrames || hasMultipleEntries;

                    PreviousFrameButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                    NextFrameButton.Visibility = showNavigation ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    TextScrollViewer.Visibility = Visibility.Visible;
                    ImageContentGrid.Visibility = Visibility.Collapsed;
                    AudioContentGrid.Visibility = Visibility.Collapsed;
                    ContentTextBox.Text = "GAF: Could not convert frame to image";
                    TextScrollViewer.ScrollToHome();
                }
            }
        }

        private void PreviousFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGafEntries == null || _currentGafEntries.Count == 0)
                return;

            var currentEntry = _currentGafEntries[_currentGafEntryIndex];
            _currentGafFrameIndex--;
            if (_currentGafFrameIndex < 0)
            {
                _currentGafEntryIndex--;
                if (_currentGafEntryIndex < 0)
                {
                    _currentGafEntryIndex = _currentGafEntries.Count - 1;
                }

                currentEntry = _currentGafEntries[_currentGafEntryIndex];
                _currentGafFrameIndex = currentEntry.Frames?.Count - 1 ?? 0;
            }

            DisplayGafFrame();
        }

        private void NextFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGafEntries == null || _currentGafEntries.Count == 0)
                return;

            var currentEntry = _currentGafEntries[_currentGafEntryIndex];
            _currentGafFrameIndex++;
            if (currentEntry.Frames == null || _currentGafFrameIndex >= currentEntry.Frames.Count)
            {
                _currentGafEntryIndex++;
                if (_currentGafEntryIndex >= _currentGafEntries.Count)
                {
                    _currentGafEntryIndex = 0;
                }

                _currentGafFrameIndex = 0;
            }

            DisplayGafFrame();
        }

        private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ImageScaleTransform != null)
            {
                ImageScaleTransform.ScaleX = e.NewValue;
                ImageScaleTransform.ScaleY = e.NewValue;

                if (ZoomValueTextBlock != null)
                {
                    ZoomValueTextBlock.Text = $"{(int)(e.NewValue * 100)}%";
                }
            }
        }

        private BitmapImage? ConvertPcxToBitmapImage(object? pcxImage)
        {
            if (pcxImage == null) return null;

            try
            {
                if (pcxImage is Image<Bgra32> imageSharpImage)
                {
                    return ConvertImageSharpToBitmapImage(imageSharpImage);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error converting PCX image: {ex.Message}");
            }

            return null;
        }

        private BitmapImage ConvertImageSharpToBitmapImage(Image<Bgra32> image)
        {
            using (var memoryStream = new MemoryStream())
            {
                image.Save(memoryStream, new PngEncoder());

                memoryStream.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memoryStream;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }

        private BitmapSource ConvertImageSharpRgba32ToBitmapImage(Image<Rgba32> image)
        {
            using var bgraImage = image.CloneAs<Bgra32>();

            var width = bgraImage.Width;
            var height = bgraImage.Height;
            var dpi = 96d;
            var stride = width * 4;

            var pixelStructs = new Bgra32[width * height];
            bgraImage.CopyPixelDataTo(pixelStructs);

            var pixelBytes = MemoryMarshal.AsBytes(pixelStructs.AsSpan()).ToArray();

            var bitmap = BitmapSource.Create(
                width,
                height,
                dpi,
                dpi,
                PixelFormats.Bgra32,
                null,
                pixelBytes,
                stride);

            bitmap.Freeze();
            return bitmap;
        }

        private BitmapImage ConvertImageSharpToBitmapImage(SixLabors.ImageSharp.Image image)
        {
            using (var memoryStream = new MemoryStream())
            {
                image.Save(memoryStream, new PngEncoder());

                memoryStream.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memoryStream;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }

        private void DisplayAudio(byte[] audioData, string extension)
        {
            try
            {
                StopAudio();

                TextScrollViewer.Visibility = Visibility.Collapsed;
                ImageContentGrid.Visibility = Visibility.Collapsed;
                AudioContentGrid.Visibility = Visibility.Visible;

                PlayButton.IsEnabled = false;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
                AudioPositionSlider.Value = 0;
                CurrentTimeTextBlock.Text = "00:00";
                TotalTimeTextBlock.Text = "00:00";

                var memoryStream = new MemoryStream(audioData);
                _waveStream = new WaveFileReader(memoryStream);
                _audioTotalDuration = _waveStream.TotalTime;

                _waveOut = new WaveOutEvent();
                _waveOut.Init(_waveStream);
                _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;

                TotalTimeTextBlock.Text = FormatTimeSpan(_audioTotalDuration);
                AudioPositionSlider.Maximum = _audioTotalDuration.TotalSeconds;

                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;

                FileInfoTextBlock.Text = $"WAV Audio File ({audioData.Length} bytes)";
            }
            catch (Exception ex)
            {
                TextScrollViewer.Visibility = Visibility.Visible;
                AudioContentGrid.Visibility = Visibility.Collapsed;
                ContentTextBox.Text = $"Error loading audio:\n{ex.Message}\n\nStack trace:\n{ex.StackTrace}";
                TextScrollViewer.ScrollToHome();

                StopAudio();
                PlayButton.IsEnabled = false;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
            }
        }

        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StopAudioPositionTimer();

                if (_waveStream != null)
                {
                    _waveStream.Position = 0;
                }

                AudioPositionSlider.Value = 0;
                CurrentTimeTextBlock.Text = "00:00";

                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = false;
            });
        }

        private void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waveOut != null && _waveStream != null)
            {
                _waveOut.Play();
                PlayButton.IsEnabled = false;
                PauseButton.IsEnabled = true;
                StopButton.IsEnabled = true;

                StartAudioPositionTimer();
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_waveOut != null)
            {
                _waveOut.Pause();
                PlayButton.IsEnabled = true;
                PauseButton.IsEnabled = false;
                StopButton.IsEnabled = true;

                StopAudioPositionTimer();
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopAudio();
        }

        private void StopAudio()
        {
            StopAudioPositionTimer();

            // Unsubscribe from event to prevent a race condition which prevents a new file being played 
            // Race condition results in: 1st file plays, 2nd does not, 3rd does play, etc.
            if (_waveOut != null)
            {
                try
                {
                    _waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
                }
                catch { }
            }

            try
            {
                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }

                if (_waveStream != null)
                {
                    _waveStream.Dispose();
                    _waveStream = null;
                }
            }
            catch { }

            AudioPositionSlider.Value = 0;
            CurrentTimeTextBlock.Text = "00:00";
        }

        private void StartAudioPositionTimer()
        {
            StopAudioPositionTimer();

            _audioPositionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _audioPositionTimer.Tick += AudioPositionTimer_Tick;
            _audioPositionTimer.Start();
        }

        private void StopAudioPositionTimer()
        {
            if (_audioPositionTimer != null)
            {
                _audioPositionTimer.Stop();
                _audioPositionTimer.Tick -= AudioPositionTimer_Tick;
                _audioPositionTimer = null;
            }
        }

        private void AudioPositionTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isDraggingAudioSlider && _waveStream != null)
            {
                var position = _waveStream.CurrentTime;
                CurrentTimeTextBlock.Text = FormatTimeSpan(position);
                AudioPositionSlider.Value = position.TotalSeconds;
            }
        }

        private void AudioPositionSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _isDraggingAudioSlider = true;
        }

        private void AudioPositionSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_isDraggingAudioSlider && _waveStream != null)
            {
                _isDraggingAudioSlider = false;
                var newPosition = TimeSpan.FromSeconds(AudioPositionSlider.Value);
                _waveStream.CurrentTime = newPosition;
                CurrentTimeTextBlock.Text = FormatTimeSpan(newPosition);
            }
        }

        private void AudioPositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isDraggingAudioSlider && _waveStream != null)
            {
                var newPosition = TimeSpan.FromSeconds(e.NewValue);
                CurrentTimeTextBlock.Text = FormatTimeSpan(newPosition);
            }
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours:D2}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
            }
            else
            {
                return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
            }
        }

        private string FormatHexDump(byte[] data, string filePath)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Binary file: {filePath}");
            sb.AppendLine($"File size: {data.Length} bytes");
            sb.AppendLine();
            sb.AppendLine("Hex dump:");
            sb.AppendLine();

            const int bytesPerLine = 16;
            const int maxBytes = 1024 * 16; // Limit to first 16KB for performance

            int bytesToShow = Math.Min(data.Length, maxBytes);

            for (int i = 0; i < bytesToShow; i += bytesPerLine)
            {
                // Offset
                sb.Append($"{i:X8}  ");

                // Hex bytes
                for (int j = 0; j < bytesPerLine; j++)
                {
                    if (i + j < bytesToShow)
                    {
                        sb.Append($"{data[i + j]:X2} ");
                    }
                    else
                    {
                        sb.Append("   ");
                    }

                    // Spacing after 8 bytes
                    if (j == 7)
                    {
                        sb.Append(" ");
                    }
                }

                sb.Append(" |");

                // ASCII representation
                for (int j = 0; j < bytesPerLine && i + j < bytesToShow; j++)
                {
                    byte b = data[i + j];
                    char c = (b >= 32 && b < 127) ? (char)b : '.';
                    sb.Append(c);
                }

                sb.AppendLine("|");
            }

            if (data.Length > maxBytes)
            {
                sb.AppendLine();
                sb.AppendLine($"... ({data.Length - maxBytes} more bytes not shown)");
            }

            return sb.ToString();
        }

        protected override void OnClosed(EventArgs e)
        {
            StopAudio();

            base.OnClosed(e);
        }
    }
}
