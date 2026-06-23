using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace JustShowMe
{
    /// A face the user can name and allow. The filter assigns the stable Id.
    public class DetectedFace : INotifyPropertyChanged
    {
        private string _name;
        private string _notes;

        public int Id { get; set; }
        public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
        public string Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }

        public DateTime DateAdded { get; set; }
        public DateTime LastSeen { get; set; }
        public ImageSource FaceImage { get; set; }
        public bool ShouldDelete { get; set; }
        public bool IsExistingFace { get; set; }

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
