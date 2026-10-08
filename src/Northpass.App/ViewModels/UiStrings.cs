namespace Northpass.ViewModels;

public sealed class UiStrings(string language)
{
    public string this[string key]
    {
        get
        {
            if (!Entries.TryGetValue(key, out var text)) return key;
            return language switch { "ru" => text.Ru, "az" => text.Az, _ => text.En };
        }
    }
    private static readonly Dictionary<string, (string En, string Ru, string Az)> Entries = new()
    {
        ["Dashboard"] = ("Dashboard", "Главная", "İdarə paneli"),
        ["Strategies"] = ("Strategies", "Стратегии", "Strategiyalar"),
        ["Diagnostics"] = ("Diagnostics", "Диагностика", "Diaqnostika"),
        ["Settings"] = ("Settings", "Настройки", "Parametrlər"),
        ["About"] = ("About", "О программе", "Haqqında"),
        ["Connect"] = ("CONNECT", "ВКЛЮЧИТЬ", "QOŞUL"),
        ["Disconnect"] = ("DISCONNECT", "ВЫКЛЮЧИТЬ", "AYRIL"),
        ["Disconnected"] = ("Disconnected", "Не запущен", "Qoşulmayıb"),
        ["Connecting"] = ("Connecting", "Запуск", "Qoşulur"),
        ["Active"] = ("Engine active", "Движок работает", "Mühərrik aktivdir"),
        ["Stopping"] = ("Stopping", "Остановка", "Dayandırılır"),
        ["Error"] = ("Error", "Ошибка", "Xəta"),
        ["Engine"] = ("Current engine", "Текущий движок", "Cari mühərrik"),
        ["Strategy"] = ("Current strategy", "Текущая стратегия", "Cari strategiya"),
        ["Session"] = ("Session duration", "Длительность сессии", "Sessiya müddəti"),
        ["Reachability"] = ("Website reachability", "Доступность сайта", "Saytın əlçatanlığı"),
        ["Unverified"] = ("Not tested", "Не проверена", "Yoxlanılmayıb"),
        ["Reachable"] = ("Reachable", "Доступен", "Əlçatandır"),
        ["Unreachable"] = ("Request failed", "Запрос не удался", "Sorğu uğursuz oldu"),
        ["StateNote"] = ("An active process does not prove a DPI bypass. Test a specific HTTPS URL in Diagnostics.", "Работа процесса не доказывает обход DPI. Проверьте конкретный HTTPS-адрес в диагностике.", "Aktiv proses DPI blokunun keçildiyini sübut etmir. Diaqnostikada konkret HTTPS ünvanını yoxlayın."),
        ["EnginePath"] = ("Engine executable", "Исполняемый файл движка", "Mühərrikin icra faylı"),
        ["Browse"] = ("Browse…", "Обзор…", "Seç…"),
        ["Validate"] = ("Validate configuration", "Проверить конфигурацию", "Konfiqurasiyanı yoxla"),
        ["New"] = ("New", "Добавить", "Yeni"),
        ["Edit"] = ("Edit JSON", "Изменить JSON", "JSON-u redaktə et"),
        ["Import"] = ("Import", "Импорт", "İdxal"),
        ["Export"] = ("Export", "Экспорт", "İxrac"),
        ["Refresh"] = ("Refresh", "Обновить", "Yenilə"),
        ["ProfileNote"] = ("Profiles are stored in your local application data. Empty drafts can be saved but cannot start the engine. Disconnect before changing strategies.", "Профили хранятся в локальных данных приложения. Пустой черновик можно сохранить, но нельзя запустить. Остановите движок перед сменой стратегии.", "Profillər yerli tətbiq məlumatlarında saxlanılır. Boş qaralamalar saxlanıla bilər, amma mühərriki başlatmır. Strategiyanı dəyişməzdən əvvəl ayrılın."),
        ["TestUrl"] = ("HTTPS test URL", "HTTPS-адрес для проверки", "HTTPS test ünvanı"),
        ["Test"] = ("Test connection", "Проверить соединение", "Bağlantını yoxla"),
        ["ExportLogs"] = ("Export logs", "Экспорт журнала", "Jurnalı ixrac et"),
        ["ClearLogs"] = ("Clear logs", "Очистить журнал", "Jurnalı təmizlə"),
        ["Language"] = ("Language", "Язык", "Dil"),
        ["Tray"] = ("Minimize to system tray", "Сворачивать в системный трей", "Sistem treyinə yığ"),
        ["AutoStart"] = ("Start with Windows (elevated scheduled task)", "Автозапуск с Windows (задача с повышенными правами)", "Windows ilə başlat (yüksək səlahiyyətli tapşırıq)"),
        ["Recovery"] = ("Recover after a crash (up to 3 attempts per session)", "Восстановление после сбоя (до 3 попыток за сессию)", "Qəzadan sonra bərpa (sessiyada ən çox 3 cəhd)"),
        ["Updates"] = ("Check for updates on startup (contacts GitHub)", "Проверять обновления при запуске (запрос к GitHub)", "Başlanğıcda yeniləmələri yoxla (GitHub sorğusu)"),
        ["Save"] = ("Save settings", "Сохранить настройки", "Parametrləri saxla"),
        ["CheckUpdates"] = ("Check updates now", "Проверить обновления", "Yeniləmələri yoxla"),
        ["Privacy"] = ("No telemetry. Diagnostic requests run only when requested. Updates are opt-in; no components are downloaded automatically.", "Без телеметрии. Диагностические запросы выполняются по нажатию. Проверка обновлений добровольна; компоненты автоматически не скачиваются.", "Telemetriya yoxdur. Diaqnostik sorğular yalnız istəklə işləyir. Yeniləmələr könüllüdür; komponentlər avtomatik endirilmir."),
        ["AboutText"] = ("Northpass 0.2 · independent Windows desktop application. Zapret2 is the initial external engine. A future C++ engine can implement IDpiEngine without changing the UI.", "Northpass 0.2 — независимое приложение Windows. Zapret2 — начальный внешний движок. Будущий C++ движок сможет реализовать IDpiEngine без замены интерфейса.", "Northpass 0.2 · müstəqil Windows tətbiqi. Zapret2 ilkin xarici mühərrikdir. Gələcək C++ mühərriki UI dəyişmədən IDpiEngine-i tətbiq edə bilər."),
        ["Licenses"] = ("Zapret2 © bol-van (MIT). WinDivert and .NET have separate terms. Engine binaries are not bundled. See THIRD_PARTY_NOTICES.md.", "Zapret2 © bol-van (MIT). WinDivert и .NET имеют отдельные условия. Движок не входит в поставку. См. THIRD_PARTY_NOTICES.md.", "Zapret2 © bol-van (MIT). WinDivert və .NET üçün ayrıca şərtlər var. Mühərrik faylları daxil deyil. THIRD_PARTY_NOTICES.md-yə baxın."),
        ["AvailabilityNote"] = ("Automatic strategy selection and the native engine are not implemented in v0.2.", "Автоматический подбор стратегии и собственный движок не реализованы в v0.2.", "Avtomatik strategiya seçimi və yerli mühərrik v0.2-də həyata keçirilməyib."),
        ["Show"] = ("Show Northpass", "Открыть Northpass", "Northpass-ı aç"),
        ["Exit"] = ("Exit", "Выход", "Çıxış")
    };
}
