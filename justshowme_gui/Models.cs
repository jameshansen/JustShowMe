using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace JustShowMe
{
    /// One captured view of a face: a thumbnail and its SFace embedding. A face row
    /// keeps a few of these so the Select dialog can show what it's grouped, and so
    /// matching has several reference embeddings instead of one drifting vector.
    public class FaceSnapshot
    {
        public ImageSource Image { get; set; }
        public float[] Embedding { get; set; }
    }

    /// A face the user can name and allow. The filter assigns the stable Id.
    public class DetectedFace : INotifyPropertyChanged
    {
        /// How many recent snapshots to keep per face. User-configurable (saved to ini).
        public static int MaxSnapshots = 5;

        private string _name;
        private string _notes;
        private ImageSource _displayImage;

        public int Id { get; set; }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public string Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }

        public DateTime DateAdded { get; set; }
        public DateTime LastSeen { get; set; }
        public DateTime LastSnapshot { get; set; }

        public ImageSource FaceImage { get; set; }   // latest snapshot (keeps changing)

        /// Frozen thumbnail shown in the allowed list. Set once when the face is added
        /// so the list image stays put instead of flickering as new snapshots arrive.
        public ImageSource DisplayImage { get => _displayImage; set { _displayImage = value; OnPropertyChanged(); } }

        public bool ShouldDelete { get; set; }
        public bool IsExistingFace { get; set; }

        /// Newest first, capped at MaxSnapshots. These embeddings are what get allowed.
        public ObservableCollection<FaceSnapshot> Snapshots { get; } = new ObservableCollection<FaceSnapshot>();

        public void AddSnapshot(ImageSource image, float[] embedding)
        {
            Snapshots.Insert(0, new FaceSnapshot { Image = image, Embedding = embedding });
            TrimSnapshots();
            LastSnapshot = DateTime.Now;
            if (image != null) { FaceImage = image; OnPropertyChanged(nameof(FaceImage)); }
        }

        /// Drop snapshots beyond the current cap (e.g. after the user lowers it).
        public void TrimSnapshots()
        {
            while (Snapshots.Count > MaxSnapshots) Snapshots.RemoveAt(Snapshots.Count - 1);
        }

        public string StatusText => IsExistingFace ? $"Allowed (added {DateAdded:yyyy-MM-dd})" : "New Face";

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string p = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public class WebcamDevice
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }
}
