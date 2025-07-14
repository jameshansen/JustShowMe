// MainWindow.xaml.cs - Complete Fixed Version
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;
using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace JustShowMe
{
    // Main Application Window
    public partial class MainWindow : System.Windows.Window, INotifyPropertyChanged
    {
        private VideoCapture _camera;
        private Mat _currentFrame;
        private FaceDetector _faceDetector;
        private VirtualWebcam _virtualWebcam;
        private System.Threading.Timer _frameTimer;
        private bool _isRunning;
        private int _selectedWebcamIndex = 0;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            _faceDetector = new FaceDetector();
            _virtualWebcam = new VirtualWebcam();

            // Initialize collections
            DetectedFaces = new ObservableCollection<DetectedFace>();
            AllowedFaces = new ObservableCollection<DetectedFace>();
            AvailableWebcams = new ObservableCollection<WebcamDevice>();

            // Set default filter mode
            BlurAllFaces = false;
            BlurFacesNotInList = true;
            EnableVirtualWebcam = true;

            LoadAvailableWebcams();
            UpdateUI(); // Initial UI update
        }

        // Properties for data binding
        public ObservableCollection<DetectedFace> DetectedFaces { get; set; }
        public ObservableCollection<DetectedFace> AllowedFaces { get; set; }
        public ObservableCollection<WebcamDevice> AvailableWebcams { get; set; }

        public ImageSource PreviewImage { get; set; }
        public bool BlurAllFaces { get; set; }
        public bool BlurFacesNotInList { get; set; }
        public bool EnableVirtualWebcam { get; set; }

        private void LoadAvailableWebcams()
        {
            AvailableWebcams.Clear();

            try
            {
                // Detect available webcams
                for (int i = 0; i < 5; i++) // Check first 5 camera indices
                {
                    var testCam = new VideoCapture(i);
                    if (testCam.IsOpened())
                    {
                        string cameraName = $"Camera {i + 1}";

                        AvailableWebcams.Add(new WebcamDevice
                        {
                            Index = i,
                            Name = cameraName
                        });

                        testCam.Release();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error detecting cameras: {ex.Message}");
            }

            if (AvailableWebcams.Count == 0)
            {
                AvailableWebcams.Add(new WebcamDevice
                {
                    Index = -1,
                    Name = "No cameras found"
                });
            }

            OnPropertyChanged(nameof(AvailableWebcams));
        }

        private void WebcamDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is WebcamDevice selected)
            {
                if (selected.Index == -1) return;

                _selectedWebcamIndex = selected.Index;
                if (_isRunning)
                {
                    StopVideoCapture();
                    Task.Run(async () => await StartVideoCapture());
                }
            }
        }

        private async void StartCapture_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
            {
                await StartVideoCapture();
            }
            else
            {
                StopVideoCapture();
            }
        }

        private void UpdateUI()
        {
            if (StartStopButton != null)
            {
                StartStopButton.Content = _isRunning ? "⏸ Stop Camera" : "▶ Start Camera";
                StartStopButton.Background = _isRunning ?
                    new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)) : // Red
                    new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));   // Green
            }

            if (NoCameraMessage != null)
            {
                NoCameraMessage.Visibility = _isRunning ? Visibility.Collapsed : Visibility.Visible;
            }

            UpdateVirtualCameraStatus();

            if (FaceCountText != null)
            {
                FaceCountText.Text = $"Faces detected: {DetectedFaces.Count}";
            }
        }

        private async Task StartVideoCapture()
        {
            try
            {
                if (_selectedWebcamIndex == -1 || AvailableWebcams.Count == 0 ||
                    AvailableWebcams[0].Index == -1)
                {
                    MessageBox.Show("No cameras available. Please connect a camera and restart the application.");
                    return;
                }

                _camera = new VideoCapture(_selectedWebcamIndex);
                if (!_camera.IsOpened())
                {
                    MessageBox.Show($"Unable to access camera {_selectedWebcamIndex + 1}. Please check if the camera is being used by another application.");
                    return;
                }

                _isRunning = true;
                Application.Current.Dispatcher.Invoke(() => UpdateUI());

                _frameTimer = new System.Threading.Timer(ProcessFrame, null, 0, 33); // ~30 FPS
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting camera: {ex.Message}");
                _isRunning = false;
                Application.Current.Dispatcher.Invoke(() => UpdateUI());
            }
        }

        private void ProcessFrame(object state)
        {
            if (_camera?.IsOpened() == true)
            {
                _currentFrame = new Mat();
                _camera.Read(_currentFrame);

                if (!_currentFrame.Empty())
                {
                    var processedFrame = ProcessVideoFrame(_currentFrame);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        PreviewImage = processedFrame.ToBitmapSource();
                        OnPropertyChanged(nameof(PreviewImage));
                    });

                    if (EnableVirtualWebcam)
                    {
                        _virtualWebcam.SendFrame(processedFrame);
                    }
                }
            }
        }

        private Mat ProcessVideoFrame(Mat frame)
        {
            var processedFrame = frame.Clone();
            var faces = _faceDetector.DetectFaces(frame);
            UpdateDetectedFacesList(faces);

            if (BlurAllFaces)
            {
                foreach (var face in faces)
                {
                    BlurFaceRegion(processedFrame, face.BoundingBox);
                }
            }
            else if (BlurFacesNotInList)
            {
                foreach (var face in faces)
                {
                    var allowedFace = AllowedFaces.FirstOrDefault(af => af.Id == face.Id);
                    if (allowedFace == null)
                    {
                        BlurFaceRegion(processedFrame, face.BoundingBox);
                    }
                }
            }

            return processedFrame;
        }

        private void BlurFaceRegion(Mat frame, OpenCvSharp.Rect faceRect)
        {
            var clampedRect = new OpenCvSharp.Rect(
                Math.Max(0, faceRect.X),
                Math.Max(0, faceRect.Y),
                Math.Min(faceRect.Width, frame.Width - faceRect.X),
                Math.Min(faceRect.Height, frame.Height - faceRect.Y)
            );

            if (clampedRect.Width > 0 && clampedRect.Height > 0)
            {
                var faceRegion = new Mat(frame, clampedRect);
                var blurredFace = new Mat();
                Cv2.GaussianBlur(faceRegion, blurredFace, new OpenCvSharp.Size(51, 51), 0);
                blurredFace.CopyTo(frame[clampedRect]);
            }
        }

        private void UpdateDetectedFacesList(List<Face> currentFaces)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var face in currentFaces)
                {
                    var existing = DetectedFaces.FirstOrDefault(df => df.Id == face.Id);
                    if (existing == null)
                    {
                        DetectedFaces.Add(new DetectedFace
                        {
                            Id = face.Id,
                            Name = "New Face",
                            DateAdded = DateTime.Now,
                            LastSeen = DateTime.Now,
                            FaceImage = ExtractFaceImage(face.BoundingBox),
                            IsExistingFace = false
                        });
                    }
                    else
                    {
                        existing.LastSeen = DateTime.Now;
                    }
                }

                var facesToRemove = DetectedFaces.Where(df =>
                    DateTime.Now - df.LastSeen > TimeSpan.FromSeconds(10) &&
                    !AllowedFaces.Contains(df)).ToList();

                foreach (var face in facesToRemove)
                {
                    DetectedFaces.Remove(face);
                }

                if (FaceCountText != null)
                {
                    FaceCountText.Text = $"Faces detected: {DetectedFaces.Count}";
                }
            });
        }

        private ImageSource ExtractFaceImage(OpenCvSharp.Rect faceRect)
        {
            if (_currentFrame != null && !_currentFrame.Empty())
            {
                var clampedRect = new OpenCvSharp.Rect(
                    Math.Max(0, faceRect.X),
                    Math.Max(0, faceRect.Y),
                    Math.Min(faceRect.Width, _currentFrame.Width - faceRect.X),
                    Math.Min(faceRect.Height, _currentFrame.Height - faceRect.Y)
                );

                if (clampedRect.Width > 0 && clampedRect.Height > 0)
                {
                    var faceRegion = new Mat(_currentFrame, clampedRect);
                    var resized = new Mat();
                    Cv2.Resize(faceRegion, resized, new OpenCvSharp.Size(64, 64));
                    return resized.ToBitmapSource();
                }
            }
            return null;
        }

        private void AddFace_Click(object sender, RoutedEventArgs e)
        {
            if (DetectedFaces.Count == 0)
            {
                MessageBox.Show("No faces detected. Please ensure someone is visible in the camera.");
                return;
            }

            var selectFaceDialog = new SelectFaceDialog(DetectedFaces.ToList());
            if (selectFaceDialog.ShowDialog() == true)
            {
                var selectedFace = selectFaceDialog.SelectedFace;
                if (selectedFace != null)
                {
                    selectedFace.IsExistingFace = false;

                    var editFaceDialog = new EditFaceDialog(selectedFace);
                    if (editFaceDialog.ShowDialog() == true)
                    {
                        if (selectedFace.ShouldDelete)
                        {
                            DetectedFaces.Remove(selectedFace);
                            AllowedFaces.Remove(selectedFace);
                        }
                        else
                        {
                            if (!AllowedFaces.Contains(selectedFace))
                            {
                                selectedFace.IsExistingFace = true;
                                AllowedFaces.Add(selectedFace);
                            }
                        }
                    }
                }
            }
        }

        private void FaceOptions_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var face = button?.DataContext as DetectedFace;
            if (face != null)
            {
                var contextMenu = new ContextMenu();

                var editItem = new MenuItem { Header = "Edit Face" };
                editItem.Click += (s, ev) => EditFace(face);

                var removeItem = new MenuItem { Header = "Remove from List" };
                removeItem.Click += (s, ev) => AllowedFaces.Remove(face);

                contextMenu.Items.Add(editItem);
                contextMenu.Items.Add(removeItem);
                contextMenu.IsOpen = true;
            }
        }

        private void EditFace(DetectedFace face)
        {
            face.IsExistingFace = true;

            var editDialog = new EditFaceDialog(face);
            if (editDialog.ShowDialog() == true)
            {
                if (face.ShouldDelete)
                {
                    AllowedFaces.Remove(face);
                    DetectedFaces.Remove(face);
                }
            }
        }

        private void FilterMode_Changed(object sender, RoutedEventArgs e)
        {
            var radioButton = sender as RadioButton;
            if (radioButton?.Name == "BlurAllFacesRadio")
            {
                BlurAllFaces = true;
                BlurFacesNotInList = false;
            }
            else if (radioButton?.Name == "BlurFacesNotInListRadio")
            {
                BlurAllFaces = false;
                BlurFacesNotInList = true;
            }
        }

        private void VirtualWebcam_Checked(object sender, RoutedEventArgs e)
        {
            EnableVirtualWebcam = true;
            _virtualWebcam?.StartVirtualCamera();
            UpdateVirtualCameraStatus();
        }

        private void VirtualWebcam_Unchecked(object sender, RoutedEventArgs e)
        {
            EnableVirtualWebcam = false;
            _virtualWebcam?.StopVirtualCamera();
            UpdateVirtualCameraStatus();
        }

        private void UpdateVirtualCameraStatus()
        {
            if (StatusText != null)
            {
                var cameraStatus = _isRunning ? "Camera running" : "Camera stopped";
                var virtualStatus = _virtualWebcam?.IsActive == true ? " | Virtual camera active" : " | Virtual camera inactive";
                StatusText.Text = cameraStatus + virtualStatus;
            }
        }

        private void StopVideoCapture()
        {
            _isRunning = false;
            _frameTimer?.Dispose();
            _camera?.Release();
            Application.Current.Dispatcher.Invoke(() => UpdateUI());
        }

        protected override void OnClosed(EventArgs e)
        {
            StopVideoCapture();
            _virtualWebcam?.StopVirtualCamera();
            base.OnClosed(e);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // Edit Face Dialog
    public partial class EditFaceDialog : System.Windows.Window
    {
        private DetectedFace _face;

        public EditFaceDialog(DetectedFace face)
        {
            InitializeComponent();
            _face = face;
            DataContext = _face;

            this.Loaded += (s, e) =>
            {
                if (DeleteButton != null)
                {
                    DeleteButton.Visibility = _face.IsExistingFace ? Visibility.Visible : Visibility.Collapsed;
                }
            };
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Finish_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show($"Are you sure you want to delete '{_face.Name}' from the list?",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _face.ShouldDelete = true;
                DialogResult = true;
                Close();
            }
        }
    }

    // Select Face Dialog
    public partial class SelectFaceDialog : System.Windows.Window
    {
        public DetectedFace SelectedFace { get; private set; }

        public SelectFaceDialog(List<DetectedFace> availableFaces)
        {
            InitializeComponent();
            FaceListBox.ItemsSource = availableFaces;
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            SelectedFace = FaceListBox.SelectedItem as DetectedFace;
            if (SelectedFace != null)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("Please select a face to continue.");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    // Virtual Webcam Implementation using Softcam
    public class VirtualWebcam
    {
        // P/Invoke declarations for Windows DLL registration
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DllRegisterServerDelegate();

        // P/Invoke declarations for Softcam API
        [DllImport("softcam.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr scCreateCamera(int width, int height, float framerate);

        [DllImport("softcam.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void scDeleteCamera(IntPtr camera);

        [DllImport("softcam.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void scSendFrame(IntPtr camera, IntPtr image_bits);

        [DllImport("softcam.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern bool scWaitForConnection(IntPtr camera, float timeout);

        // Member variables
        private IntPtr _camera = IntPtr.Zero;
        private bool _isActive = false;
        private int _width = 640;
        private int _height = 480;
        private float _framerate = 30.0f;
        private static bool _dllRegistered = false;

        public bool IsActive => _isActive;

        public static bool RegisterSoftcamDLL()
        {
            try
            {
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "softcam.dll");

                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"Softcam DLL not found at: {dllPath}");
                    MessageBox.Show($"softcam.dll not found in application folder.\n\nPlease download softcam.dll from:\nhttps://github.com/tshino/softcam/releases\n\nAnd place it in the same folder as JustShowMe.exe",
                        "Virtual Camera Setup Required", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                Console.WriteLine($"Attempting to register: {dllPath}");

                IntPtr hModule = LoadLibrary(dllPath);
                if (hModule == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"Failed to load DLL. Error: {error}");
                    return false;
                }

                try
                {
                    IntPtr procAddress = GetProcAddress(hModule, "DllRegisterServer");
                    if (procAddress == IntPtr.Zero)
                    {
                        Console.WriteLine("DllRegisterServer function not found in softcam.dll");
                        return false;
                    }

                    var dllRegisterServer = Marshal.GetDelegateForFunctionPointer<DllRegisterServerDelegate>(procAddress);
                    int result = dllRegisterServer();

                    if (result == 0) // S_OK
                    {
                        Console.WriteLine("Softcam DLL registered successfully!");
                        _dllRegistered = true;
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"DLL registration failed with HRESULT: 0x{result:X8}");

                        if ((uint)result == 0x80070005) // E_ACCESSDENIED
                        {
                            MessageBox.Show("Administrator privileges required to register the virtual camera.\n\nPlease run JustShowMe as Administrator, or manually register softcam.dll using:\nregsvr32 softcam.dll",
                                "Admin Rights Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }

                        return false;
                    }
                }
                finally
                {
                    FreeLibrary(hModule);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during DLL registration: {ex.Message}");
                return false;
            }
        }

        private static bool CheckIfRunningAsAdmin()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private static void RestartAsAdmin()
        {
            try
            {
                var exeName = Process.GetCurrentProcess().MainModule.FileName;
                var startInfo = new ProcessStartInfo(exeName)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process.Start(startInfo);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to restart as administrator: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void StartVirtualCamera()
        {
            try
            {
                if (!_dllRegistered)
                {
                    Console.WriteLine("Registering Softcam DLL...");

                    if (!RegisterSoftcamDLL())
                    {
                        Console.WriteLine("Failed to register Softcam DLL");

                        if (!CheckIfRunningAsAdmin())
                        {
                            var result = MessageBox.Show("Virtual camera registration failed.\n\nWould you like to restart JustShowMe as Administrator to enable virtual camera features?",
                                "Admin Rights Required", MessageBoxButton.YesNo, MessageBoxImage.Question);

                            if (result == MessageBoxResult.Yes)
                            {
                                RestartAsAdmin();
                            }
                        }
                        return;
                    }
                }

                if (_camera != IntPtr.Zero)
                {
                    StopVirtualCamera();
                }

                _camera = scCreateCamera(_width, _height, _framerate);

                if (_camera != IntPtr.Zero)
                {
                    _isActive = true;
                    Console.WriteLine($"Virtual camera created: {_width}x{_height} @ {_framerate}fps");

                    Task.Run(() =>
                    {
                        if (scWaitForConnection(_camera, 1.0f))
                        {
                            Console.WriteLine("Virtual camera connected successfully!");
                        }
                        else
                        {
                            Console.WriteLine("Virtual camera created but no applications connected yet");
                        }
                    });
                }
                else
                {
                    Console.WriteLine("Failed to create virtual camera - check if softcam.dll is properly registered");
                }
            }
            catch (DllNotFoundException)
            {
                MessageBox.Show("softcam.dll not found!\n\nPlease download it from:\nhttps://github.com/tshino/softcam/releases\n\nAnd place it in the same folder as JustShowMe.exe",
                    "Virtual Camera DLL Missing", MessageBoxButton.OK, MessageBoxImage.Error);
                _isActive = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting virtual camera: {ex.Message}");
                _isActive = false;
            }
        }

        public void StopVirtualCamera()
        {
            if (_camera != IntPtr.Zero)
            {
                scDeleteCamera(_camera);
                _camera = IntPtr.Zero;
                _isActive = false;
                Console.WriteLine("Virtual camera stopped");
            }
        }

        public void SendFrame(Mat frame)
        {
            if (!_isActive || _camera == IntPtr.Zero || frame == null || frame.Empty())
                return;

            try
            {
                var resizedFrame = new Mat();
                Cv2.Resize(frame, resizedFrame, new OpenCvSharp.Size(_width, _height));

                var rgbFrame = new Mat();
                Cv2.CvtColor(resizedFrame, rgbFrame, ColorConversionCodes.BGR2RGB);

                var imageBytes = new byte[_width * _height * 3];
                Marshal.Copy(rgbFrame.Data, imageBytes, 0, imageBytes.Length);

                var handle = GCHandle.Alloc(imageBytes, GCHandleType.Pinned);
                try
                {
                    scSendFrame(_camera, handle.AddrOfPinnedObject());
                }
                finally
                {
                    handle.Free();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending frame to virtual camera: {ex.Message}");
            }
        }

        public void SetResolution(int width, int height)
        {
            if (width > 0 && height > 0)
            {
                _width = width;
                _height = height;

                if (_isActive)
                {
                    StopVirtualCamera();
                    StartVirtualCamera();
                }
            }
        }

        ~VirtualWebcam()
        {
            StopVirtualCamera();
        }
    }

    // Data Models
    public class Face
    {
        public int Id { get; set; }
        public OpenCvSharp.Rect BoundingBox { get; set; }
        public float Confidence { get; set; }
    }

    public class DetectedFace : INotifyPropertyChanged
    {
        private string _name;
        private string _notes;

        public int Id { get; set; }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Notes
        {
            get => _notes;
            set { _notes = value; OnPropertyChanged(); }
        }

        public DateTime DateAdded { get; set; }
        public DateTime LastSeen { get; set; }
        public ImageSource FaceImage { get; set; }
        public bool ShouldDelete { get; set; }
        public bool IsExistingFace { get; set; }

        public string DateAddedText => $"Added on {DateAdded:yyyy-MM-dd}";
        public string StatusText => IsExistingFace ? $"Already Added on {DateAdded:yyyy-MM-dd}" : "New Face";

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class WebcamDevice
    {
        public int Index { get; set; }
        public string Name { get; set; }
    }

    public class FaceEmbedding
    {
        public float[] Features { get; set; }
    }

    // Face Detection Service
    public class FaceDetector
    {
        private CascadeClassifier _faceCascade;
        private Dictionary<int, FaceEmbedding> _knownFaces;
        private int _nextFaceId = 1;

        public FaceDetector()
        {
            _faceCascade = new CascadeClassifier("haarcascade_frontalface_alt.xml");
            _knownFaces = new Dictionary<int, FaceEmbedding>();
        }

        public List<Face> DetectFaces(Mat frame)
        {
            var faces = new List<Face>();
            var grayFrame = new Mat();
            Cv2.CvtColor(frame, grayFrame, ColorConversionCodes.BGR2GRAY);

            var faceRects = _faceCascade.DetectMultiScale(grayFrame, 1.3, 5);

            foreach (var rect in faceRects)
            {
                var faceRegion = new Mat(grayFrame, rect);
                var embedding = ExtractFaceEmbedding(faceRegion);
                int faceId = MatchOrCreateFace(embedding);

                faces.Add(new Face
                {
                    Id = faceId,
                    BoundingBox = rect,
                    Confidence = 0.9f
                });
            }

            return faces;
        }

        private FaceEmbedding ExtractFaceEmbedding(Mat faceRegion)
        {
            var resized = new Mat();
            Cv2.Resize(faceRegion, resized, new OpenCvSharp.Size(128, 128));

            var embedding = new float[128];
            var hist = new Mat();

            var channels = new int[] { 0 };
            var histSize = new int[] { 256 };
            var ranges = new Rangef[] { new Rangef(0, 256) };

            Cv2.CalcHist(new Mat[] { resized }, channels, new Mat(), hist, 1, histSize, ranges);

            for (int i = 0; i < Math.Min(128, hist.Rows); i++)
            {
                embedding[i] = hist.At<float>(i, 0);
            }

            return new FaceEmbedding { Features = embedding };
        }

        private int MatchOrCreateFace(FaceEmbedding embedding)
        {
            double bestSimilarity = 0;
            int bestMatchId = -1;

            foreach (var kvp in _knownFaces)
            {
                double similarity = CalculateSimilarity(embedding, kvp.Value);
                if (similarity > bestSimilarity && similarity > 0.8)
                {
                    bestSimilarity = similarity;
                    bestMatchId = kvp.Key;
                }
            }

            if (bestMatchId != -1)
            {
                return bestMatchId;
            }
            else
            {
                int newId = _nextFaceId++;
                _knownFaces[newId] = embedding;
                return newId;
            }
        }

        private double CalculateSimilarity(FaceEmbedding a, FaceEmbedding b)
        {
            double dotProduct = 0;
            double normA = 0;
            double normB = 0;

            for (int i = 0; i < a.Features.Length; i++)
            {
                dotProduct += a.Features[i] * b.Features[i];
                normA += a.Features[i] * a.Features[i];
                normB += b.Features[i] * b.Features[i];
            }

            return dotProduct / (Math.Sqrt(normA) * Math.Sqrt(normB));
        }
    }
}