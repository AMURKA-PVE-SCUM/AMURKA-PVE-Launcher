# AMURKA PVE Launcher for SCUM

<div align="center">

![Version](https://img.shields.io/badge/version-2.1.22-00FF88?style=for-the-badge&logo=github)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-blue?style=for-the-badge&logo=windows)
![.NET](https://img.shields.io/badge/.NET-8.0%20LTS%20WPF-512BD4?style=for-the-badge&logo=dotnet)
![SCUM Server](https://img.shields.io/badge/Server-85.88.179.207%3A7004-orange?style=for-the-badge)

**Официальный лаунчер и менеджер модификаций для сервера AMURKA PVE в игре SCUM.**

[Скачать лаунчер](https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest) • [Discord сообщество](https://discord.gg/CApw7CYBtA) • [Сайт сервера](https://amurka-pve-scum.github.io/)

</div>

---

## ⚡ Особенности

- 🎮 **Автоматическое обнаружение игры**: Мгновенно находит путь установки SCUM через системный реестр Windows и библиотеки Steam (`libraryfolders.vdf`, `appmanifest_513710.acf`).
- 📦 **Синхронизация модов в 1 клик**: Загрузка актуального набора модификаций напрямую из репозитория GitHub ([amurka-pve-mods](https://github.com/AMURKA-PVE-SCUM/amurka-pve-mods)) с поддержкой докачки, многопоточности и подписей `.sig`.
- 🛡️ **Безопасная изоляция модов**: Установка и удаление файлов производятся **строго** в каталог `SCUM\SCUM\Content\Paks\~mods`, исключая повреждение файлов оригинальной игры.
- ⚙️ **Гибкий выбор графического API**: Удобный запуск игры в режимах **DirectX 11**, **DirectX 12** или **Vulkan** с оптимизированными аргументами (`-nobattleye`, `-skipfilestreamingchecks`, `-nohmd`, `-nosplash`).
- 🔄 **Автообновление**: Встроенная система фоновой проверки и безопасного обновления лаунчера через официальные GitHub Releases.
- 💬 **Discord Rich Presence**: Динамическое отображение статуса игры и адреса сервера в профиле Discord.
- 🔑 **Верификация игроков**: Автоматическое определение активного SteamID64 через реестр Steam/VDF для защиты и синхронизации с игровым сервером.
- 🎨 **Современный Cyberpunk UI**: Высокопроизводительный интерфейс на WPF с эффектами неонового свечения и интерактивными частицами.

---

## 🚀 Быстрый старт (для игроков)

### 1. Скачивание и установка
1. Перейдите в раздел [**Releases**](https://github.com/AMURKA-PVE-SCUM/AMURKA-PVE-Launcher/releases/latest).
2. Скачайте последнюю версию инсталлятора: `AMURKA-PVE-Launcher-Setup-2.1.22.exe`.
3. Запустите установщик. Программа автоматически создаст удобные ярлыки на **Рабочем столе** и в **меню «Пуск»**.

### 2. Запуск и настройка
1. Запустите **AMURKA PVE Launcher**.
2. Лаунчер автоматически определит папку с игрой. Если игра установлена в нестандартную директорию, нажмите кнопку **«📁»** и укажите папку, где расположен `SCUM.exe` (например, `D:\SteamLibrary\steamapps\common\SCUM`).
3. Выберите желаемые категории модификаций:
   - **Основные** — обязательный набор модов сервера.
   - **Скорость транспорта** — моды характеристик техники.
   - **Скины одежды** — кастомные текстуры и одежда.
   - **Скины на оружие** — модификации внешнего вида оружия.
   - **Скины на постройки** — строительные элементы и декор.
   - **Музыка** — музыкальные моды.
4. Нажмите **«СКАЧАТЬ ВЫБРАННОЕ»** (или кнопку быстрого обновления в появившемся баннере).
5. Выберите режим графики (**DX11**, **DX12**, **VULKAN**) и нажмите **«ЗАПУСТИТЬ ИГРУ»**.

---

## 🛠️ Разработка и сборка (для разработчиков)

### Требования
- **ОС**: Windows 10 / Windows 11 (x64)
- **SDK**: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) или новее
- **IDE**: Visual Studio 2022 (с компонентом «Разработка классических приложений .NET») или JetBrains Rider / VS Code
- **NSIS**: [Nullsoft Scriptable Install System 3.x](https://nsis.sourceforge.io/) (для сборки `.exe` инсталлятора)

### Структура проекта
```
amurka-launcher/
├── AmurkaLauncher.sln          # Файл решения Visual Studio
├── AmurkaLauncher/             # Исходный код C# .NET 8 WPF
│   ├── Controls/               # Кастомные элементы UI (ParticleCanvas)
│   ├── Models/                 # Модели данных (конфиг, моды, обновления)
│   ├── Services/               # Бизнес-логика:
│   │   ├── ScumDetectionService.cs       # Автопоиск SCUM и путей ~mods
│   │   ├── ModManagerService.cs         # Загрузка и удаление модов с GitHub
│   │   ├── GameLauncherService.cs       # Запуск процесса SCUM
│   │   ├── UpdateService.cs             # Проверка и установка обновлений
│   │   ├── LauncherVerificationService.cs# Определение SteamID и верификация
│   │   ├── DiscordRpcService.cs         # Discord Rich Presence
│   │   └── ConfigService.cs             # Сохранение настроек пользователя
│   ├── Resources/              # Иконки и ресурсы приложения
│   ├── MainWindow.xaml         # Разметка главного окна WPF
│   └── MainWindow.xaml.cs      # Логика пользовательского интерфейса
├── assets/                     # Иконки и графические материалы
├── dist/                       # Каталог готовых сборок и манифест launcher.json
├── installer.nsi               # Скрипт сборки инсталлятора NSIS
├── build.bat                   # Быстрая сборка Release версии
├── run.bat                     # Запуск лаунчера из командной строки
└── build-installer.bat         # Полная сборка автономного инсталлятора
```

### Сборка через консоль
```powershell
# Сборка проекта в конфигурации Release
dotnet build AmurkaLauncher.sln -c Release

# Публикация автономной сборки (Self-Contained single-file)
dotnet publish AmurkaLauncher/AmurkaLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o AmurkaLauncher/publish-standalone

# Сборка инсталлятора через NSIS (или запустите build-installer.bat)
makensis installer.nsi
```

---

## 🌐 Полезные ссылки сервера

- **IP сервера**: `85.88.179.207:7004`
- **Discord**: [discord.gg/CApw7CYBtA](https://discord.gg/CApw7CYBtA)
- **Официальный сайт**: [amurka-pve-scum.github.io](https://amurka-pve-scum.github.io/)
- **Голосование на WARGM**: [wargm.ru/server/77385/votes](https://wargm.ru/server/77385/votes)
- **Игровой магазин**: [wargm.ru/server/77385/shop](https://wargm.ru/server/77385/shop)

---

## 📄 Авторство

Разработано специально для сервера **AMURKA PVE**.  
Created by **DOMO**.
