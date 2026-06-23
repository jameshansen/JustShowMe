using System.Collections.Generic;
using System.Windows;

namespace JustShowMe
{
    public partial class SelectFaceDialog : Window
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
            if (SelectedFace != null) { DialogResult = true; Close(); }
            else MessageBox.Show("Please select a face to continue.");
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    }
}
