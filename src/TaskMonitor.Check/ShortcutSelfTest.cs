using TaskMonitor.Core;

/// <summary>
/// 바로가기 핵심 기능 검증 (실제로 앱을 실행하지는 않음).
///   TaskMonitor.Check.exe --shortcut-selftest &lt;출력 폴더&gt;
/// 종류 판별, .lnk/.url 읽기, 저장·백업 복구, 실행 정보, 아이콘 추출(BMP 저장)을 확인.
/// </summary>
internal static class ShortcutSelfTest
{
    private static int _fail;

    private static void Check(bool ok, string msg)
    {
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + msg);
        if (!ok) _fail++;
    }

    public static int Run(string outDir)
    {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        Paths.SetRoot(Path.Combine(outDir, "state"));
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string notepad = Path.Combine(win, "notepad.exe");
        if (!File.Exists(notepad)) notepad = Path.Combine(win, "System32", "notepad.exe");

        Console.WriteLine("[1] 종류 판별");
        var folder = ShortcutStore.CreateFrom(win);
        Check(folder?.Kind == ShortcutKind.Folder && folder.Name.Length > 0, $"폴더 → Folder ({folder?.Name})");
        var app = ShortcutStore.CreateFrom(notepad);
        Check(app?.Kind == ShortcutKind.App, $"exe → App ({app?.Name}, 작업 폴더 {app?.Workdir})");

        string lnk = Path.Combine(outDir, "메모장 테스트.lnk");
        ShellLinks.Create(lnk, notepad, "--demo-arg", win);
        var sc = ShortcutStore.CreateFrom(lnk);
        Check(sc?.Kind == ShortcutKind.Shortcut, $".lnk → Shortcut ({sc?.Name})");
        Check(string.Equals(sc?.Target, notepad, StringComparison.OrdinalIgnoreCase), $".lnk 대상 읽기 ({sc?.Target})");
        Check(sc?.Args == "--demo-arg" && string.Equals(sc?.Workdir, win, StringComparison.OrdinalIgnoreCase), $".lnk 인자·시작 폴더 ({sc?.Args}, {sc?.Workdir})");

        string urlFile = Path.Combine(outDir, "예제 사이트.url");
        File.WriteAllText(urlFile, "[InternetShortcut]\r\nURL=https://example.com/\r\n");
        var url1 = ShortcutStore.CreateFrom(urlFile);
        Check(url1?.Kind == ShortcutKind.Url && url1.Target == "https://example.com/", $".url → Url ({url1?.Target})");
        var url2 = ShortcutStore.CreateFrom("https://example.com/path");
        Check(url2?.Kind == ShortcutKind.Url && url2.Name == "example.com", $"주소 문자열 → Url ({url2?.Name})");

        string txt = Path.Combine(outDir, "메모.txt");
        File.WriteAllText(txt, "hello");
        var file = ShortcutStore.CreateFrom(txt);
        Check(file?.Kind == ShortcutKind.File, $"일반 파일 → File ({file?.Name})");
        Check(ShortcutStore.CreateFrom(Path.Combine(outDir, "없는파일.exe")) is null, "없는 경로 → 등록 안 함");

        Console.WriteLine("[2] 대상 확인 · 실행 정보 (실행하지 않음)");
        var items = new[] { folder!, app!, sc!, url1!, url2!, file! };
        foreach (var it in items)
        {
            var psi = ShortcutStore.BuildStart(it);
            Check(ShortcutStore.TargetExists(it) && psi.UseShellExecute,
                  $"{it.Kind,-8} {it.Name} → {psi.FileName} {psi.Arguments}".TrimEnd());
        }
        var missing = new ShortcutItem { Id = "x", Kind = ShortcutKind.Folder, Target = Path.Combine(outDir, "지워진폴더") };
        Check(!ShortcutStore.TargetExists(missing) && ShortcutStore.Launch(missing) is string msg && msg.Contains("대상이 없습니다"),
              "없는 대상은 실행하지 않고 메시지 반환");

        Console.WriteLine("[2a] 웹 브라우저 선택 (실행하지 않음)");
        const string chrome = @"C:\Browser Test\chrome.exe";
        const string address = "https://example.com/path?q=a%20b&x=1#section";
        var chromeStart = ShortcutStore.BuildUrlStart(address, ShortcutBrowser.Chrome, chrome);
        Check(chromeStart.FileName == chrome && chromeStart.ArgumentList.SequenceEqual(new[] { address }),
              "Chrome 실행 파일과 웹 주소를 별도 인자로 전달");
        var systemStart = ShortcutStore.BuildUrlStart(address, ShortcutBrowser.SystemDefault, chrome);
        Check(systemStart.FileName == address && systemStart.ArgumentList.Count == 0, "Windows 기본 브라우저 선택");
        Check(ShortcutStore.BuildUrlStart(address, ShortcutBrowser.Chrome, null).FileName == address,
              "Chrome이 없으면 Windows 기본 브라우저로 연결");
        Check(ShortcutStore.BuildStart(url1!, ShortcutBrowser.SystemDefault).FileName == url1!.Target,
              ".url 파일에서 읽은 웹 주소에도 선택 적용");
        var internetShortcut = ShortcutStore.BuildUrlStart(url1.Target, ShortcutBrowser.Chrome, chrome);
        Check(internetShortcut.FileName == chrome && internetShortcut.ArgumentList.Single() == url1.Target,
              ".url 웹 주소를 Chrome으로 전달");
        Check(ShortcutStore.BuildStart(sc!, ShortcutBrowser.Chrome).FileName == lnk &&
              ShortcutStore.BuildStart(file!, ShortcutBrowser.Chrome).FileName == txt,
              ".lnk와 일반 파일은 원래 연결 유지");
        Check(ShortcutStore.BuildUrlStart("mailto:user@example.com", ShortcutBrowser.Chrome, chrome).FileName == "mailto:user@example.com",
              "웹 외 프로토콜은 원래 연결 유지");
        Check(new TaskMonitor.AppSettings().WebBrowser == ShortcutBrowser.Chrome, "초기 브라우저는 Chrome");
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.Settings, "{}");
        Check(TaskMonitor.AppSettings.Load().WebBrowser == ShortcutBrowser.Chrome, "기존 설정에 브라우저 항목이 없으면 Chrome");
        var settings = TaskMonitor.AppSettings.Load();
        settings.WebBrowser = ShortcutBrowser.SystemDefault;
        settings.Save();
        Check(TaskMonitor.AppSettings.Load().WebBrowser == ShortcutBrowser.SystemDefault, "브라우저 선택 저장·다시 읽기");

        Console.WriteLine("[3] 저장 · 백업 복구");
        var doc = new ShortcutDoc { Expanded = true };
        for (int k = 0; k < items.Length; k++) { items[k].Order = k * 10; doc.Items.Add(items[k]); }
        ShortcutStore.Save(doc);
        ShortcutStore.Save(doc); // 두 번째 저장에서 .bak 생성
        var back = ShortcutStore.Load();
        Check(back.Items.Count == items.Length && back.Items[2].Lnk == lnk && back.Items[2].Kind == ShortcutKind.Shortcut,
              $"다시 읽기 {back.Items.Count}개, 종류·.lnk 경로 유지");
        File.WriteAllText(ShortcutStore.FilePath, "{ 깨진 json");
        var recovered = ShortcutStore.Load();
        Check(recovered.Items.Count == items.Length, "본 파일이 깨지면 .bak 에서 복구");
        Console.WriteLine("      " + File.ReadAllText(ShortcutStore.FilePath + ".bak").Split('\n').FirstOrDefault(l => l.Contains("\"kind\""))?.Trim());

        Console.WriteLine("[4] 아이콘 추출 (BMP 저장)");
        int n = 0;
        foreach (var it in items)
        {
            string ip = ShortcutStore.IconPath(it);
            foreach (int size in new[] { 32, 48 })
            {
                var px = ShellIcons.Get(ip, size);
                string bmp = Path.Combine(outDir, $"icon_{n}_{it.Kind}_{size}.bmp");
                if (px is not null) ShellIcons.SaveBmp(px, bmp);
                // 주소만 있는 URL 은 아이콘 파일이 없으므로 화면에서 기본 지구본 아이콘을 씀 (네트워크로 받지 않음)
                bool expect = !(it.Kind == ShortcutKind.Url && it.Lnk is null);
                Check(expect ? px is not null && px.Width >= 16 : true,
                      $"{it.Kind,-8} {size}px → {(px is null ? (expect ? "실패" : "없음(기본 아이콘 사용)") : $"{px.Width}x{px.Height} {px.Method}")}  ({ip})");
            }
            n++;
        }

        Console.WriteLine(_fail == 0 ? "\n결과: 모두 통과" : $"\n결과: {_fail}개 실패");
        return _fail == 0 ? 0 : 1;
    }
}
