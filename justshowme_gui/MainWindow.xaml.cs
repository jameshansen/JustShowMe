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
            ModeBlurFaceRadio.IsChecked = _config.PersonMode == PersonMode.BlurFace;
            ModeBlurPersonRadio.IsChecked = _config.PersonMode == PersonMode.BlurPerson;
            ModeSmartFillRadio.IsChecked = _config.PersonMode == PersonMode.SmartFill;
            BodySizeSlider.Value = _config.BodyScale;             // fires ValueChanged (guarded)
            BodySizeValueText.Text = _config.BodyScale.ToString("0.0");
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

            _pump = new Pump(filter, new VirtualWebcam(_config.Width, _config.Height, _config.Fps));
            _pump.Settings.Mode = _config.Mode;
            _pump.Settings.PersonMode = _config.PersonMode;
            _pump.Settings.BodyScale = _config.BodyScale;
            _pump.Settings.SmartFillSeconds = _config.SmartFillSeconds;
            _pump.Settings.GhostSustainFrames = (int)(_config.GhostSustainSeconds * _config.Fps);
            _pump.Settings.BlurStrength = _config.BlurStrength;
            SyncAllowedIds();
            _pump.FrameReady += OnFrameReady;
            _pump.FacesReady += OnFacesReady;

            if (!_pump.Start(_selectedCamera, _config.Fps))
            {
                MessageBox.Show($"Could not open camera {_selectedCamera + 1}.");
                _pump.Dispose(); _pump = null;
                return;
            }
            UpdateRunningUI();
        }

        private void Stop()
        {
            _pump?.Dispose();
            _pump = null;
            BeforePreview.Source = null;
            AfterPreview.Source = null;
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

        private void OnFrameReady(ImageSource before, ImageSource after) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BeforePreview.Source = before;
                AfterPreview.Source = after;
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
                ModeSmartFillRadio.IsChecked == true ? PersonMode.SmartFill :
                ModeBlurPersonRadio.IsChecked == true ? PersonMode.BlurPerson : PersonMode.BlurFace;
            _config.Save();
            if (_pump != null) _pump.Settings.PersonMode = _config.PersonMode;
            UpdateModeControls();
        }

        // Body-size slider applies to whole-person modes; smart-fill seconds only to
        // Smart Fill. Hide each where it has no effect.
        private void UpdateModeControls()
        {
            bool person = _config.PersonMode != PersonMode.BlurFace;
            bool smartFill = _config.PersonMode == PersonMode.SmartFill;
            BodySizePanel.Visibility = person ? Visibility.Visible : Visibility.Collapsed;
            SmartFillPanel.Visibility = smartFill ? Visibility.Visible : Visibility.Collapsed;
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
            string s = !DriverManager.IsInstalled ? "Driver: not found beside GUI"
                : DriverManager.IsRegistered ? "Driver: installed and registered"
                : "Driver: present but not registered";
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
}
