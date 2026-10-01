using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Contracts.Indi;
using ELink.Core;
using ELink.UI.Infrastructure;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.UI.ViewModels;

public sealed partial class IndiElementRow : ObservableObject
{
    public IndiElementRow(string id, string label) { Id = id; Label = label; }
    public string Id { get; }
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _value = "";
    /// <summary>What the user is typing for a writable text/number element.</summary>
    [ObservableProperty] private string _edit = "";
    [ObservableProperty] private bool _on;
    /// <summary>This element is a switch the user may press.</summary>
    [ObservableProperty] private bool _canSwitch;
}

public sealed partial class IndiPropertyRow : ObservableObject
{
    public IndiPropertyRow(string device, string name) { Device = device; Name = name; }
    public string Device { get; }
    public string Name { get; }
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _group = "";
    [ObservableProperty] private string _type = "";
    [ObservableProperty] private string _state = "Idle";
    [ObservableProperty] private string _permission = "ro";
    public ObservableCollection<IndiElementRow> Elements { get; } = new();
    public bool IsSwitch => Type == "Switch";
    public bool IsEditable => Permission != "ro" && Type is "Text" or "Number";
    public bool IsSwitchEditable => Permission != "ro" && Type == "Switch";
    public string Header => $"{(Label == "" ? Name : Label)}   [{Name}]";
    public void RaiseKindChanged() { OnPropertyChanged(nameof(IsSwitch)); OnPropertyChanged(nameof(IsEditable)); OnPropertyChanged(nameof(IsSwitchEditable)); OnPropertyChanged(nameof(Header)); }
}

/// <summary>The generic window onto any INDI driver: every property of every device, live, and editable. Works for
/// whatever a bridge mirrors, with no knowledge of the driver. Uses only the Indi.&lt;server&gt;.* contract.</summary>
public sealed partial class IndiBrowserViewModel : ObservableObject, IDisposable
{
    private readonly MeshSession _mesh;
    private readonly Dictionary<string, IndiPropertyRow> _rows = new();
    private string? _hookedServer;
    private Action<IndiPropertyEvent>? _hook;

    public IndiBrowserViewModel(MeshSession mesh) { _mesh = mesh; }

    public ObservableCollection<string> Servers { get; } = new();
    public ObservableCollection<string> Devices { get; } = new();
    public ObservableCollection<IndiPropertyRow> Properties { get; } = new();
    [ObservableProperty] private string? _selectedServer;
    [ObservableProperty] private string? _selectedDevice;
    [ObservableProperty] private string _message = "";

    [RelayCommand]
    public async Task RefreshServersAsync()
    {
        var lists = await _mesh.Node.CallFunctionAsync<NOTESVoid, IndiServerList>(IndiIds.Servers, NOTESVoid.Void);
        var names = (lists ?? new()).SelectMany(l => l.Names).Select(n => n.Text).Distinct().Order().ToList();
        UiThread.Post(() =>
        {
            Servers.Clear(); foreach (var n in names) Servers.Add(n);
            SelectedServer ??= Servers.FirstOrDefault();
        });
    }

    partial void OnSelectedServerChanged(string? value) { if (value is not null) _ = FollowServerAsync(value); }
    partial void OnSelectedDeviceChanged(string? value) => ShowDevice();

    private async Task FollowServerAsync(string server)
    {
        if (_hookedServer is not null && _hook is not null)
        {
            try { _mesh.Node.UnhookEvent(IndiIds.Property(_hookedServer), _hook); } catch (ObjectDisposedException) { }
        }
        _hookedServer = server;
        UiThread.Post(() => { _rows.Clear(); Devices.Clear(); Properties.Clear(); });
        _hook = e => UiThread.Post(() => Apply(e));
        await _mesh.Node.HookEventAsync(IndiIds.Property(server), _hook, "INDI browser");
        var snaps = await _mesh.Node.CallFunctionAsync<NOTESVoid, IndiSnapshot>(IndiIds.Snapshot(server), NOTESVoid.Void);
        foreach (var snap in snaps ?? new())
            foreach (var p in snap.Properties)
                UiThread.Post(() => Apply(new IndiPropertyEvent { Kind = "Defined", Info = p }));
    }

    private void Apply(IndiPropertyEvent e)
    {
        var info = e.Info;
        string device = info.Device.Text, name = info.Property.Text;
        if (e.Kind.Text == "Deleted")
        {
            var gone = _rows.Where(r => r.Value.Device == device && (name == "" || r.Value.Name == name)).ToList();
            foreach (var g in gone) { _rows.Remove(g.Key); Properties.Remove(g.Value); }
            if (!_rows.Values.Any(r => r.Device == device)) Devices.Remove(device);
            return;
        }
        string key = device + "/" + name;
        if (!_rows.TryGetValue(key, out var row))
        {
            row = new IndiPropertyRow(device, name);
            _rows[key] = row;
            if (!Devices.Contains(device)) { Insert(Devices, device); SelectedDevice ??= device; }
            if (device == SelectedDevice) Properties.Add(row);
        }
        row.Label = info.Label.Text; row.Group = info.Group.Text; row.Type = info.Type.Text; row.State = info.State.Text; row.Permission = info.Permission.Text;
        foreach (var el in info.Elements)
        {
            var er = row.Elements.FirstOrDefault(x => x.Id == el.Id.Text);
            if (er is null) { er = new IndiElementRow(el.Id.Text, el.Label.Text); row.Elements.Add(er); er.Edit = el.Value.Text; }
            string display = row.Type == "Number" ? FormatNumber(el.Value.Text, el.Format.Text) : el.Value.Text;
            er.Value = display;
            er.On = row.Type == "Switch" && el.Value.Text == "On";
            er.CanSwitch = row.Permission != "ro" && row.Type == "Switch";
            if (row.Type == "Number" && er.Edit == "") er.Edit = el.Value.Text;
        }
        row.RaiseKindChanged();
    }

    private static string FormatNumber(string raw, string format)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return raw;
        return format.EndsWith('m') ? ELink.Core.Astro.Sexagesimal.Format(v, 1) : v.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private static void Insert(ObservableCollection<string> list, string item)
    {
        int i = 0; while (i < list.Count && string.CompareOrdinal(list[i], item) < 0) i++;
        list.Insert(i, item);
    }

    private void ShowDevice()
    {
        Properties.Clear();
        foreach (var r in _rows.Values.Where(r => r.Device == SelectedDevice).OrderBy(r => r.Group).ThenBy(r => r.Name)) Properties.Add(r);
    }

    private async Task SendAsync(IndiPropertyRow row, IEnumerable<(string id, string value)> values)
    {
        if (_hookedServer is null) return;
        var req = new IndiSetRequest { Device = row.Device, Property = row.Name };
        foreach (var (id, v) in values) req.Elements.Add(new IndiSetElement { Id = id, Value = v });
        var answers = await _mesh.Node.CallFunctionAsync<IndiSetRequest, IndiResult>(IndiIds.Set(_hookedServer), req);
        var r = answers?.FirstOrDefault();
        Message = r is null ? "no answer" : r.Ok.Value ? "" : r.Error.Text;
    }

    /// <summary>Send the edited values of a text/number property.</summary>
    [RelayCommand] private Task ApplyAsync(IndiPropertyRow? row) =>
        row is null ? Task.CompletedTask : SendAsync(row, row.Elements.Select(e => (e.Id, e.Edit)));

    /// <summary>Turn one switch of a switch property on.</summary>
    [RelayCommand] private Task SwitchOnAsync(IndiElementRow? el)
    {
        var row = _rows.Values.FirstOrDefault(r => r.Elements.Contains(el!));
        return row is null || el is null ? Task.CompletedTask : SendAsync(row, new[] { (el.Id, "On") });
    }

    public void Dispose()
    {
        if (_hookedServer is not null && _hook is not null)
        { try { _mesh.Node.UnhookEvent(IndiIds.Property(_hookedServer), _hook); } catch (ObjectDisposedException) { } }
    }
}
