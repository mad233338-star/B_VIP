using System.Collections.ObjectModel;
using System.IO;
using B_VIP.Desktop.Mvvm;
using B_VIP.Desktop.Services.Catalog;

namespace B_VIP.Desktop.ViewModels
{
    /// <summary>
    /// The brand/model header shared by the vendor tabs. It filters <see cref="DeviceCatalogService"/>
    /// as the technician types, shows the SoC and engine the selected model uses, and raises
    /// <see cref="SelectionChanged"/> so the screen can bind the matching engine and loader file.
    /// </summary>
    public sealed class DeviceCatalogSelectorViewModel : ObservableObject
    {
        private readonly DeviceCatalogService _catalog;
        private bool _loaded;

        public DeviceCatalogSelectorViewModel(DeviceCatalogService catalog)
        {
            _catalog = catalog;
            Brands = new ObservableCollection<string> { AllBrands };
            Matches = new ObservableCollection<CatalogDevice>();
            _selectedBrand = AllBrands;
            _searchText = string.Empty;
        }

        /// <summary>The dropdown entry that shows every brand at once.</summary>
        public const string AllBrands = "All brands";

        /// <summary>Raised on the UI thread after the technician picks or clears a model.</summary>
        public event Action<CatalogDevice?>? SelectionChanged;

        public ObservableCollection<string> Brands { get; }

        /// <summary>Models matching the current brand and search text.</summary>
        public ObservableCollection<CatalogDevice> Matches { get; }

        private string _selectedBrand;

        public string SelectedBrand
        {
            get => _selectedBrand;
            set
            {
                if (!SetProperty(ref _selectedBrand, value))
                {
                    return;
                }

                // A model from another brand no longer applies once the brand changes.
                if (SelectedModel is { } previous &&
                    !string.Equals(previous.Brand, value, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value, AllBrands, StringComparison.Ordinal))
                {
                    SelectedModel = null;
                }

                RefreshMatches();
            }
        }

        private string _searchText;

        /// <summary>
        /// What the technician has typed. Every keystroke re-filters the dropdown, so typing "A06"
        /// narrows the list to the Galaxy A06 instead of the whole Samsung catalog.
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    RefreshMatches();
                }
            }
        }

        private CatalogDevice? _selectedModel;

        public CatalogDevice? SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (!SetProperty(ref _selectedModel, value, OnSelectedModelChanged))
                {
                    return;
                }
            }
        }

        /// <summary>The catalog's own explanation when it could not be read.</summary>
        public string? CatalogWarning => _catalog.Warning;

        public bool HasSelection => _selectedModel is not null;

        /// <summary>The engine that will service the selected handset, or a dash.</summary>
        public string PlatformText => _selectedModel?.Platform ?? "—";

        /// <summary>The SoC the catalog lists for the selected handset.</summary>
        public string ChipsetText => string.IsNullOrWhiteSpace(_selectedModel?.Chipset) ? "—" : _selectedModel.Chipset;

        /// <summary>
        /// The loader file the engine will receive. It only names a file that is really on disk;
        /// otherwise the engine's own default is used.
        /// </summary>
        public string LoaderText
        {
            get
            {
                string? resolved = LoaderPath;
                return resolved is null ? "Bundled default" : Path.GetFileName(resolved);
            }
        }

        /// <summary>Absolute loader path for the selected handset, or null when none is installed.</summary>
        public string? LoaderPath => _catalog.ResolveLoader(_selectedModel);

        /// <summary>One line for the console and the status bar.</summary>
        public string ModelSummary => _selectedModel is null
            ? "No model selected — the engine is chosen from the attached handset."
            : _selectedModel.Summary;

        /// <summary>Loads the catalog once and fills the brand dropdown.</summary>
        public async Task LoadAsync()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                await _catalog.LoadAsync();
            }
            catch (Exception)
            {
                // LoadAsync already records the reason for the screen; a failed catalog must not
                // take the vendor tab down with it.
            }

            UiThread.Invoke(() =>
            {
                Brands.Clear();
                Brands.Add(AllBrands);
                foreach (string brand in _catalog.Brands)
                {
                    Brands.Add(brand);
                }

                if (!Brands.Contains(_selectedBrand))
                {
                    _selectedBrand = AllBrands;
                    OnPropertyChanged(nameof(SelectedBrand));
                }

                OnPropertyChanged(nameof(CatalogWarning));
                RefreshMatches();
            });
        }

        /// <summary>Re-filters the model list against the brand and the typed text.</summary>
        private void RefreshMatches()
        {
            IReadOnlyList<CatalogDevice> matches = _catalog.Search(_selectedBrand, _searchText);

            // The catalog lists hundreds of handsets, so an edit that leaves the list unchanged
            // (a keystroke and its undo, a search that only refined nothing) must not rebuild it:
            // clearing a bound collection makes the ComboBox drop its selection and flicker.
            if (matches.Count == Matches.Count && matches.SequenceEqual(Matches))
            {
                return;
            }

            Matches.Clear();
            foreach (CatalogDevice device in matches)
            {
                Matches.Add(device);
            }

            // Keep the current selection only while it is still part of the filtered list.
            if (_selectedModel is { } selected && !matches.Contains(selected))
            {
                SelectedModel = null;
            }
        }

        private void OnSelectedModelChanged()
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(PlatformText));
            OnPropertyChanged(nameof(ChipsetText));
            OnPropertyChanged(nameof(LoaderText));
            OnPropertyChanged(nameof(LoaderPath));
            OnPropertyChanged(nameof(ModelSummary));

            SelectionChanged?.Invoke(_selectedModel);
        }
    }
}
