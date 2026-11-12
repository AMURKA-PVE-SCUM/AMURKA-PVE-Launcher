# Каталог модов GitVerse

Лаунчер 2.1.29 получает каталог без токена:

`https://gitverse.ru/api/repos/DOMO/amurka-pve-mods/raw/branch/main/mods.json`

Файлы читаются по неизменяемому коммиту из поля `revision`:

`https://gitverse.ru/api/repos/DOMO/amurka-pve-mods/raw/commit/<revision>/core/<filename>`

Формат: `schemaVersion: 1`, полный 40-символьный SHA коммита в `revision`,
полный список `{path, size, sha}` для `.pak` и `.sig`. `sha` — Git blob SHA-1,
не обычный SHA-1 файла. Большие PAK поддерживаются без ограничения GitHub в коде
клиента; сервер хранения должен отдавать реальные байты файла, а не Git LFS pointer.

## Публикация обновления модов

1. В локальной копии **DOMO/amurka-pve-mods** на ветке `main` заменить связанные PAK/SIG и закоммитить изменения.
2. Запустить из этого репозитория лаунчера:

```powershell
./tools/mod-catalog/Publish-GitVerseModCatalog.ps1 -RepositoryPath E:/amurka-pve-mods -Publish
```

Утилита строит каталог из закоммиченного дерева Git, проверяет непустой набор,
имена, дубли и LFS pointers, создаёт отдельный коммит каталога и отправляет оба
коммита в GitVerse. Для администратора нужен обычный доступ Git на запись;
игрокам никакие данные авторизации не передаются. Без `-Publish` она только
создаёт локальный `mods.json` для проверки. Не оставляйте изменения PAK
незакоммиченными: каталог должен ссылаться на доступный коммит с точными байтами.

Старые `Publish-ModCatalog.ps1` и `update-mod-catalog.yml` оставлены для
обслуживания GitHub-зеркала. Они не публикуют каталог GitVerse. Автоматический
CI-процесс GitVerse этим изменением не устанавливается.
