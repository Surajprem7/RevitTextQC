using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RevitTextQC.Core;

namespace RevitTextQC.UI
{
    /// <summary>Lets the user tick the views and sheets to scan.</summary>
    public partial class ViewPickerWindow : Window
    {
        private readonly List<ViewChoice> _all;
        private readonly ICollectionView _view;

        public ViewPickerWindow(List<ViewChoice> choices)
        {
            Theme.Apply(this);
            InitializeComponent();
            _all = choices;
            _view = CollectionViewSource.GetDefaultView(choices);
            _view.Filter = o => Matches((ViewChoice)o);
            ChoicesGrid.ItemsSource = _view;
            UpdateCount();
        }

        /// <summary>Element ids of the ticked views and sheets.</summary>
        public List<long> ChosenIds => _all.Where(c => c.IsChecked).Select(c => c.Id).ToList();

        private bool Matches(ViewChoice choice)
        {
            string search = SearchBox == null ? "" : SearchBox.Text.Trim();
            if (search.Length == 0) return true;
            return choice.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                   || choice.Kind.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateCount()
        {
            CountText.Text = _all.Count(c => c.IsChecked) + " ticked";
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
        }

        private void OnRowChecked(object sender, RoutedEventArgs e)
        {
            UpdateCount();
        }

        // Ticks only what the search currently shows, so "Tick shown" after typing "FCU" picks just those.
        private void OnTickShown(object sender, RoutedEventArgs e)
        {
            foreach (ViewChoice choice in _view.Cast<ViewChoice>()) choice.IsChecked = true;
            UpdateCount();
        }

        private void OnClear(object sender, RoutedEventArgs e)
        {
            foreach (ViewChoice choice in _all) choice.IsChecked = false;
            UpdateCount();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
