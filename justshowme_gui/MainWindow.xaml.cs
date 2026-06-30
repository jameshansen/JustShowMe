using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JustShowMe.Filter;
using OpenCvSharp;

namespace JustShowMe
{
    public partial class MainWindow : System.Windows.Window
    {
        private readonly Config _config;
        private Pump _pump;
        private int _selectedCamera;

        public ObservableCollection<DetectedFace> DetectedFaces { get; } = new ObservableCollection<DetectedFace>();
        public ObservableCollection<DetectedFace> AllowedFaces { get; } = new ObservableCollection<DetectedFace>();
        public ObservableCollection<WebcamDevice> AvailableWebcams { get; } = new ObservableCollection<WebcamDevice>();

        public MainWindow()
        {
            InitializeComponent();
            Title = $"JustShowMe - Privacy Webcam Filter - Build {BuildInfo.Number}";
            DataContext = this;
            _config = Config.Load();
            _selectedCamera = _config.CameraIndex;

            LoadWebcams();
            BlurAllRadio.IsChecked = _config.Mode == FilterMode.BlurAll;
            BlurNotAllowedRadio.IsChecked = _config.Mode == FilterMode.BlurNotAllowed;
            ModeVirtualBgRadio.IsChecked = _config.PersonMode == PersonMode.VirtualBackground;
            ModeBlurFaceRadio.IsChecked = _config.PersonMode == PersonMode.BlurFace;
            ModeBlurPersonRadio.IsChecked = _config.PersonMode == PersonMode.BlurPerson;
            ModeSmartFillRadio.IsChecked = _config.PersonMode == PersonMode.SmartFill;
            AspectButton.Content = _config.WideAspect ? "16:9" : "4:3";
            MaskEnabledRadio.IsChecked = _config.ForegroundMaskEnabled;
            MaskDisabledRadio.IsChecked = !_config.ForegroundMaskEnabled;
            SmartFillBgMaskRadio.IsChecked = _config.SmartFillMode == SmartFillMode.VirtualBackground;
            SmartFillRewindRadio.IsChecked = _config.SmartFillMode != SmartFillMode.VirtualBackground;
            BodySizeSlider.Value = _config.BodyScale;             // fires ValueChanged (guarded)
            BodySizeValueText.Text = _config.BodyScale.ToString("0.0");
            PadForegroundSlider.Value = _config.ForegroundPad;    // fires ValueChanged (guarded)
            PadForegroundValueText.Text = _config.ForegroundPad + " px";
            SmartFillSlider.Value = _config.SmartFillSeconds;     // fires ValueChanged (guarded)
            SmartFillValueText.Text = _config.SmartFillSeconds.ToString("0.0") + "s";
            GhostSustainSlider.Value = _config.GhostSustainSeconds;   // fires ValueChanged (guarded)
            GhostSustainValueText.Text = _config.GhostSustainSeconds.ToString("0.0") + "s";
            UpdateModeControls();
            FaceMatch.Threshold = _config.MatchThreshold;
            ThresholdSlider.Value = _config.MatchThreshold;   // fires ValueChanged (guarded)
            ThresholdValueText.Text = _config.MatchThreshold.ToString("0.00");

            DetectedFace.MaxSnapshots = _config.SnapshotCount;
            SnapshotCountText.Text = _config.SnapshotCount.ToString();

            LoadSavedFaces();
            RefreshDriverStatus();
        }

        // Restore the allowed list saved at last shutdown. These are locked, so they go
        // only into AllowedFaces — never the live DetectedFaces list (UpdateFaceList
        // recognises them by embedding and leaves them untouched).
        private void LoadSavedFaces()
        {
            foreach (var f in FaceStore.Load(Config.FaceListDir))
                AllowedFaces.Add(f);
        }

        // ---- camera list ----
        private void LoadWebcams()
        {
            AvailableWebcams.Clear();
            var names = CameraEnumerator.GetDeviceNames();
            for (int i = 0; i < names.Count; i++)
            {
                var name = names[i];
                // Skip our own virtual camera so you don't filter it into itself.
                if (!string.IsNullOrWhiteSpace(name) &&
                    name.IndexOf("JustShowMe", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                AvailableWebcams.Add(new WebcamDevice
                {
                    Index = i, // matches VideoCapture(i, DSHOW)
                    Name = string.IsNullOrWhiteSpace(name) ? $"Camera {i + 1}" : name,
                });
            }
            if (AvailableWebcams.Count == 0)
                AvailableWebcams.Add(new WebcamDevice { Index = -1, Name = "No cameras found" });

            var sel = AvailableWebcams.FirstOrDefault(w => w.Index == _selectedCamera)
                      ?? AvailableWebcams[0];
            WebcamDropdown.SelectedItem = sel;
        }

        private void WebcamDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WebcamDropdown.SelectedItem is WebcamDevice d && d.Index >= 0)
            {
                _selectedCamera = d.Index;
                _config.CameraIndex = d.Index;
                _config.Save();
                if (_pump != null && _pump.IsRunning) { Stop(); Start(); }
            }
        }

        // ---- pump ----
        private void StartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_pump != null && _pump.IsRunning) Stop(); else Start();
        }

        private void Start()
        {
            if (_selectedCamera < 0) { MessageBox.Show("No camera available."); return; }

            Log.Write($"Start clicked: camera={_selectedCamera}, filterDll={Config.DefaultFilterDll}");

            IFrameFilter filter;
            try { filter = FilterHost.Load(Config.DefaultFilterDll); }
            catch (Exception ex)
            {
                Log.Write("FilterHost.Load", ex);
                MessageBox.Show($"Could not load filter DLL:\n{Config.DefaultFilterDll}\n\n{ex.Message}");
                return;
            }

            _pump = new Pump(filter);
            _pump.Settings.Mode = _config.Mode;
            _pump.Settings.PersonMode = _config.PersonMode;
            _pump.Settings.SmartFillMode = _config.SmartFillMode;
            _pump.Settings.ForegroundMaskEnabled = _config.ForegroundMaskEnabled;
            _pump.Settings.ForegroundPad = _config.ForegroundPad;
            _pump.Settings.BodyScale = _config.BodyScale;
            _pump.Settings.SmartFillSeconds = _config.SmartFillSeconds;
            _pump.Settings.GhostSustainFrames = (int)(_config.GhostSustainSeconds * _config.Fps);
            _pump.Settings.BlurStrength = _config.BlurStrength;
            SyncAllowedIds();
            _pump.FrameReady += OnFrameReady;
            _pump.FacesReady += OnFacesReady;

            if (!_pump.Start(_selectedCamera, _config.Fps, _config.WideAspect))
            {
                MessageBox.Show($"Could not open camera {_selectedCamera + 1}.");
                _pump.Dispose(); _pump = null;
                return;
            }
            UpdateRunningUI();
        }

        private void Stop()
        {
            // Disposing the pump disposes the filter, so the virtual background, masks, and
            // frame history are wiped from memory. Clear every preview so nothing stays
            // frozen on screen; a fresh filter is created on the next Start.
            _pump?.Dispose();
            _pump = null;
            BeforePreview.Source = null;
            AfterPreview.Source = null;
            MaskPreview.Source = null;
            PlatePreview.Source = null;
            UpdateRunningUI();
        }

        private void UpdateRunningUI()
        {
            bool running = _pump != null && _pump.IsRunning;
            StartStopButton.Content = running ? "⏸ Stop" : "▶ Start";
            StartStopButton.Background = new SolidColorBrush(running
                ? Color.FromRgb(244, 67, 54) : Color.FromRgb(76, 175, 80));
            NoCameraMessage.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = running ? "Running" : "Stopped";
        }

        private void OnFrameReady(ImageSource before, ImageSource after, ImageSource mask, ImageSource plate) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // A frame can already be queued when Stop runs; ignore it so it doesn't
                // repopulate (and freeze) the previews we just cleared.
                if (_pump == null) return;
                BeforePreview.Source = before;
                AfterPreview.Source = after;
                MaskPreview.Source = mask;
                PlatePreview.Source = plate;
            }));

        private void OnFacesReady(IReadOnlyList<DetectedFaceInfo> faces) =>
            Dispatcher.BeginInvoke(new Action(() => UpdateFaceList(faces)));

        private void UpdateFaceList(IReadOnlyList<DetectedFaceInfo> faces)
        {
            foreach (var f in faces)
            {
                // Already a locked (allowed) face? Recognise them and leave their frozen
                // snapshots/embedding completely alone — never refresh, never overwrite,
                // and don't spawn a duplicate "New Face" row for them.
                if (f.Embedding != null &&
                    AllowedFaces.Any(a => a.Snapshots.Any(s => s.Embedding != null && FaceMatch.IsSame(s.Embedding, f.Embedding))))
                    continue;

                // Match to an existing transient row by identity (any of its snapshot
                // embeddings) first, so a person who got a fresh tracker id (left frame,
                // video looped) updates their row instead of spawning a new "New Face".
                // Fall back to the id when there's no embedding yet.
                var existing =
                    (f.Embedding != null
                        ? DetectedFaces.FirstOrDefault(d =>
                              d.Snapshots.Any(s => s.Embedding != null && FaceMatch.IsSame(s.Embedding, f.Embedding)))
                        : null)
                    ?? DetectedFaces.FirstOrDefault(d => d.Id == f.Id);

                if (existing == null)
                {
                    // Only mint a row from a face actually seen this frame — never from a
                    // ghost track coasting on persistence (its box is stale, so a thumbnail
                    // there would be whatever the video now shows at that spot).
                    if (!f.Seen) continue;
                    var nf = new DetectedFace
                    {
                        Id = f.Id, Name = "New Face",
                        DateAdded = DateTime.Now, LastSeen = DateTime.Now,
                    };
                    nf.AddSnapshot(_pump?.GetThumbnail(f.Box), f.Embedding);
                    DetectedFaces.Add(nf);
                }
                else
                {
                    existing.Id = f.Id;
                    existing.LastSeen = DateTime.Now;
                    // Refresh the (still-unlocked) row's gallery at most ~twice a second
                    // so its snapshots span recent time — but only from a face seen this
                    // frame, so a stale ghost box can never overwrite it with background.
                    if (f.Seen && f.Embedding != null &&
                        DateTime.Now - existing.LastSnapshot > TimeSpan.FromSeconds(0.5))
                        existing.AddSnapshot(_pump?.GetThumbnail(f.Box), f.Embedding);
                }
            }

            // Drop faces not seen for 10s unless they're allowed.
            foreach (var stale in DetectedFaces
                .Where(d => DateTime.Now - d.LastSeen > TimeSpan.FromSeconds(10) && !AllowedFaces.Contains(d))
                .ToList())
                DetectedFaces.Remove(stale);

            FaceCountText.Text = $"Faces detected: {DetectedFaces.Count}";
        }

        // ponytail: publish a fresh immutable list so the pump thread never reads it
        // mid-mutation (reference assignment is atomic). Every snapshot embedding of
        // every allowed face goes in, so a person is recognised across the angles we
        // captured — more reference vectors, better recall.
        private void SyncAllowedIds()
        {
            if (_pump != null)
                _pump.Settings.AllowedEmbeddings =
                    AllowedFaces.SelectMany(f => f.Snapshots)
                                .Where(s => s.Embedding != null)
                                .Select(s => s.Embedding).ToList();
        }

        // ---- face management ----
        private void AddFace_Click(object sender, RoutedEventArgs e)
        {
            if (DetectedFaces.Count == 0) { MessageBox.Show("No faces detected yet."); return; }

            var select = new SelectFaceDialog(DetectedFaces.ToList()) { Owner = this };
            if (select.ShowDialog() != true || select.SelectedFace == null) return;

            var face = select.SelectedFace;
            face.IsExistingFace = false;
            // Freeze the thumbnail now (at selection) so the Edit dialog — and later the
            // allowed list — show this exact face, not whatever the live video updates
            // FaceImage to as other faces come and go.
            face.DisplayImage = face.FaceImage;
            var edit = new EditFaceDialog(face) { Owner = this };
            if (edit.ShowDialog() != true) return;

            if (face.ShouldDelete) { DetectedFaces.Remove(face); AllowedFaces.Remove(face); }
            else if (!AllowedFaces.Contains(face))
            {
                face.IsExistingFace = true;
                AllowedFaces.Add(face);
                // Lock it: out of the live list, so its snapshots/embedding freeze at
                // this moment and it can't be detected/added again or overwritten.
                DetectedFaces.Remove(face);
            }
            SyncAllowedIds();
        }

        private void FaceOptions_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as Button)?.DataContext is DetectedFace face)) return;
            var menu = new ContextMenu();
            var edit = new MenuItem { Header = "Edit Face" };
            edit.Click += (s, ev) => EditFace(face);
            var remove = new MenuItem { Header = "Remove from List" };
            remove.Click += (s, ev) => { AllowedFaces.Remove(face); SyncAllowedIds(); };
            menu.Items.Add(edit); menu.Items.Add(remove);
            menu.IsOpen = true;
        }

        private void EditFace(DetectedFace face)
        {
            face.IsExistingFace = true;
            var dlg = new EditFaceDialog(face) { Owner = this };
            if (dlg.ShowDialog() == true && face.ShouldDelete)
            {
                AllowedFaces.Remove(face); DetectedFaces.Remove(face); SyncAllowedIds();
            }
        }

        // Higher = stricter (faces must look more alike to count as the same person).
        // Tune up if different people get merged / the wrong face un-blurs; down if an
        // allowed face keeps re-blurring. Saved to the ini and applied live.
        private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            FaceMatch.Threshold = e.NewValue;
            _config.MatchThreshold = e.NewValue;
            _config.Save();
            if (ThresholdValueText != null) ThresholdValueText.Text = e.NewValue.ToString("0.00");
        }

        // ---- snapshots-per-face stepper ----
        private void SnapshotPlus_Click(object sender, RoutedEventArgs e) => SetSnapshotCount(_config.SnapshotCount + 1);
        private void SnapshotMinus_Click(object sender, RoutedEventArgs e) => SetSnapshotCount(_config.SnapshotCount - 1);

        private void SetSnapshotCount(int n)
        {
            n = Math.Max(1, Math.Min(20, n));
            _config.SnapshotCount = n;
            _config.Save();
            DetectedFace.MaxSnapshots = n;
            foreach (var f in DetectedFaces) f.TrimSnapshots();   // shrink existing galleries if lowered
            SnapshotCountText.Text = n.ToString();
            SyncAllowedIds();
        }

        private void FilterMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null) return; // radio Checked fires during InitializeComponent, before ctor sets _config
            _config.Mode = BlurAllRadio.IsChecked == true ? FilterMode.BlurAll : FilterMode.BlurNotAllowed;
            _config.Save();
            if (_pump != null) _pump.Settings.Mode = _config.Mode;
        }

        private void PersonMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            _config.PersonMode =
                ModeVirtualBgRadio.IsChecked == true ? PersonMode.VirtualBackground :
                ModeSmartFillRadio.IsChecked == true ? PersonMode.SmartFill :
                ModeBlurPersonRadio.IsChecked == true ? PersonMode.BlurPerson : PersonMode.BlurFace;
            // Virtual Background mode needs the mask; auto-enable it (mirrors Smart Fill's
            // Virtual Background submode).
            if (_config.PersonMode == PersonMode.VirtualBackground && MaskEnabledRadio.IsChecked != true)
                MaskEnabledRadio.IsChecked = true;   // fires MaskMode_Changed (enables + saves)
            _config.Save();
            if (_pump != null) _pump.Settings.PersonMode = _config.PersonMode;
            UpdateModeControls();
        }

        // 16:9 / 4:3 capture toggle (between the camera dropdown and Start).
        private void AspectToggle_Click(object sender, RoutedEventArgs e)
        {
            _config.WideAspect = !_config.WideAspect;
            AspectButton.Content = _config.WideAspect ? "16:9" : "4:3";
            _config.Save();
            if (_pump != null && _pump.IsRunning) { Stop(); Start(); }   // re-open at the new resolution
        }

        // Body-size slider applies to whole-person modes; smart-fill seconds only to
        // Smart Fill. Hide each where it has no effect.
        private void UpdateModeControls()
        {
            bool body = _config.PersonMode == PersonMode.BlurPerson || _config.PersonMode == PersonMode.SmartFill;
            bool smartFill = _config.PersonMode == PersonMode.SmartFill;
            bool vbFill = smartFill && _config.SmartFillMode == SmartFillMode.VirtualBackground;
            BodySizePanel.Visibility = body ? Visibility.Visible : Visibility.Collapsed;
            SmartFillModePanel.Visibility = smartFill ? Visibility.Visible : Visibility.Collapsed;
            // "go back" seconds only matter to Rewind; Virtual Background has no rewind.
            SmartFillPanel.Visibility = (smartFill && !vbFill) ? Visibility.Visible : Visibility.Collapsed;
            // Both previews and the pad slider track the mask toggle: the virtual background
            // builds (and the mask can be padded) as soon as the mask is on, regardless of mode.
            MaskPreviewPanel.Visibility = _config.ForegroundMaskEnabled ? Visibility.Visible : Visibility.Collapsed;
            PlatePreviewPanel.Visibility = _config.ForegroundMaskEnabled ? Visibility.Visible : Visibility.Collapsed;
            PadForegroundPanel.Visibility = _config.ForegroundMaskEnabled ? Visibility.Visible : Visibility.Collapsed;
        }

        // Virtual background (person segmentation) on or off.
        private void MaskMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            _config.ForegroundMaskEnabled = MaskEnabledRadio.IsChecked == true;
            // Virtual Background smart fill depends on the mask; if it's switched off, fall
            // back to Rewind (fires SmartFillMode_Changed, which sets + saves the mode).
            if (!_config.ForegroundMaskEnabled && _config.PersonMode == PersonMode.SmartFill
                && _config.SmartFillMode == SmartFillMode.VirtualBackground)
                SmartFillRewindRadio.IsChecked = true;
            _config.Save();
            if (_pump != null) _pump.Settings.ForegroundMaskEnabled = _config.ForegroundMaskEnabled;
            UpdateModeControls();
        }

        // Smart fill source: Rewind vs Virtual Background. Virtual Background needs the
        // mask, so picking it auto-enables that (mirrors the user's spec).
        private void SmartFillMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            _config.SmartFillMode = SmartFillBgMaskRadio.IsChecked == true
                ? SmartFillMode.VirtualBackground : SmartFillMode.Rewind;
            if (_config.SmartFillMode == SmartFillMode.VirtualBackground && MaskEnabledRadio.IsChecked != true)
                MaskEnabledRadio.IsChecked = true;   // fires MaskMode_Changed (enables + saves)
            _config.Save();
            if (_pump != null) _pump.Settings.SmartFillMode = _config.SmartFillMode;
            UpdateModeControls();
        }

        // Grows the foreground mask area by N pixels (dilation), so the kept foreground
        // covers a little more around the person. 0 = the raw segmentation mask.
        private void PadForegroundSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            _config.ForegroundPad = (int)e.NewValue;
            _config.Save();
            if (_pump != null) _pump.Settings.ForegroundPad = _config.ForegroundPad;
            if (PadForegroundValueText != null) PadForegroundValueText.Text = _config.ForegroundPad + " px";
        }

        // Whole-person zone width (face widths). Sizes both the blur/fill and the safe zone.
        private void BodySizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            _config.BodyScale = e.NewValue;
            _config.Save();
            if (_pump != null) _pump.Settings.BodyScale = e.NewValue;
            if (BodySizeValueText != null) BodySizeValueText.Text = e.NewValue.ToString("0.0");
        }

        // How many seconds back to pull the background plate for Smart Fill.
        private void SmartFillSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            _config.SmartFillSeconds = e.NewValue;
            _config.Save();
            if (_pump != null) _pump.Settings.SmartFillSeconds = e.NewValue;
            if (SmartFillValueText != null) SmartFillValueText.Text = e.NewValue.ToString("0.0") + "s";
        }

        // How long a face keeps being tracked (and blurred/filled) after detection drops.
        // Stored in seconds; the filter wants frames, so convert with the configured fps.
        private void GhostSustainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_config == null) return; // fires during InitializeComponent, before ctor sets _config
            _config.GhostSustainSeconds = e.NewValue;
            _config.Save();
            if (_pump != null) _pump.Settings.GhostSustainFrames = (int)(e.NewValue * _config.Fps);
            if (GhostSustainValueText != null) GhostSustainValueText.Text = e.NewValue.ToString("0.0") + "s";
        }

        // ---- driver ----
        private void RefreshDriverStatus()
        {
            string s = !DriverManager.IsInstalled ? "not found beside GUI"
                : DriverManager.IsRegistered ? "Installed / Registered"
                : "present but not registered";
            DriverStatusText.Text = s;

            // Blue "Install" prompts action when not registered; grey once installed.
            InstallButton.Background = new SolidColorBrush(DriverManager.IsRegistered
                ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0x1E, 0x88, 0xE5));
        }

        private void InstallDriver_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MessageBox.Show(DriverManager.Register()
                    ? "Driver registered." : "Registration failed or was cancelled.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            RefreshDriverStatus();
        }

        private void UninstallDriver_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                MessageBox.Show(DriverManager.Unregister()
                    ? "Driver unregistered." : "Unregistration failed or was cancelled.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            RefreshDriverStatus();
        }

        protected override void OnClosed(EventArgs e)
        {
            Stop();
            _config.Save();
            try { FaceStore.Save(Config.FaceListDir, AllowedFaces); }
            catch (Exception ex) { Log.Write("FaceStore.Save", ex); }
            base.OnClosed(e);
        }
    }

    /// Returns width * ratio, so a preview box can bind its Height to its own ActualWidth
    /// and stay a fixed aspect ratio (16:9 = 0.5625) while growing with the window.
    public sealed class AspectRatioConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            double w = value is double d ? d : 0;
            double ratio = 0.5625; // 9/16
            if (parameter is string s &&
                double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r))
                ratio = r;
            double h = w * ratio;
            return double.IsNaN(h) || h < 0 ? 0.0 : h;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
