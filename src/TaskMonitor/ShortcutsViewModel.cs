using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TaskMonitor.Core;

namespace TaskMonitor;

/// <summary>바로가기 한 개 (화면용)</summary>
public sealed class ShortcutVm : Observable
{
    public ShortcutItem Item { get; }
    private ImageSource? _icon;
    private bool _exists = true;

    public ShortcutVm(ShortcutItem item) => Item = item;

    public string Id => Item.Id;

    public string Name
    {
        get => Item.Name;
        set
        {
            string v = (value ?? "").Trim();
            if (v.Length == 0 || v == Item.Name) return;
            Item.Name = v;
            Raise();
            Raise(nameof(Tooltip));
            Changed?.Invoke();
        }
    }

    public string? Args
    {
        get => Item.Args;
        set
        {
            string? v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (v == Item.Args) return;
            Item.Args = v;
            Raise();
            Changed?.Invoke();
        }
    }

    public string KindLabel => Item.Kind switch
    {
        ShortcutKind.App => "앱",
        ShortcutKind.Folder => "폴더",
        ShortcutKind.File => "파일",
        ShortcutKind.Url => "웹 주소",
        ShortcutKind.Shortcut => "바로가기(.lnk)",
        _ => "",
    };

    public bool CanEditArgs => Item.Kind == ShortcutKind.App;
    public string PathText => Item.Kind == ShortcutKind.Shortcut ? $"{Item.Lnk}  →  {Item.Target}" : Item.Target;
    public ImageSource? Icon { get => _icon; set { _icon = value; Raise(); Raise(nameof(HasIcon)); Raise(nameof(NoIcon)); } }
    public bool HasIcon => _icon is not null;
    public bool NoIcon => _icon is null;
    public bool Exists { get => _exists; set { if (Set(ref _exists, value)) { Raise(nameof(Opacity)); Raise(nameof(Tooltip)); } } }
    public double Opacity => _exists ? 1.0 : 0.4;
    public string Tooltip => $"{Item.Name}\n{PathText}" + (_exists ? "" : "\n⚠ 경로가 없습니다 (눌러도 실행하지 않음)");

    /// <summary>이름·인자 수정 시 저장 요청</summary>
    public event Action? Changed;
}

/// <summary>바로가기 목록: 불러오기·추가·순서·제거·실행. 바와 작업 창이 같은 인스턴스를 씀.</summary>
public sealed class ShortcutsController : Observable
{
    private ShortcutDoc _doc = new();
    private const int IconPx = 32;

    public ObservableCollection<ShortcutVm> Items { get; } = new();

    public string CountText => Items.Count == 0 ? "앱" : $"앱 {Items.Count}";

    /// <summary>패널을 열어 둔 채로 고정 (바깥을 눌러도 닫히지 않음)</summary>
    public bool Pinned
    {
        get => _doc.Expanded;
        set { if (_doc.Expanded == value) return; _doc.Expanded = value; Raise(); Save(); }
    }

    public void Load()
    {
        _doc = ShortcutStore.Load();
        Items.Clear();
        foreach (var it in _doc.Items.OrderBy(i => i.Order)) Items.Add(MakeVm(it));
        Raise(nameof(CountText));
        Raise(nameof(Pinned));
    }

    private ShortcutVm MakeVm(ShortcutItem it)
    {
        var vm = new ShortcutVm(it) { Exists = ShortcutStore.TargetExists(it) };
        vm.Icon = LoadIcon(it);
        vm.Changed += Save;
        return vm;
    }

    /// <summary>대상 존재 여부만 다시 확인 (폴더가 생기거나 지워졌을 때)</summary>
    public void RefreshExists()
    {
        foreach (var vm in Items) vm.Exists = ShortcutStore.TargetExists(vm.Item);
    }

    private static ImageSource? LoadIcon(ShortcutItem it)
    {
        try
        {
            double scale = 1.0;
            if (Application.Current?.MainWindow is { } w && PresentationSource.FromVisual(w)?.CompositionTarget is { } ct)
                scale = ct.TransformToDevice.M11;
            int px = (int)Math.Ceiling(IconPx * Math.Max(1, scale));
            var p = ShellIcons.Get(ShortcutStore.IconPath(it), px);
            if (p is null) return null;
            var bmp = BitmapSource.Create(p.Width, p.Height, 96, 96, PixelFormats.Pbgra32, null, p.Bgra, p.Width * 4);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>경로·주소 여러 개 추가. 등록하지 못한 것은 돌려줌.</summary>
    public List<string> Add(IEnumerable<string> inputs)
    {
        var failed = new List<string>();
        foreach (var s in inputs)
        {
            var it = ShortcutStore.CreateFrom(s);
            if (it is null) { failed.Add(s); continue; }
            it.Order = (_doc.Items.Count == 0 ? 0 : _doc.Items.Max(i => i.Order)) + 10;
            _doc.Items.Add(it);
            Items.Add(MakeVm(it));
        }
        Save();
        Raise(nameof(CountText));
        return failed;
    }

    public void Remove(ShortcutVm vm)
    {
        _doc.Items.RemoveAll(i => i.Id == vm.Id);
        Items.Remove(vm);
        Save();
        Raise(nameof(CountText));
    }

    public void Move(ShortcutVm vm, int delta)
    {
        int i = Items.IndexOf(vm), j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        Items.Move(i, j);
        for (int k = 0; k < Items.Count; k++) Items[k].Item.Order = k * 10;
        Save();
    }

    public string? Launch(ShortcutVm vm)
    {
        vm.Exists = ShortcutStore.TargetExists(vm.Item);
        return ShortcutStore.Launch(vm.Item);
    }

    public void Save()
    {
        _doc.Items = Items.Select(v => v.Item).ToList();
        try { ShortcutStore.Save(_doc); }
        catch (Exception ex) { MessageBox.Show("바로가기를 저장하지 못했습니다: " + ex.Message, "TaskMonitor"); }
    }
}
