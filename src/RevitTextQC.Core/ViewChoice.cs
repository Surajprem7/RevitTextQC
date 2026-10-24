using System.ComponentModel;

namespace RevitTextQC.Core
{
    /// <summary>A view or sheet the user can tick in the "selected views / sheets" scope.</summary>
    public class ViewChoice : INotifyPropertyChanged
    {
        private bool _isChecked;

        public long Id { get; set; }

        /// <summary>Sheet number and name, or the view name.</summary>
        public string Name { get; set; }

        /// <summary>"Sheet", "Floor Plan", "Section", "Schedule"...</summary>
        public string Kind { get; set; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
