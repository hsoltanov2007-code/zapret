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
        ["EngineSetup"] = ("Engine installation", "Установка движка", "Mühərrikin quraşdırılması"),
        ["SetupRequired"] = ("Consent required for engine setup", "Требуется согласие на установку", "Quraşdırma üçün razılıq lazımdır"),
        ["Downloading"] = ("Downloading reviewed engine…", "Загрузка проверенного движка…", "Yoxlanmış mühərrik endirilir…"),
        ["Verifying"] = ("Verifying integrity…", "Проверка целостности…", "Bütövlük yoxlanılır…"),
        ["Extracting"] = ("Installing verified components…", "Установка проверенных компонентов…", "Yoxlanmış komponentlər quraşdırılır…"),
        ["Ready"] = ("Engine ready", "Движок готов", "Mühərrik hazırdır"),
        ["SetupFailed"] = ("Engine setup failed — see diagnostics", "Ошибка установки — см. диагностику", "Quraşdırma uğursuz oldu — diaqnostikaya baxın"),
        ["SetupEngine"] = ("Set up engine / retry", "Установить движок / повторить", "Mühərriki quraşdır / təkrar et"),
        ["UpdateEngine"] = ("Install reviewed engine update", "Установить проверенное обновление движка", "Yoxlanmış mühərrik yeniləməsini quraşdır"),
        ["RollbackEngine"] = ("Roll back engine", "Откатить движок", "Mühərriki geri qaytar"),
        ["EngineConsent"] = ("Install the reviewed official Zapret2 Windows x64 engine? Northpass will download a pinned GitHub source archive or use the offline payload, verify SHA-256, and store components in protected Program Files. WinDivert requires administrator rights and may be blocked by Windows security policy. No Windows security settings will be changed. Configured strategies run with administrator rights; only reviewed bundled Lua is allowed. Installation does not guarantee that a strategy works on your network.", "Установить проверенный официальный движок Zapret2 для Windows x64? Northpass загрузит архив закреплённой Git-ревизии с GitHub или использует автономный пакет, проверит SHA-256 и сохранит файлы в защищённой папке Program Files. WinDivert требует прав администратора и может блокироваться политикой безопасности Windows. Настройки безопасности Windows не изменяются. Стратегии запускаются с правами администратора; разрешены только проверенные Lua-файлы движка. Установка не гарантирует работу стратегии в вашей сети.", "Yoxlanmış rəsmi Zapret2 Windows x64 mühərriki quraşdırılsın? Northpass sabitlənmiş GitHub arxivini endirəcək və ya oflayn paketi istifadə edəcək, SHA-256 yoxlayacaq və faylları qorunan Program Files qovluğunda saxlayacaq. WinDivert administrator hüquqları tələb edir və Windows təhlükəsizlik siyasəti ilə bloklana bilər. Windows təhlükəsizlik parametrləri dəyişməyəcək. Strategiyalar administrator hüquqları ilə işləyir; yalnız yoxlanmış paket Lua faylları icazəlidir. Quraşdırma strategiyanın şəbəkənizdə işləyəcəyinə zəmanət vermir."),
        ["EngineUpdateConsent"] = ("Install the revision reviewed into this Northpass build? The previous verified engine will remain available for rollback.", "Установить ревизию, проверенную для этой версии Northpass? Предыдущий проверенный движок сохранится для отката.", "Bu Northpass versiyası üçün yoxlanmış reviziya quraşdırılsın? Əvvəlki yoxlanmış mühərrik geri qaytarmaq üçün saxlanılacaq."),
        ["EngineRollbackConsent"] = ("Activate the previous reviewed engine? Its integrity will be checked again.", "Активировать предыдущий проверенный движок? Его целостность будет проверена повторно.", "Əvvəlki yoxlanmış mühərrik aktivləşdirilsin? Bütövlüyü yenidən yoxlanılacaq."),
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
        ["Privacy"] = ("No telemetry. Diagnostic requests run only when requested. Updates are opt-in; engine setup downloads reviewed files after consent.", "Без телеметрии. Диагностические запросы выполняются по нажатию. Проверка обновлений добровольна; движок загружается после согласия.", "Telemetriya yoxdur. Diaqnostik sorğular yalnız istəklə işləyir. Yeniləmələr könüllüdür; mühərrik razılıqdan sonra endirilir."),
        ["AboutText"] = ("Northpass 0.3 · independent Windows desktop application. Zapret2 is the initial external engine. A future C++ engine can implement IDpiEngine without changing the UI.", "Northpass 0.3 — независимое приложение Windows. Zapret2 — начальный внешний движок. Будущий C++ движок сможет реализовать IDpiEngine без замены интерфейса.", "Northpass 0.3 · müstəqil Windows tətbiqi. Zapret2 ilkin xarici mühərrikdir. Gələcək C++ mühərriki UI dəyişmədən IDpiEngine-i tətbiq edə bilər."),
        ["Licenses"] = ("Zapret2 © bol-van (MIT). WinDivert and .NET have separate terms. The offline installer includes reviewed engine files and third-party sources. See THIRD_PARTY_NOTICES.md.", "Zapret2 © bol-van (MIT). WinDivert и .NET имеют отдельные условия. Автономный установщик включает проверенный движок и исходники зависимостей. См. THIRD_PARTY_NOTICES.md.", "Zapret2 © bol-van (MIT). WinDivert və .NET üçün ayrıca şərtlər var. Oflayn quraşdırıcı yoxlanmış mühərrik fayllarını və mənbələri ehtiva edir. THIRD_PARTY_NOTICES.md-yə baxın."),
        ["AvailabilityNote"] = ("Automatic strategy selection and the native engine are not implemented in v0.3.", "Автоматический подбор стратегии и собственный движок не реализованы в v0.3.", "Avtomatik strategiya seçimi və yerli mühərrik v0.3-də həyata keçirilməyib."),
        ["Show"] = ("Show Northpass", "Открыть Northpass", "Northpass-ı aç"),
        ["Exit"] = ("Exit", "Выход", "Çıxış")
    };
}
