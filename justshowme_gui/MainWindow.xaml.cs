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
            FilterDllText.Text = _config.FilterDllPath;
            FaceMatch.Threshold = _config.MatchThreshold;
            ThresholdSlider.Value = _config.MatchThreshold;   // fires ValueChanged (guarded)
            ThresholdValueText.Text = _config.MatchThreshold.ToString("0.00");

            DetectedFace.MaxSnapshots = _config.SnapshotCount;
            SnapshotCountText.Text = _config.SnapshotCount.ToString();

            LoadSavedFaces();
            RefreshDriverStatus();
        }

        // Restore the allowed list saved at last shutdown. Each loaded face goes into
        // both lists: AllowedFaces (so it's allowed and Start syncs its embeddings) and
        // DetectedFaces (so when the person reappears they match this row, not a dupe).
        private void LoadSavedFaces()
        {
            foreach (var f in FaceStore.Load(Config.FaceListDir))
            {
                AllowedFaces.Add(f);
                DetectedFaces.Add(f);
            }
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

            Log.Write($"Start clicked: camera={_selectedCamera}, filterDll={_config.FilterDllPath}");

            IFrameFilter filter;
            try { filter = FilterHost.Load(_config.FilterDllPath); }
            catch (Exception ex)
            {
                Log.Write("FilterHost.Load", ex);
                MessageBox.Show($"Could not load filter DLL:\n{_config.FilterDllPath}\n\n{ex.Message}");
                return;
            }

            _pump = new Pump(filter, new VirtualWebcam(_config.Width, _config.Height, _config.Fps));
            _pump.Settings.Mode = _config.Mode;
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
                // Match to an existing row by identity (any of its snapshot embeddings)
                // first, so a person who got a fresh tracker id (left frame, video
                // looped) updates their row instead of spawning a new "New Face".
                // Fall back to the id when there's no embedding yet.
                var existing =
                    (f.Embedding != null
                        ? DetectedFaces.FirstOrDefault(d =>
                              d.Snapshots.Any(s => s.Embedding != null && FaceMatch.IsSame(s.Embedding, f.Embedding)))
                        : null)
                    ?? DetectedFaces.FirstOrDefault(d => d.Id == f.Id);

                if (existing == null)
                {
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
                    // Refresh the gallery at most ~once a second so the 5 snapshots
                    // span recent time instead of being 5 near-identical frames.
                    if (f.Embedding != null &&
                        DateTime.Now - existing.LastSnapshot > TimeSpan.FromSeconds(1))
                    {
                        existing.AddSnapshot(_pump?.GetThumbnail(f.Box), f.Embedding);
                        if (AllowedFaces.Contains(existing)) SyncAllowedIds();
                    }
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
            var edit = new EditFaceDialog(face) { Owner = this };
            if (edit.ShowDialog() != true) return;

            if (face.ShouldDelete) { DetectedFaces.Remove(face); AllowedFaces.Remove(face); }
            else if (!AllowedFaces.Contains(face))
            {
                face.IsExistingFace = true;
                face.DisplayImage = face.FaceImage; // freeze the list thumbnail at add time
                AllowedFaces.Add(face);
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

        // ---- driver ----
        private void RefreshDriverStatus()
        {
            string s = !DriverManager.IsInstalled ? "Driver: not found beside GUI"
                : DriverManager.IsRegistered ? "Driver: installed and registered"
                : "Driver: present but not registered";
            DriverStatusText.Text = s;
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

        // ---- filter dll selection ----
        private void ChooseFilter_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Filter DLL (*.dll)|*.dll",
                InitialDirectory = AppDomain.CurrentDomain.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                _config.FilterDllPath = dlg.FileName;
                _config.Save();
                FilterDllText.Text = dlg.FileName;
                if (_pump != null && _pump.IsRunning) { Stop(); Start(); }
            }
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
