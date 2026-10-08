using System.Text.Json;
using Northpass.Models;
using Northpass.Services;

namespace Northpass.ViewModels;

public sealed class ProfileEditorViewModel : ObservableObject
{
    private string _json, _error = "";
    private readonly ProfileStore _store;
    private readonly StrategyProfile? _original;
    public StrategyProfile? SavedProfile { get; private set; }
    public event Action? Saved;
    public string Json { get => _json; set => Set(ref _json, value); }
    public string Error { get => _error; private set => Set(ref _error, value); }
    public RelayCommand SaveCommand { get; }
    public ProfileEditorViewModel(ProfileStore store, StrategyProfile? original)
    {
        (_store, _original) = (store, original);
        _json = JsonSerializer.Serialize(original ?? new StrategyProfile
        {
            Id = "custom-" + Guid.NewGuid().ToString("N")[..8], Name = "New strategy", Description = "Configure verified Zapret2 arguments."
        }, ProfileValidation.JsonOptions);
        SaveCommand = new(Save);
    }
    private void Save()
    {
        try
        {
            var profile = ProfileValidation.Parse(Json);
            SavedProfile = _original is null ? _store.Save(profile) : _store.Update(_original, profile);
            Saved?.Invoke();
        }
        catch (Exception ex) { Error = ex.Message; }
    }
}
