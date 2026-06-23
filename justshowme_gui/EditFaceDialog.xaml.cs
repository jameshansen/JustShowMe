using System.Windows;

namespace JustShowMe
{
    public partial class EditFaceDialog : Window
    {
        private readonly DetectedFace _face;

        public EditFaceDialog(DetectedFace face)
        {
            InitializeComponent();
            _face = face;
            DataContext = _face;
            Loaded += (s, e) =>
                DeleteButton.Visibility = _face.IsExistingFace ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Back_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
        private void Finish_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show($"Delete '{_face.Name}' from the allowed list?", "Confirm Delete",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _face.ShouldDelete = true;
                DialogResult = true;
                Close();
            }
        }
    }
}
