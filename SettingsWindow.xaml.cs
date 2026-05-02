using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using Path = System.IO.Path;

namespace ClassicRadio;

public partial class SettingsWindow : Window
{
    private StationData _data = new();

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LoadData();
            RebindList();
        };
    }

    private List<Station> CurrentList =>
        SourceIdRadio.IsChecked == true ? _data.Indonesia : _data.International;

    // ====== Data load / save ======

    private void LoadData()
    {
        string? json = null;
        if (File.Exists(MainWindow.UserStationsPath))
        {
            try { json = File.ReadAllText(MainWindow.UserStationsPath); } catch { }
        }
        if (json is null && File.Exists(MainWindow.BundledStationsPath))
        {
            try { json = File.ReadAllText(MainWindow.BundledStationsPath); } catch { }
        }
        if (json is null) return;

        try
        {
            var doc = JsonSerializer.Deserialize<StationData>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (doc is not null) _data = doc;
        }
        catch (Exception ex)
        {
            StatusMsg.Text = $"Failed to read stations: {ex.Message}";
        }
    }

    private bool Persist()
    {
        try
        {
            Directory.CreateDirectory(MainWindow.UserDataDir);
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(MainWindow.UserStationsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            StatusMsg.Text = $"Save error: {ex.Message}";
            return false;
        }
    }

    // ====== List / form binding ======

    private void RebindList()
    {
        var sel = StationsList.SelectedItem;
        StationsList.ItemsSource = null;
        StationsList.ItemsSource = CurrentList;
        UpdateCountryVisibility();
        if (sel is Station s && CurrentList.Contains(s))
            StationsList.SelectedItem = s;
        else
            ClearForm();
    }

    private void UpdateCountryVisibility()
    {
        CountryPanel.Visibility = SourceIdRadio.IsChecked == true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ClearForm()
    {
        NameBox.Text = "";
        UrlBox.Text = "";
        CountryBox.Text = "";
    }

    private void PopulateForm(Station st)
    {
        NameBox.Text = st.Name;
        UrlBox.Text = st.Url;
        CountryBox.Text = st.Country ?? "";
    }

    // ====== Event handlers ======

    private void SourceChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RebindList();
        StatusMsg.Text = "";
    }

    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StationsList.SelectedItem is Station st)
            PopulateForm(st);
        else
            ClearForm();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var st = new Station
        {
            Name = "New Station",
            Url = "https://",
            Country = SourceIdRadio.IsChecked == true ? null : "Other",
        };
        CurrentList.Add(st);
        if (!Persist()) { CurrentList.Remove(st); return; }
        RebindList();
        StationsList.SelectedItem = st;
        StationsList.ScrollIntoView(st);
        NameBox.Focus();
        NameBox.SelectAll();
        StatusMsg.Text = "Added. Edit fields and click Save Changes.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (StationsList.SelectedItem is not Station st)
        {
            StatusMsg.Text = "Select a station first.";
            return;
        }
        var name = st.Name;
        CurrentList.Remove(st);
        if (!Persist()) { CurrentList.Add(st); return; }
        RebindList();
        StatusMsg.Text = $"Deleted: {name}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (StationsList.SelectedItem is not Station st)
        {
            StatusMsg.Text = "Select a station to edit.";
            return;
        }

        var name = NameBox.Text.Trim();
        var url  = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { StatusMsg.Text = "Name is required."; return; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            StatusMsg.Text = "URL must be a valid http(s) address.";
            return;
        }

        st.Name = name;
        st.Url  = url;
        if (SourceIntlRadio.IsChecked == true)
            st.Country = string.IsNullOrWhiteSpace(CountryBox.Text) ? "Other" : CountryBox.Text.Trim();
        else
            st.Country = null;

        if (!Persist()) return;

        // Refresh list so the (possibly new) name shows up
        var keep = st;
        StationsList.ItemsSource = null;
        StationsList.ItemsSource = CurrentList;
        StationsList.SelectedItem = keep;
        StatusMsg.Text = "Saved.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
